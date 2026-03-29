using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.ComponentModel;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

using CUE4Parse.UE4.Assets.Exports.Texture;

using CUE4Parse_Conversion.Textures;

using FModel.Extensions;
using FModel.Settings;

using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;

using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

using Serilog;

namespace FModel.Services;

[McpServerToolType]
public class FModelMcpTools(McpServerHandler handler)
{
    private bool IsLoaded => handler.CUE4Parse?.Provider != null;
    private const string NotLoadedMsg = "No game is loaded in FModel. Open a game directory and load archives first.";

    // -------------------------------------------------------------------------
    // Status
    // -------------------------------------------------------------------------

    [McpServerTool]
    [Description("Get the current status of FModel: which game is loaded, how many files and archives are mounted, and localization count.")]
    public CallToolResult GetStatus()
    {
        if (!IsLoaded)
            return Text(JsonConvert.SerializeObject(new { loaded = false, message = NotLoadedMsg }, Formatting.Indented));

        var provider = handler.CUE4Parse.Provider;
        return Text(JsonConvert.SerializeObject(new
        {
            loaded = true,
            project = provider.ProjectName,
            game = provider.Versions.Game.ToString(),
            platform = provider.Versions.Platform.ToString(),
            file_count = provider.Files.Count,
            mounted_archive_count = provider.MountedVfs.Count,
            localization_count = handler.CUE4Parse.LocalizedResourcesCount
        }, Formatting.Indented));
    }

    // -------------------------------------------------------------------------
    // File navigation
    // -------------------------------------------------------------------------

    [McpServerTool]
    [Description("List asset files, optionally filtered by a path prefix and/or file extension. Returns paths and sizes. Paginated via limit/offset.")]
    public CallToolResult ListFiles(
        [Description("Directory prefix to filter by (e.g. 'FortniteGame/Content/Athena'). Leave empty for all files.")] string path_prefix = "",
        [Description("File extension without dot to filter by (e.g. 'uasset', 'umap', 'bnk'). Empty for all.")] string extension = "",
        [Description("Maximum number of results.")] int limit = 100,
        [Description("Number of results to skip for pagination.")] int offset = 0)
    {
        if (!IsLoaded) return Error(NotLoadedMsg);

        var files = handler.CUE4Parse.Provider.Files.Values.AsEnumerable();

        if (!string.IsNullOrEmpty(path_prefix))
            files = files.Where(f => f.Path.StartsWith(path_prefix, StringComparison.OrdinalIgnoreCase));

        if (!string.IsNullOrEmpty(extension))
            files = files.Where(f => f.Path.EndsWith('.' + extension, StringComparison.OrdinalIgnoreCase));

        var total = files.Count();
        var page = files.Skip(offset).Take(limit)
            .Select(f => new { path = f.Path, size = f.Size })
            .ToList();

        return Text(JsonConvert.SerializeObject(new { total, offset, limit, count = page.Count, files = page }, Formatting.Indented));
    }

    [McpServerTool]
    [Description("Get a hierarchical directory tree for a given path. Use depth to control how many levels to expand.")]
    public CallToolResult FileTree(
        [Description("Root path to start from (e.g. 'FortniteGame/Content'). Empty for the full root.")] string path = "",
        [Description("Maximum depth of the tree to return.")] int depth = 3)
    {
        if (!IsLoaded) return Error(NotLoadedMsg);

        depth = Math.Clamp(depth, 1, 6);
        var prefix = path.TrimEnd('/');
        var prefixWithSlash = string.IsNullOrEmpty(prefix) ? "" : prefix + "/";

        var keys = handler.CUE4Parse.Provider.Files.Keys
            .Where(k => string.IsNullOrEmpty(prefix) || k.StartsWith(prefixWithSlash, StringComparison.OrdinalIgnoreCase))
            .ToList();

        var tree = BuildTree(keys, prefix, depth);
        return Text(JsonConvert.SerializeObject(
            new { path = string.IsNullOrEmpty(prefix) ? "/" : prefix, file_count = keys.Count, tree },
            Formatting.Indented));
    }

    [McpServerTool]
    [Description("Search for asset paths by name or pattern. Supports plain substring or full regex matching.")]
    public CallToolResult SearchAssets(
        [Description("Search term (substring match or regex pattern).")] string query,
        [Description("Enable regex pattern matching.")] bool use_regex = false,
        [Description("Case-sensitive matching.")] bool case_sensitive = false,
        [Description("Maximum number of results.")] int limit = 50)
    {
        if (!IsLoaded) return Error(NotLoadedMsg);
        if (string.IsNullOrWhiteSpace(query)) return Error("query cannot be empty.");

        var comparison = case_sensitive ? StringComparison.Ordinal : StringComparison.OrdinalIgnoreCase;
        Func<string, bool> matcher;

        if (use_regex)
        {
            try
            {
                var opts = RegexOptions.Compiled | (case_sensitive ? RegexOptions.None : RegexOptions.IgnoreCase);
                var re = new Regex(query, opts);
                matcher = re.IsMatch;
            }
            catch (Exception ex)
            {
                return Error($"Invalid regex: {ex.Message}");
            }
        }
        else
        {
            matcher = s => s.Contains(query, comparison);
        }

        var results = handler.CUE4Parse.Provider.Files
            .Where(kv => matcher(kv.Key))
            .Take(limit)
            .Select(kv => new { path = kv.Key, size = kv.Value.Size })
            .ToList();

        return Text(JsonConvert.SerializeObject(new { query, count = results.Count, results }, Formatting.Indented));
    }

    [McpServerTool]
    [Description("List all mounted game archives (pak/utoc/ucas files) with their names, paths, and file counts.")]
    public CallToolResult ListArchives()
    {
        if (!IsLoaded) return Error(NotLoadedMsg);

        var archives = handler.CUE4Parse.Provider.MountedVfs
            .Select(vfs => new
            {
                name = vfs.Name,
                path = vfs.Path,
                file_count = vfs.FileCount
            })
            .ToList();

        return Text(JsonConvert.SerializeObject(new { count = archives.Count, archives }, Formatting.Indented));
    }

    // -------------------------------------------------------------------------
    // Asset reading
    // -------------------------------------------------------------------------

    private const int ReadAssetMaxBytes = 300_000;

    [McpServerTool]
    [Description("""
        Load a game asset (.uasset / .umap) and return its property data as JSON.

        Filtering options (applied before the size cap):
        • jpath  — JSONPath expression to extract a subset, e.g. "$[0].Rows.1048" for one DataTable row,
                   "$[0].Properties" for just the top-level properties, or "$[0].Rows.*" for all row values.
                   Uses Newtonsoft SelectTokens — supports wildcards, recursive descent (..), filters (?(@.Role=='EHeroRole::Damage')), etc.
        • grep   — Case-insensitive substring or regex kept from the serialized JSON lines.
                   When set, only lines containing the match are returned (with ±context_lines of context).

        Without jpath/grep the response is capped at ~300 KB with a truncation notice.
        With jpath the full selected subset is returned (no cap).
        """)]
    public async Task<CallToolResult> ReadAssetJson(
        [Description("Full asset path. Extension is optional — will try .uasset and .umap if omitted.")] string asset_path,
        [Description("JSONPath expression to select a subset of the asset JSON. Empty = return everything.")] string jpath = "",
        [Description("Substring or regex to filter output lines (case-insensitive). Empty = no filter.")] string grep = "",
        [Description("Lines of context to include around each grep match.")] int context_lines = 2)
    {
        if (!IsLoaded) return Error(NotLoadedMsg);

        try
        {
            var entry = ResolveEntry(asset_path, ".uasset", ".umap");
            if (entry == null) return Error($"File not found: {asset_path}");

            var result = await Task.Run(() => handler.CUE4Parse.Provider.GetLoadPackageResult(entry));
            var data = result.GetDisplayData(save: true);
            // Serialize with Newtonsoft — MCP SDK uses System.Text.Json which would mangle JToken internals
            var json = JsonConvert.SerializeObject(data, Formatting.Indented);

            // --- jpath filter ---
            if (!string.IsNullOrWhiteSpace(jpath))
            {
                try
                {
                    var root = JToken.Parse(json);

                    // Newtonsoft can't filter object children with [?()] — e.g. "$[0].Rows[?(@.Role=='X')]"
                    // fails because Rows is a JObject, not a JArray.
                    // Workaround: if the token at the path prefix is a JObject, wrap its values in a
                    // temporary JArray, re-run the filter on that, then return the matching values.
                    IEnumerable<JToken> selected;
                    var filterMatch = Regex.Match(jpath, @"^(.*?)(\[\?\(.*\)\])$");
                    if (filterMatch.Success)
                    {
                        var basePath = filterMatch.Groups[1].Value.TrimEnd('.');
                        var filter = filterMatch.Groups[2].Value;
                        var baseToken = string.IsNullOrEmpty(basePath)
                            ? root
                            : root.SelectToken(basePath);
                        if (baseToken is JObject obj)
                        {
                            var arr = new JArray(obj.PropertyValues());
                            selected = arr.SelectTokens("$" + filter);
                        }
                        else
                        {
                            selected = root.SelectTokens(jpath);
                        }
                    }
                    else
                    {
                        selected = root.SelectTokens(jpath);
                    }

                    var list = selected.ToList();
                    json = list.Count == 1
                        ? list[0].ToString(Formatting.Indented)
                        : new JArray(list).ToString(Formatting.Indented);
                }
                catch (Exception ex)
                {
                    return Error($"Invalid jpath '{jpath}': {ex.Message}");
                }

                // grep on jpath result, then return without size cap
                if (!string.IsNullOrWhiteSpace(grep))
                    json = GrepLines(json, grep, context_lines);

                return Text(json);
            }

            // --- grep only (no jpath) ---
            if (!string.IsNullOrWhiteSpace(grep))
                return Text(GrepLines(json, grep, context_lines));

            // --- plain return with size cap ---
            if (json.Length > ReadAssetMaxBytes)
                return Text(json[..ReadAssetMaxBytes] +
                       $"\n\n/* OUTPUT TRUNCATED at {ReadAssetMaxBytes} bytes (full size: {json.Length} bytes). " +
                       "Use the jpath parameter to select a specific subset. */");

            return Text(json);
        }
        catch (Exception ex)
        {
            Log.Error(ex, "[MCP] ReadAssetJson failed for '{AssetPath}'", asset_path);
            return Error(ex.Message);
        }
    }

    private static string GrepLines(string text, string pattern, int context)
    {
        Regex re;
        try { re = new Regex(pattern, RegexOptions.IgnoreCase | RegexOptions.Compiled); }
        catch { re = new Regex(Regex.Escape(pattern), RegexOptions.IgnoreCase | RegexOptions.Compiled); }

        var lines = text.Split('\n');
        var keep = new SortedSet<int>();
        for (var i = 0; i < lines.Length; i++)
            if (re.IsMatch(lines[i]))
                for (var j = Math.Max(0, i - context); j <= Math.Min(lines.Length - 1, i + context); j++)
                    keep.Add(j);

        if (keep.Count == 0) return "/* no matches */";

        var sb = new StringBuilder();
        int prev = -2;
        foreach (var idx in keep)
        {
            if (idx > prev + 1) sb.AppendLine("...");
            sb.AppendLine(lines[idx]);
            prev = idx;
        }
        return sb.ToString().TrimEnd();
    }

    [McpServerTool]
    [Description("Get a summary of all exports inside a package (their names, class types, and outer objects) without fully deserializing them. Useful for quickly inspecting what's in a file.")]
    public CallToolResult GetAssetExports(
        [Description("Full asset path (extension optional).")] string asset_path)
    {
        if (!IsLoaded) return Error(NotLoadedMsg);

        try
        {
            var entry = ResolveEntry(asset_path, ".uasset", ".umap");
            if (entry == null) return Error($"File not found: {asset_path}");

            var pkg = handler.CUE4Parse.Provider.LoadPackage(entry);
            var exports = pkg.GetExports()
                .Select((e, i) => new
                {
                    index = i,
                    name = e.Name,
                    class_type = e.ExportType,
                    outer = e.Outer?.Name
                })
                .ToList();

            return Text(JsonConvert.SerializeObject(new { path = entry.Path, export_count = exports.Count, exports }, Formatting.Indented));
        }
        catch (Exception ex)
        {
            Log.Error(ex, "[MCP] GetAssetExports failed for '{AssetPath}'", asset_path);
            return Error(ex.Message);
        }
    }

    [McpServerTool]
    [Description("Read the raw bytes of any file in the game archives. For text files (INI, JSON, CSV, etc.) the content is returned as a UTF-8 string. Binary files are returned as base64.")]
    public CallToolResult GetRawFile(
        [Description("Full file path including extension.")] string file_path)
    {
        if (!IsLoaded) return Error(NotLoadedMsg);

        try
        {
            if (!handler.CUE4Parse.Provider.Files.TryGetValue(file_path, out var entry))
                return Error($"File not found: {file_path}");

            var data = entry.Read();

            // Heuristic: if every byte is 7-bit ASCII or common whitespace treat it as text
            var isText = data.Length > 0 && data.Take(Math.Min(512, data.Length)).All(b => b < 128);
            if (isText)
                return Text(JsonConvert.SerializeObject(
                    new { path = file_path, size = data.Length, is_text = true, content = Encoding.UTF8.GetString(data) },
                    Formatting.Indented));

            return Text(JsonConvert.SerializeObject(
                new { path = file_path, size = data.Length, is_text = false, content_base64 = Convert.ToBase64String(data) },
                Formatting.Indented));
        }
        catch (Exception ex)
        {
            Log.Error(ex, "[MCP] GetRawFile failed for '{FilePath}'", file_path);
            return Error(ex.Message);
        }
    }

    // -------------------------------------------------------------------------
    // Texture extraction
    // -------------------------------------------------------------------------

    [McpServerTool]
    [Description("Extract a UTexture2D (or UTextureCube, etc.) and return it as a PNG image with metadata. The response contains both a text block (width, height, format) and an image content block.")]
    public async Task<CallToolResult> ExtractTexture(
        [Description("Full path to the texture asset (extension optional).")] string asset_path)
    {
        if (!IsLoaded) return Error(NotLoadedMsg);

        try
        {
            var entry = ResolveEntry(asset_path, ".uasset");
            if (entry == null) return Error($"File not found: {asset_path}");

            return await Task.Run(() =>
            {
                var pkg = handler.CUE4Parse.Provider.LoadPackage(entry);
                var texture = pkg.GetExports().OfType<UTexture>().FirstOrDefault();

                if (texture == null)
                    return Error("No UTexture export found in this asset.");

                var platform = UserSettings.Default.CurrentDir?.TexturePlatform
                               ?? ETexturePlatform.DesktopMobile;
                var decoded = texture.Decode(platform);
                if (decoded == null)
                    return Error("Failed to decode texture — unsupported format or missing data.");

                var pngBytes = decoded.Encode(ETextureFormat.Png, false, out _);

                var meta = $"path: {entry.Path}\nwidth: {decoded.Width}\nheight: {decoded.Height}\nmip_count: {texture.PlatformData.Mips.Length}\npixel_format: {texture.Format}";

                return new CallToolResult
                {
                    Content =
                    [
                        new TextContentBlock { Text = meta },
                        ImageContentBlock.FromBytes(pngBytes, "image/png")
                    ]
                };
            });
        }
        catch (Exception ex)
        {
            Log.Error(ex, "[MCP] ExtractTexture failed for '{AssetPath}'", asset_path);
            return Error(ex.Message);
        }
    }

    // -------------------------------------------------------------------------
    // Localization
    // -------------------------------------------------------------------------

    [McpServerTool]
    [Description("Browse or search localization strings loaded by FModel. Filter by namespace and/or key substring to narrow results.")]
    public CallToolResult ReadLocalization(
        [Description("Namespace substring to filter (e.g. 'Athena_Cosmetics'). Empty = all namespaces.")] string namespace_filter = "",
        [Description("Key substring to search for. Empty = all keys.")] string key_filter = "",
        [Description("Value substring to search for. Empty = all values.")] string value_filter = "",
        [Description("Maximum number of results.")] int limit = 100)
    {
        if (!IsLoaded) return Error(NotLoadedMsg);

        var results = new List<object>();
        var intern = handler.CUE4Parse.Provider.Internationalization;

        foreach (var (ns, table) in intern)
        {
            if (!string.IsNullOrEmpty(namespace_filter) &&
                !ns.Contains(namespace_filter, StringComparison.OrdinalIgnoreCase))
                continue;

            foreach (var (key, value) in table)
            {
                if (!string.IsNullOrEmpty(key_filter) &&
                    !key.Contains(key_filter, StringComparison.OrdinalIgnoreCase))
                    continue;

                if (!string.IsNullOrEmpty(value_filter) &&
                    !value.Contains(value_filter, StringComparison.OrdinalIgnoreCase))
                    continue;

                results.Add(new { @namespace = ns, key, value });
                if (results.Count >= limit) break;
            }

            if (results.Count >= limit) break;
        }

        return Text(JsonConvert.SerializeObject(new { count = results.Count, limit, results }, Formatting.Indented));
    }

    // -------------------------------------------------------------------------
    // Full-text content search
    // -------------------------------------------------------------------------

    [McpServerTool]
    [Description("""
        Full-text search across the raw content of all game files.
        Scans raw bytes for the query string in both UTF-8 and UTF-16LE encodings,
        so it finds property names, string table entries, object references, etc.
        without needing to deserialize every asset.

        Use path_prefix and extension filters to narrow the search scope — searching
        the entire game can take minutes for large titles.
        """)]
    public async Task<CallToolResult> SearchContent(
        [Description("The text string to search for inside file contents.")] string query,
        [Description("Directory prefix to narrow the search (e.g. 'FortniteGame/Content/Athena'). Highly recommended for large games.")] string path_prefix = "",
        [Description("File extension without dot to filter by (e.g. 'uasset', 'umap', 'bin'). Empty searches all files.")] string extension = "",
        [Description("Case-sensitive matching. Default false.")] bool case_sensitive = false,
        [Description("Maximum number of matching files to return.")] int limit = 50,
        [Description("Maximum number of files to scan before stopping (0 = unlimited). Use to cap search time on huge games.")] int max_scan = 0)
    {
        if (!IsLoaded) return Error(NotLoadedMsg);
        if (string.IsNullOrWhiteSpace(query)) return Error("query cannot be empty.");
        if (query.Length < 2) return Error("query must be at least 2 characters.");

        return await Task.Run(() =>
        {
            var files = handler.CUE4Parse.Provider.Files.Values.AsEnumerable();

            if (!string.IsNullOrEmpty(path_prefix))
                files = files.Where(f => f.Path.StartsWith(path_prefix, StringComparison.OrdinalIgnoreCase));
            if (!string.IsNullOrEmpty(extension))
                files = files.Where(f => f.Path.EndsWith('.' + extension, StringComparison.OrdinalIgnoreCase));

            var fileList = files.ToList();
            var totalFiles = fileList.Count;
            if (max_scan > 0 && fileList.Count > max_scan)
                fileList = fileList.Take(max_scan).ToList();

            // Prepare search needles — for case-insensitive we store both lower and upper variants
            var utf8Needle = Encoding.UTF8.GetBytes(case_sensitive ? query : query.ToLowerInvariant());
            var utf16Needle = Encoding.Unicode.GetBytes(case_sensitive ? query : query.ToLowerInvariant());

            // Build case-insensitive lookup tables for the first byte of each needle
            // so we can compare without allocating a lowered copy of every file
            byte[] utf8NeedleUpper = null, utf16NeedleUpper = null;
            if (!case_sensitive)
            {
                utf8NeedleUpper = Encoding.UTF8.GetBytes(query.ToUpperInvariant());
                utf16NeedleUpper = Encoding.Unicode.GetBytes(query.ToUpperInvariant());
            }

            var results = new ConcurrentBag<(string path, long size)>();
            var scanned = 0;
            var errors = 0;
            var done = 0;

            Parallel.ForEach(fileList,
                new ParallelOptions { MaxDegreeOfParallelism = Environment.ProcessorCount },
                file =>
            {
                if (Volatile.Read(ref done) == 1) return;
                Interlocked.Increment(ref scanned);

                try
                {
                    var data = file.Read();
                    if (data == null || data.Length == 0) return;

                    var found = case_sensitive
                        ? ContainsSequence(data, utf8Needle) || ContainsSequence(data, utf16Needle)
                        : ContainsSequenceCaseInsensitive(data, utf8Needle, utf8NeedleUpper)
                          || ContainsSequenceCaseInsensitive(data, utf16Needle, utf16NeedleUpper);

                    if (found)
                    {
                        results.Add((file.Path, file.Size));
                        if (results.Count >= limit)
                            Volatile.Write(ref done, 1);
                    }
                }
                catch
                {
                    Interlocked.Increment(ref errors);
                }
            });

            var sorted = results.OrderBy(r => r.path)
                .Take(limit)
                .Select(r => new { path = r.path, size = r.size })
                .ToList();

            return Text(JsonConvert.SerializeObject(new
            {
                query,
                case_sensitive,
                total_files_in_scope = totalFiles,
                files_scanned = scanned,
                files_matched = sorted.Count,
                scan_errors = errors,
                limit,
                results = sorted
            }, Formatting.Indented));
        });
    }

    /// <summary>Exact byte sequence search (case-sensitive).</summary>
    private static bool ContainsSequence(byte[] haystack, byte[] needle)
    {
        return ((ReadOnlySpan<byte>)haystack).IndexOf((ReadOnlySpan<byte>)needle) >= 0;
    }

    /// <summary>
    /// Case-insensitive byte sequence search. Checks each candidate position
    /// byte-by-byte against both the lower and upper needle variants —
    /// avoids allocating a lowered copy of every file.
    /// </summary>
    private static bool ContainsSequenceCaseInsensitive(byte[] haystack, byte[] lower, byte[] upper)
    {
        if (lower.Length == 0 || haystack.Length < lower.Length) return false;

        var end = haystack.Length - lower.Length;
        var lo0 = lower[0];
        var hi0 = upper[0];

        for (var i = 0; i <= end; i++)
        {
            var b = haystack[i];
            if (b != lo0 && b != hi0) continue;

            var match = true;
            for (var j = 1; j < lower.Length; j++)
            {
                var c = haystack[i + j];
                if (c != lower[j] && c != upper[j])
                {
                    match = false;
                    break;
                }
            }

            if (match) return true;
        }

        return false;
    }

    // -------------------------------------------------------------------------
    // Material visualization
    // -------------------------------------------------------------------------

    [McpServerTool]
    [Description("Generate a Mermaid flowchart diagram visualizing a material's parameter hierarchy, texture references, overrides, blend mode, shading model, and static switches. Works with MaterialInstanceConstant, MaterialInstance, and Material assets.")]
    public async Task<CallToolResult> VisualizeMaterial(
        [Description("Full asset path to the material (extension optional). e.g. 'Game/Characters/Materials/MI_Body'")] string asset_path)
    {
        if (!IsLoaded) return Error(NotLoadedMsg);

        try
        {
            var entry = ResolveEntry(asset_path, ".uasset", ".umap");
            if (entry == null) return Error($"File not found: {asset_path}");

            var package = await Task.Run(() => handler.CUE4Parse.Provider.LoadPackage(entry));
            var mermaid = MaterialVisualizer.Generate(package);
            return Text(mermaid);
        }
        catch (Exception ex)
        {
            Log.Error(ex, "[MCP] VisualizeMaterial failed for '{AssetPath}'", asset_path);
            return Error(ex.Message);
        }
    }

    // -------------------------------------------------------------------------
    // Reference scanning
    // -------------------------------------------------------------------------

    [McpServerTool]
    [Description("Find all assets that reference the given asset. Uses IoStore container headers for fast lookup — most effective on modern UE5 games (Fortnite, Valorant, etc.). Returns an empty list for pak-only games.")]
    public async Task<CallToolResult> FindReferences(
        [Description("Full asset path to find references for. Extension is optional.")] string asset_path)
    {
        if (!IsLoaded) return Error(NotLoadedMsg);

        return await Task.Run(() =>
        {
            try
            {
                var entry = ResolveEntry(asset_path, ".uasset", ".umap");
                if (entry == null) return Error($"File not found: {asset_path}");

                var refs = handler.CUE4Parse.Provider.ScanForPackageRefs(entry);
                var results = refs.Select(f => f.Path).ToList();
                return Text(JsonConvert.SerializeObject(
                    new { query = asset_path, count = results.Count, references = results },
                    Formatting.Indented));
            }
            catch (Exception ex)
            {
                Log.Error(ex, "[MCP] FindReferences failed for '{AssetPath}'", asset_path);
                return Error(ex.Message);
            }
        });
    }

    // -------------------------------------------------------------------------
    // Helpers
    // -------------------------------------------------------------------------

    private CUE4Parse.FileProvider.Objects.GameFile ResolveEntry(string path, params string[] fallbackExtensions)
    {
        if (handler.CUE4Parse.Provider.Files.TryGetValue(path, out var entry))
            return entry;

        foreach (var ext in fallbackExtensions)
        {
            if (handler.CUE4Parse.Provider.Files.TryGetValue(path + ext, out entry))
                return entry;
        }

        // Try stripping extension and re-trying with each fallback
        var noExt = Path.GetFileNameWithoutExtension(path);
        var dir = Path.GetDirectoryName(path)?.Replace('\\', '/') ?? "";
        var basePath = string.IsNullOrEmpty(dir) ? noExt : $"{dir}/{noExt}";

        foreach (var ext in fallbackExtensions)
        {
            if (handler.CUE4Parse.Provider.Files.TryGetValue(basePath + ext, out entry))
                return entry;
        }

        return null;
    }

    private static CallToolResult Text(string content) =>
        new() { Content = [new TextContentBlock { Text = content }] };

    private static CallToolResult Error(string message) =>
        new() { Content = [new TextContentBlock { Text = message }], IsError = true };

    private static object BuildTree(List<string> paths, string prefix, int maxDepth)
    {
        var prefixLen = string.IsNullOrEmpty(prefix) ? 0 : prefix.Length + 1;
        var root = new SortedDictionary<string, object>(StringComparer.OrdinalIgnoreCase);

        foreach (var path in paths)
        {
            if (path.Length <= prefixLen) continue;
            var relative = path[prefixLen..];
            var parts = relative.Split('/', StringSplitOptions.RemoveEmptyEntries);
            AddToTree(root, parts, 0, maxDepth);
        }

        return root;
    }

    private static void AddToTree(SortedDictionary<string, object> node, string[] parts, int depth, int maxDepth)
    {
        if (depth >= parts.Length) return;

        var key = parts[depth];
        var isLeaf = depth == parts.Length - 1;

        if (isLeaf)
        {
            node.TryAdd(key, null);
            return;
        }

        if (!node.TryGetValue(key, out var existing) || existing is not SortedDictionary<string, object> child)
        {
            if (depth >= maxDepth - 1)
            {
                node[key] = "...";
                return;
            }

            child = new SortedDictionary<string, object>(StringComparer.OrdinalIgnoreCase);
            node[key] = child;
        }

        if (depth < maxDepth - 1 && child is SortedDictionary<string, object> childDict)
            AddToTree(childDict, parts, depth + 1, maxDepth);
    }
}
