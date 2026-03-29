using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

using CUE4Parse.FileProvider;
using CUE4Parse.FileProvider.Objects;
using CUE4Parse.UE4.Assets;
using CUE4Parse.UE4.Assets.Exports;
using CUE4Parse.UE4.Assets.Exports.Material;
using CUE4Parse.UE4.Assets.Exports.Material.Parameters;
using CUE4Parse.UE4.Assets.Objects;
using CUE4Parse.UE4.Objects.Core.Math;
using CUE4Parse.UE4.Objects.Engine;
using CUE4Parse.UE4.Objects.UObject;

namespace FModel.Services;

public static class MaterialVisualizer
{
    private const int MaxNodes = 150;

    private static readonly string[] OutputPinNames =
    [
        "BaseColor", "EmissiveColor", "Opacity", "OpacityMask",
        "WorldPositionOffset", "SubsurfaceColor", "Normal", "Tangent",
        "Metallic", "Specular", "Roughness", "Anisotropy",
        "AmbientOcclusion", "Refraction", "PixelDepthOffset",
        "ShadingModel", "CustomData0", "CustomData1", "CustomEyeTangent"
    ];

    // Engine utility functions that are internal plumbing — hidden from flow diagrams
    private static readonly HashSet<string> HiddenFunctionNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "BreakOutFloat2Components", "BreakOutFloat3Components", "BreakOutFloat4Components",
        "MakeFloat2", "MakeFloat3", "MakeFloat4",
        "Pi", "ScreenResolution", "FOV", "VectorLength",
        "ComponentPivotLocation", "ObjectPivotPoint", "TransformToClipSpace",
        "WorldSpaceAlignedScreenCoordinates", "ScreenAlignedPixelToPixelUVs",
        "LocalPosition", "ObjectLocalBounds", "BoundingBoxBased_0-1_UVW",
        "ProjectVectorOntoPlane", "RemapValueRange", "DebugTimeSine",
        "Sine_Remapped", "CustomRotator", "DitherTemporalAA"
    };

    // Output pin keyword mapping: function name tokens → likely output pin
    private static readonly Dictionary<string, string> OutputKeywords = new(StringComparer.OrdinalIgnoreCase)
    {
        ["dissolve"] = "OpacityMask", ["clip"] = "OpacityMask", ["mask"] = "OpacityMask",
        ["opacity"] = "OpacityMask", ["alpha"] = "OpacityMask",
        ["fresnel"] = "EmissiveColor", ["emissive"] = "EmissiveColor", ["emote"] = "EmissiveColor",
        ["glow"] = "EmissiveColor", ["light"] = "EmissiveColor", ["bloom"] = "EmissiveColor",
        ["color"] = "BaseColor", ["base"] = "BaseColor", ["albedo"] = "BaseColor",
        ["diffuse"] = "BaseColor", ["tint"] = "BaseColor",
        ["normal"] = "Normal", ["bump"] = "Normal", ["normalmap"] = "Normal",
        ["offset"] = "WorldPositionOffset", ["position"] = "WorldPositionOffset",
        ["displacement"] = "WorldPositionOffset", ["vertex"] = "WorldPositionOffset",
        ["metallic"] = "Metallic", ["metal"] = "Metallic",
        ["roughness"] = "Roughness", ["rough"] = "Roughness",
        ["specular"] = "Specular",
        ["occlusion"] = "AmbientOcclusion", ["ao"] = "AmbientOcclusion",
        ["refraction"] = "Refraction",
        ["subsurface"] = "SubsurfaceColor", ["sss"] = "SubsurfaceColor",
        ["dither"] = "OpacityMask", ["depth"] = "PixelDepthOffset",
    };

    // -------------------------------------------------------------------
    // Entry points
    // -------------------------------------------------------------------

    public static async Task<string> GenerateFromEntry(IFileProvider provider, GameFile entry)
    {
        var package = await Task.Run(() => provider.LoadPackage(entry));
        return Generate(package);
    }

    public static string Generate(IPackage package)
    {
        var exports = package.GetExports();
        var primary = exports.FirstOrDefault();

        return primary switch
        {
            UMaterialInstanceConstant mic => BuildMIDiagram(mic),
            UMaterialInstance mi => BuildMIDiagram(mi),
            UMaterial mat => BuildMaterialDiagram(mat),
            _ => throw new InvalidOperationException(
                $"Asset is not a material. First export is {primary?.ExportType ?? "null"}.")
        };
    }

    // -------------------------------------------------------------------
    // Graph data structures
    // -------------------------------------------------------------------

    private class ExprNode
    {
        public int Id;
        public int PkgIndex; // FPackageIndex.Index for matching
        public string ExportType;
        public string ShortType;
        public string Label;
        public UObject Export;
        public bool IsParameter;
        public string ParameterName;
        public string DefaultValueStr;
        public string Group; // parameter group name
    }

    private class ExprEdge
    {
        public int FromId;
        public int ToId;
        public string InputName;
        public int OutputIndex;
        public string Swizzle;
    }

    private class ExprGraph
    {
        public Dictionary<int, ExprNode> Nodes = new(); // Id -> Node
        public Dictionary<int, int> PkgToId = new(); // FPackageIndex.Index -> node Id
        public List<ExprEdge> Edges = [];
        public List<(string PinName, int NodeId)> OutputEdges = [];
        public List<string> ConnectedOutputs = []; // from PropertyConnectedMask
    }

    // -------------------------------------------------------------------
    // Build expression graph from UMaterial
    // -------------------------------------------------------------------

    private static ExprGraph BuildExpressionGraph(UMaterial mat)
    {
        var graph = new ExprGraph();
        var nodeId = 0;

        // Build nodes from Expressions array
        foreach (var exprRef in mat.Expressions)
        {
            if (!exprRef.TryLoad(out UMaterialExpression expr)) continue;

            var id = nodeId++;
            var shortType = expr.ExportType
                .Replace("MaterialExpression", "")
                .Replace("MaterialFunction", "MF_");

            var node = new ExprNode
            {
                Id = id,
                PkgIndex = exprRef.Index,
                ExportType = expr.ExportType,
                ShortType = shortType,
                Export = expr,
            };

            // Extract parameter info and build labels
            switch (expr)
            {
                case UMaterialExpressionScalarParameter sp:
                    node.IsParameter = true;
                    node.ParameterName = sp.ParameterName.Text;
                    node.DefaultValueStr = sp.DefaultValue.ToString("G");
                    node.Group = sp.Group.Text;
                    node.Label = $"{sp.ParameterName}<br/>= {sp.DefaultValue:G}";
                    break;
                case UMaterialExpressionVectorParameter vp:
                    node.IsParameter = true;
                    node.ParameterName = vp.ParameterName.Text;
                    node.DefaultValueStr = vp.DefaultValue.Hex;
                    node.Group = vp.Group.Text;
                    node.Label = $"{vp.ParameterName}<br/>= {vp.DefaultValue.Hex}";
                    break;
                case UMaterialExpressionTextureSampleParameter tsp:
                    node.IsParameter = true;
                    node.ParameterName = tsp.ParameterName.Text;
                    node.DefaultValueStr = tsp.Texture?.Name ?? "None";
                    node.Group = tsp.Group.Text;
                    node.Label = $"{tsp.ParameterName}<br/>{tsp.Texture?.Name ?? "None"}";
                    break;
                case UMaterialExpressionStaticBoolParameter sbp:
                    node.IsParameter = true;
                    node.ParameterName = sbp.ParameterName.Text;
                    node.DefaultValueStr = sbp.DefaultValue.ToString();
                    node.Group = sbp.Group.Text;
                    node.Label = $"{sbp.ParameterName}<br/>= {sbp.DefaultValue.ToString().ToLower()}";
                    break;
                case UMaterialExpressionTextureBase tb:
                    node.Label = $"Texture<br/>{tb.Texture?.Name ?? "?"}";
                    break;
                default:
                    node.Label = BuildGenericLabel(expr, shortType);
                    break;
            }

            graph.Nodes[id] = node;
            graph.PkgToId[exprRef.Index] = id;
        }

        // Extract edges from expression input properties
        foreach (var (id, node) in graph.Nodes)
        {
            var seen = new HashSet<int>(); // avoid duplicate edges by source pkg index

            // Pass 1: Direct FExpressionInput properties (A, B, Coordinates, etc.)
            foreach (var prop in node.Export.Properties)
            {
                var input = ExtractExpressionInput(prop);
                if (input?.Expression == null || input.Expression.Index == 0) continue;
                if (!seen.Add(input.Expression.Index)) continue;

                if (graph.PkgToId.TryGetValue(input.Expression.Index, out var sourceId))
                {
                    graph.Edges.Add(new ExprEdge
                    {
                        FromId = sourceId,
                        ToId = id,
                        InputName = prop.Name.Text,
                        OutputIndex = input.OutputIndex,
                        Swizzle = GetSwizzle(input)
                    });
                }
            }

            // Pass 2: FunctionInputs array (MaterialExpressionMaterialFunctionCall)
            try
            {
                if (node.Export.TryGetValue(out FStructFallback[] funcInputs, "FunctionInputs"))
                {
                    foreach (var fi in funcInputs)
                    {
                        var input = fi.GetOrDefault<FExpressionInput>("ExpressionInput");
                        if (input?.Expression == null || input.Expression.Index == 0) continue;
                        if (!seen.Add(input.Expression.Index)) continue;

                        if (graph.PkgToId.TryGetValue(input.Expression.Index, out var sourceId))
                        {
                            var inputName = fi.GetOrDefault<FName>("InputName").Text;
                            if (string.IsNullOrEmpty(inputName) || inputName == "None")
                                inputName = "Input";
                            graph.Edges.Add(new ExprEdge
                            {
                                FromId = sourceId,
                                ToId = id,
                                InputName = inputName,
                                OutputIndex = input.OutputIndex,
                                Swizzle = GetSwizzle(input)
                            });
                        }
                    }
                }
            }
            catch { }

            // Pass 3: Inputs array (QualitySwitch, FeatureLevelSwitch, etc.)
            try
            {
                if (node.Export.TryGetValue(out FExpressionInput[] inputsArr, "Inputs"))
                {
                    for (var i = 0; i < inputsArr.Length; i++)
                    {
                        var input = inputsArr[i];
                        if (input?.Expression == null || input.Expression.Index == 0) continue;
                        if (!seen.Add(input.Expression.Index)) continue;

                        if (graph.PkgToId.TryGetValue(input.Expression.Index, out var sourceId))
                        {
                            graph.Edges.Add(new ExprEdge
                            {
                                FromId = sourceId,
                                ToId = id,
                                InputName = $"Input {i}",
                                OutputIndex = input.OutputIndex,
                                Swizzle = GetSwizzle(input)
                            });
                        }
                    }
                }
            }
            catch { }
        }

        // Try to find output pin connections from UMaterial properties
        foreach (var prop in mat.Properties)
        {
            var pinIdx = Array.IndexOf(OutputPinNames, prop.Name.Text);
            if (pinIdx < 0) continue;

            var input = ExtractExpressionInput(prop);
            if (input?.Expression == null || input.Expression.Index == 0) continue;

            if (graph.PkgToId.TryGetValue(input.Expression.Index, out var sourceId))
            {
                graph.OutputEdges.Add((OutputPinNames[pinIdx], sourceId));
            }
        }

        // Get connected outputs from PropertyConnectedMask (always available)
        graph.ConnectedOutputs = GetConnectedOutputPins(mat.CachedExpressionData);

        // If we found no output edges from properties, try to infer them
        if (graph.OutputEdges.Count == 0 && graph.ConnectedOutputs.Count > 0)
        {
            InferOutputEdges(graph);
        }

        return graph;
    }

    /// <summary>
    /// When output pin properties aren't available, find terminal expressions
    /// (ones that no other expression reads from) and try to match them to output pins.
    /// </summary>
    private static void InferOutputEdges(ExprGraph graph)
    {
        var nodesWithOutgoing = new HashSet<int>();
        foreach (var edge in graph.Edges)
            nodesWithOutgoing.Add(edge.FromId);

        // Terminal nodes: have incoming edges but no outgoing edges to other expressions
        var terminals = graph.Nodes.Keys
            .Where(id => !nodesWithOutgoing.Contains(id))
            .Where(id => graph.Edges.Any(e => e.ToId == id) || graph.Nodes[id].IsParameter)
            .ToList();

        // Simple heuristic: if there's exactly one terminal per output, match them in order
        // Otherwise just mark all terminals as "→ Output"
        if (terminals.Count > 0 && graph.ConnectedOutputs.Count > 0)
        {
            // Can't reliably match terminals to specific outputs without more info
            // Just show connected outputs separately
        }
    }

    // -------------------------------------------------------------------
    // Base Material diagram
    // -------------------------------------------------------------------

    private static string BuildMaterialDiagram(UMaterial mat)
    {
        var graph = BuildExpressionGraph(mat);

        if (graph.Edges.Count == 0)
            return BuildFallbackMaterialDiagram(mat);

        return RenderGraph(graph, mat.Name,
            $"BlendMode: {mat.BlendMode} | ShadingModel: {mat.ShadingModel} | TwoSided: {mat.TwoSided}");
    }

    // -------------------------------------------------------------------
    // Material Instance diagram
    // -------------------------------------------------------------------

    private static string BuildMIDiagram(UMaterialInstance mi)
    {
        var scalars = mi is UMaterialInstanceConstant mic1 ? mic1.ScalarParameterValues : [];
        var vectors = mi is UMaterialInstanceConstant mic2 ? mic2.VectorParameterValues : [];
        var textures = mi is UMaterialInstanceConstant mic3 ? mic3.TextureParameterValues : [];
        var switches = mi.StaticParameters?.StaticSwitchParameters ?? [];

        // Walk up to find the root UMaterial
        UMaterial parentMat = null;
        try
        {
            UObject parent = mi.Parent;
            while (parent is UMaterialInstance pmi)
                parent = pmi.Parent;
            if (parent is UMaterial m) parentMat = m;
        }
        catch { }

        // Try graph approach with parent material
        if (parentMat != null && parentMat.Expressions.Length > 0)
        {
            var graph = BuildExpressionGraph(parentMat);
            if (graph.Edges.Count > 0)
            {
                return RenderMIGraph(mi, parentMat, graph,
                    scalars, vectors, textures, switches);
            }
        }

        return BuildFallbackMIDiagram(mi, parentMat, scalars, vectors, textures, switches);
    }

    // -------------------------------------------------------------------
    // Render graph as Mermaid
    // -------------------------------------------------------------------

    private static string RenderGraph(ExprGraph graph, string title, string settingsLine,
        HashSet<int> overriddenNodes = null)
    {
        var sb = new StringBuilder();
        sb.AppendLine("flowchart LR");

        // Style classes
        sb.AppendLine("    classDef param fill:#e3f2fd,stroke:#1565c0");
        sb.AppendLine("    classDef override fill:#ffe0b2,stroke:#e65100,stroke-width:2px");
        sb.AppendLine("    classDef texNode fill:#e8f5e9,stroke:#2e7d32");
        sb.AppendLine("    classDef funcNode fill:#f3e5f5,stroke:#7b1fa2");
        sb.AppendLine("    classDef output fill:#fce4ec,stroke:#c62828,stroke-width:2px");

        // Find reachable nodes
        var reachable = FindReachable(graph);
        if (reachable.Count > MaxNodes)
            reachable = reachable.OrderBy(id => id).Take(MaxNodes).ToHashSet();

        // Emit all nodes flat — no subgraph grouping, let edges create the flow
        foreach (var id in reachable)
        {
            if (!graph.Nodes.TryGetValue(id, out var node)) continue;
            EmitNode(sb, node, overriddenNodes);
        }

        // Emit edges
        foreach (var edge in graph.Edges)
        {
            if (!reachable.Contains(edge.FromId) || !reachable.Contains(edge.ToId)) continue;
            var label = FormatEdgeLabel(edge);
            if (!string.IsNullOrEmpty(label))
                sb.AppendLine($"    n{edge.FromId} -->|\"{Esc(label)}\"| n{edge.ToId}");
            else
                sb.AppendLine($"    n{edge.FromId} --> n{edge.ToId}");
        }

        // Output pins — connected directly to their source expressions
        if (graph.OutputEdges.Count > 0)
        {
            foreach (var (pinName, nodeId) in graph.OutputEdges)
            {
                if (!reachable.Contains(nodeId)) continue;
                sb.AppendLine($"    n{nodeId} --> out_{pinName}{{\"{Esc(pinName)}\"}}");
                sb.AppendLine($"    class out_{pinName} output");
            }
        }
        else if (graph.ConnectedOutputs.Count > 0)
        {
            // No direct output edges found — show connected output pins as standalone
            foreach (var pin in graph.ConnectedOutputs)
            {
                sb.AppendLine($"    out_{pin}{{\"{Esc(pin)}\"}}");
                sb.AppendLine($"    class out_{pin} output");
            }
        }

        // Material settings as a single info node
        if (!string.IsNullOrEmpty(settingsLine))
            sb.AppendLine($"    settings[\"{Esc(title)}<br/>{Esc(settingsLine)}\"]");

        return sb.ToString();
    }

    private static void EmitNode(StringBuilder sb, ExprNode node, HashSet<int> overriddenNodes)
    {
        var label = Esc(node.Label);
        string styleClass = null;

        if (node.IsParameter)
        {
            // Stadium shape for parameters
            sb.AppendLine($"    n{node.Id}([\"{label}\"])");
            styleClass = overriddenNodes?.Contains(node.Id) == true ? "override" : "param";
        }
        else if (node.ShortType.Contains("Texture"))
        {
            sb.AppendLine($"    n{node.Id}[\"{label}\"]");
            styleClass = "texNode";
        }
        else if (node.ShortType.Contains("FunctionCall") || node.ShortType.StartsWith("MF_"))
        {
            sb.AppendLine($"    n{node.Id}[[\"{label}\"]]");
            styleClass = "funcNode";
        }
        else
        {
            sb.AppendLine($"    n{node.Id}[\"{label}\"]");
        }

        if (styleClass != null)
            sb.AppendLine($"    class n{node.Id} {styleClass}");
    }

    // -------------------------------------------------------------------
    // MI graph render - overlays parameter overrides onto parent graph
    // -------------------------------------------------------------------

    private static string RenderMIGraph(
        UMaterialInstance mi, UMaterial parentMat, ExprGraph graph,
        FScalarParameterValue[] scalars,
        FVectorParameterValue[] vectors,
        FTextureParameterValue[] textures,
        FStaticSwitchParameter[] switches)
    {
        var overriddenNodes = new HashSet<int>();

        // Build override lookup tables
        var scalarMap = scalars.ToDictionary(s => s.Name, s => s.ParameterValue, StringComparer.OrdinalIgnoreCase);
        var vectorMap = vectors.ToDictionary(v => v.Name, v => v.ParameterValue, StringComparer.OrdinalIgnoreCase);
        var textureMap = textures.ToDictionary(t => t.Name, t => t.ParameterValue, StringComparer.OrdinalIgnoreCase);
        var switchMap = switches.ToDictionary(s => s.Name, s => s.Value, StringComparer.OrdinalIgnoreCase);

        // Overlay MI parameter overrides onto graph nodes
        foreach (var (id, node) in graph.Nodes)
        {
            if (!node.IsParameter || string.IsNullOrEmpty(node.ParameterName)) continue;

            if (scalarMap.TryGetValue(node.ParameterName, out var scalarVal))
            {
                node.Label = $"{node.ParameterName}<br/>{node.DefaultValueStr} -> {scalarVal:G}";
                overriddenNodes.Add(id);
            }
            else if (vectorMap.TryGetValue(node.ParameterName, out var vecVal))
            {
                var hex = vecVal?.Hex ?? "?";
                node.Label = $"{node.ParameterName}<br/>{node.DefaultValueStr} -> {hex}";
                overriddenNodes.Add(id);
            }
            else if (textureMap.TryGetValue(node.ParameterName, out var texVal))
            {
                var texName = texVal?.Name ?? "None";
                node.Label = $"{node.ParameterName}<br/>{node.DefaultValueStr} -> {texName}";
                overriddenNodes.Add(id);
            }
            else if (switchMap.TryGetValue(node.ParameterName, out var swVal))
            {
                node.Label = $"{node.ParameterName}<br/>{node.DefaultValueStr} -> {swVal.ToString().ToLower()}";
                overriddenNodes.Add(id);
            }
        }

        // Build settings line
        var bpo = GetBasePropertyOverrideStrings(mi);
        var settingsParts = new List<string>
        {
            $"BlendMode: {parentMat.BlendMode}",
            $"ShadingModel: {parentMat.ShadingModel}",
        };
        if (parentMat.TwoSided) settingsParts.Add("TwoSided: true");
        foreach (var kv in bpo)
            settingsParts.Add($"{kv.Key}: {kv.Value} [override]");

        var settings = string.Join(" | ", settingsParts);
        var title = $"{mi.Name} -> {parentMat.Name}";

        return RenderGraph(graph, title, settings, overriddenNodes);
    }

    // -------------------------------------------------------------------
    // Flow diagrams — groups params by feature prefix, connects to functions & outputs
    // -------------------------------------------------------------------

    private record ParamInfo(string Name, string Type, string Value, bool IsOverride);

    private static string BuildFallbackMaterialDiagram(UMaterial mat)
    {
        var sb = new StringBuilder();
        sb.AppendLine("flowchart LR");
        sb.AppendLine("    classDef param fill:#e3f2fd,stroke:#1565c0");
        sb.AppendLine("    classDef funcNode fill:#f3e5f5,stroke:#7b1fa2");
        sb.AppendLine("    classDef texNode fill:#e8f5e9,stroke:#2e7d32");
        sb.AppendLine("    classDef output fill:#fce4ec,stroke:#c62828,stroke-width:2px");

        // Collect params
        var allParams = new List<ParamInfo>();
        var paramsByType = GetCachedParametersByType(mat.CachedExpressionData);
        foreach (var (typeName, names, defaults) in paramsByType)
            for (var i = 0; i < names.Count; i++)
                allParams.Add(new ParamInfo(names[i], typeName.ToLower(),
                    i < defaults.Count ? defaults[i] : null, false));

        var groups = GroupByPrefix(allParams);
        var functions = GetMaterialFunctions(mat.CachedExpressionData);
        var outputPins = GetConnectedOutputPins(mat.CachedExpressionData);

        // Texture parameter → texture asset mapping from CachedExpressionData parallel arrays
        var texParamMapping = GetTextureParameterMapping(mat.CachedExpressionData);
        var streamingData = GetTextureStreamingData(mat);

        // Create texture nodes keyed by parameter name, labeled with asset name
        var texParamNames = new List<string>();
        foreach (var (paramName, assetName) in texParamMapping)
        {
            var label = assetName;
            if (streamingData.TryGetValue(assetName, out var info))
                label = $"{assetName}<br/>UV{info.UVChannel} scale:{info.Scale:G}";
            sb.AppendLine($"    tex_{SanitizeId(paramName)}[\"{Esc(label)}\"]:::texNode");
            texParamNames.Add(paramName);
        }

        // Parameter groups, functions, outputs — shared flow builder
        EmitFlowLayers(sb, groups, functions, outputPins, texParamNames, null);

        // Material settings
        sb.AppendLine($"    info[\"{Esc(mat.Name)}<br/>BlendMode: {mat.BlendMode}<br/>ShadingModel: {mat.ShadingModel}<br/>TwoSided: {mat.TwoSided}\"]");

        return sb.ToString();
    }

    private static string BuildFallbackMIDiagram(
        UMaterialInstance mi, UMaterial parentMat,
        FScalarParameterValue[] scalars,
        FVectorParameterValue[] vectors,
        FTextureParameterValue[] textures,
        FStaticSwitchParameter[] switches)
    {
        var sb = new StringBuilder();
        sb.AppendLine("flowchart LR");
        sb.AppendLine("    classDef override fill:#ffe0b2,stroke:#e65100,stroke-width:2px");
        sb.AppendLine("    classDef param fill:#e3f2fd,stroke:#1565c0");
        sb.AppendLine("    classDef funcNode fill:#f3e5f5,stroke:#7b1fa2");
        sb.AppendLine("    classDef texNode fill:#e8f5e9,stroke:#2e7d32");
        sb.AppendLine("    classDef output fill:#fce4ec,stroke:#c62828,stroke-width:2px");

        // Collect ALL parameters — MI overrides + parent defaults
        var allParams = new List<ParamInfo>();
        foreach (var s in scalars)
            allParams.Add(new ParamInfo(s.Name, "scalar", s.ParameterValue.ToString("G"), true));
        foreach (var v in vectors)
            allParams.Add(new ParamInfo(v.Name, "vector", FormatColor(v), true));
        foreach (var t in textures)
            allParams.Add(new ParamInfo(t.Name, "texture", t.ParameterValue?.Name ?? "None", true));
        foreach (var sw in switches)
            allParams.Add(new ParamInfo(sw.Name, "switch", sw.Value.ToString().ToLower(), true));

        // Parent defaults not overridden
        if (parentMat != null)
        {
            var overriddenNames = new HashSet<string>(allParams.Select(p => p.Name), StringComparer.OrdinalIgnoreCase);
            var parentParams = GetCachedParametersByType(parentMat.CachedExpressionData);
            foreach (var (typeName, names, defaults) in parentParams)
                for (var i = 0; i < names.Count; i++)
                {
                    if (overriddenNames.Contains(names[i])) continue;
                    allParams.Add(new ParamInfo(names[i], typeName.ToLower(),
                        i < defaults.Count ? defaults[i] : null, false));
                }
        }

        var groups = GroupByPrefix(allParams);
        var functions = parentMat != null ? GetMaterialFunctions(parentMat.CachedExpressionData) : [];
        var outputPins = parentMat != null ? GetConnectedOutputPins(parentMat.CachedExpressionData) : [];

        // Texture asset nodes (leftmost layer)
        foreach (var t in textures)
        {
            var texName = t.ParameterValue?.Name ?? "None";
            sb.AppendLine($"    tex_{SanitizeId(t.Name)}[\"{Esc(texName)}\"]:::texNode");
        }

        // Parameter groups, functions, outputs
        var texParamNames = textures.Select(t => t.Name).ToList();
        EmitFlowLayers(sb, groups, functions, outputPins, texParamNames, textures);

        // Settings + BPO
        var settingParts = new List<string>();
        settingParts.Add(mi.Name);
        if (parentMat != null)
        {
            settingParts.Add($"Parent: {parentMat.Name}");
            settingParts.Add($"BlendMode: {parentMat.BlendMode}");
            settingParts.Add($"ShadingModel: {parentMat.ShadingModel}");
        }
        var bpo = GetBasePropertyOverrideStrings(mi);
        foreach (var kv in bpo)
            settingParts.Add($"{kv.Key}: {kv.Value}");
        sb.AppendLine($"    info[\"{Esc(string.Join("<br/>", settingParts))}\"]");

        return sb.ToString();
    }

    /// <summary>
    /// Emit the core flow: Parameter Groups → Functions → Output Pins.
    /// Uses score-based matching, function pipeline analysis, and keyword-based output inference.
    /// </summary>
    private static void EmitFlowLayers(
        StringBuilder sb,
        Dictionary<string, List<ParamInfo>> groups,
        List<(string Name, string Path)> functions,
        List<string> outputPins,
        List<string> texParamNames,
        FTextureParameterValue[] texOverrides)
    {
        // --- Layer 1: Parameter groups ---
        var groupIds = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var gIdx = 0;
        foreach (var (groupName, parms) in groups)
        {
            var gid = $"g{gIdx++}";
            groupIds[groupName] = gid;

            var title = FormatGroupTitle(groupName, parms);

            if (parms.Count > 1)
            {
                sb.AppendLine($"    subgraph {gid}[\"{Esc(title)}\"]");
                sb.AppendLine("        direction TB");
                var pIdx = 0;
                foreach (var p in parms.Take(12))
                {
                    var pid = $"{gid}_{pIdx++}";
                    var label = p.Value != null ? $"{p.Name}: {p.Value}" : p.Name;
                    var cls = p.IsOverride ? "override" : "param";
                    sb.AppendLine($"        {pid}([\"{Esc(label)}\"]):::{cls}");
                }
                if (parms.Count > 12)
                    sb.AppendLine($"        {gid}_more[\"... +{parms.Count - 12} more\"]");
                sb.AppendLine("    end");
            }
            else
            {
                var p = parms[0];
                var label = p.Value != null ? $"{p.Name}: {p.Value}" : p.Name;
                var cls = p.IsOverride ? "override" : "param";
                sb.AppendLine($"    {gid}([\"{Esc(label)}\"]):::{cls}");
            }
        }

        // --- Connect texture assets to their param groups ---
        if (texOverrides != null)
        {
            foreach (var t in texOverrides)
            {
                var texGroupKey = groups.Keys
                    .FirstOrDefault(k => groups[k].Any(p => p.Name.Equals(t.Name, StringComparison.OrdinalIgnoreCase)));
                if (texGroupKey != null && groupIds.TryGetValue(texGroupKey, out var gid))
                    sb.AppendLine($"    tex_{SanitizeId(t.Name)} --> {gid}");
            }
        }
        else
        {
            foreach (var texName in texParamNames)
            {
                var texGroupKey = groups.Keys
                    .FirstOrDefault(k => groups[k].Any(p =>
                        p.Type == "texture" && p.Name.Equals(texName, StringComparison.OrdinalIgnoreCase)));
                if (texGroupKey != null && groupIds.TryGetValue(texGroupKey, out var gid))
                    sb.AppendLine($"    tex_{SanitizeId(texName)} --> {gid}");
            }
        }

        // --- Layer 2: Feature functions (filter out utility/UV plumbing) ---
        var featureFuncs = functions
            .Where(f => !HiddenFunctionNames.Contains(f.Name))
            .Select(f => (f.Name, f.Path, Id: SanitizeId(f.Name)))
            .DistinctBy(f => f.Id, StringComparer.OrdinalIgnoreCase)
            .ToList();

        // Separate UV processing functions from true feature functions
        var uvFuncIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var trueFeatureFuncs = new List<(string Name, string Path, string Id)>();
        foreach (var f in featureFuncs)
        {
            var lower = f.Name.ToLower();
            if (lower.Contains("uvtile") || lower.Contains("uvpanner") || lower.Contains("uvclamp")
                || lower == "smoothstep" || lower == "lerp")
            {
                uvFuncIds.Add(f.Id);
            }
            else
            {
                trueFeatureFuncs.Add(f);
            }
        }

        // Emit feature function nodes
        var emittedFuncs = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var f in trueFeatureFuncs)
        {
            if (emittedFuncs.Add(f.Id))
                sb.AppendLine($"    f_{f.Id}[[\"{Esc(f.Name)}\"]]:::funcNode");
        }

        // --- Score-based matching: param groups → feature functions ---
        var groupToFuncs = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
        var funcMatchedByGroup = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var (groupName, parms) in groups)
        {
            var bestScore = 0;
            var bestMatches = new List<string>();

            foreach (var f in trueFeatureFuncs)
            {
                var score = ScoreGroupFunction(groupName, parms, f.Name);
                if (score > bestScore)
                {
                    bestScore = score;
                    bestMatches = [f.Id];
                }
                else if (score == bestScore && score > 0)
                {
                    bestMatches.Add(f.Id);
                }
            }

            if (bestScore >= 2 && bestMatches.Count > 0)
            {
                // Take at most 2 best matches per group
                var matches = bestMatches.Take(2).ToList();
                groupToFuncs[groupName] = matches;
                foreach (var m in matches)
                    funcMatchedByGroup.Add(m);
            }
        }

        // Emit group → function edges
        foreach (var (groupName, funcIds) in groupToFuncs)
        {
            if (!groupIds.TryGetValue(groupName, out var gid)) continue;
            foreach (var fid in funcIds)
                sb.AppendLine($"    {gid} --> f_{fid}");
        }

        // --- Unmatched groups: connect to combiner or nearest function ---
        // The LAST feature function in call order is typically the combiner (e.g., MF_CommonEffects)
        var combinerFunc = trueFeatureFuncs.LastOrDefault();
        var combinerFid = combinerFunc.Id;

        var unmatchedGroups = groups.Keys
            .Where(g => !groupToFuncs.ContainsKey(g))
            .ToList();

        if (unmatchedGroups.Count > 0 && trueFeatureFuncs.Count > 0)
        {
            foreach (var groupName in unmatchedGroups)
            {
                if (!groupIds.TryGetValue(groupName, out var gid)) continue;

                // Try to find a reasonable function by checking param names in this group
                var found = false;
                foreach (var p in groups[groupName])
                {
                    foreach (var f in trueFeatureFuncs)
                    {
                        if (ScoreGroupFunction(p.Name, null, f.Name) >= 2)
                        {
                            sb.AppendLine($"    {gid} --> f_{f.Id}");
                            found = true;
                            break;
                        }
                    }
                    if (found) break;
                }

                // If still unmatched, connect to combiner function
                if (!found && !string.IsNullOrEmpty(combinerFid))
                    sb.AppendLine($"    {gid} --> f_{combinerFid}");
            }
        }

        // --- Function-to-function chaining based on call order ---
        // Feature functions appearing in sequence form a pipeline
        // The combiner function (last) receives input from other feature functions
        if (trueFeatureFuncs.Count > 1 && !string.IsNullOrEmpty(combinerFid))
        {
            // Connect unmatched feature functions to the combiner
            var funcsToCombiner = trueFeatureFuncs
                .Where(f => !f.Id.Equals(combinerFid, StringComparison.OrdinalIgnoreCase))
                .Where(f => funcMatchedByGroup.Contains(f.Id))
                .ToList();

            foreach (var f in funcsToCombiner)
            {
                // Only chain to combiner if this function doesn't already map to an output
                var directOutput = InferOutputForFunction(f.Name, outputPins);
                if (directOutput == null || directOutput == outputPins.FirstOrDefault())
                    sb.AppendLine($"    f_{f.Id} --> f_{combinerFid}");
            }
        }

        // --- Layer 3: Output pins ---
        foreach (var pin in outputPins)
            sb.AppendLine($"    out_{pin}{{\"{Esc(pin)}\"}}:::output");

        // --- Connect functions to output pins ---
        if (emittedFuncs.Count > 0 && outputPins.Count > 0)
        {
            var funcToOutputs = new Dictionary<string, HashSet<string>>(StringComparer.OrdinalIgnoreCase);

            foreach (var f in trueFeatureFuncs)
            {
                var output = InferOutputForFunction(f.Name, outputPins);
                if (output != null)
                {
                    if (!funcToOutputs.ContainsKey(f.Id))
                        funcToOutputs[f.Id] = [];
                    funcToOutputs[f.Id].Add(output);
                }
            }

            // Combiner function connects to ALL output pins
            if (!string.IsNullOrEmpty(combinerFid))
            {
                if (!funcToOutputs.ContainsKey(combinerFid))
                    funcToOutputs[combinerFid] = [];
                foreach (var pin in outputPins)
                    funcToOutputs[combinerFid].Add(pin);
            }

            // Emit function → output edges
            foreach (var (fid, outputs) in funcToOutputs)
            {
                foreach (var outPin in outputs)
                    sb.AppendLine($"    f_{fid} --> out_{outPin}");
            }

            // Functions without any output match: connect to combiner or first output
            foreach (var f in trueFeatureFuncs.Where(f => !funcToOutputs.ContainsKey(f.Id)))
            {
                if (!string.IsNullOrEmpty(combinerFid) && !f.Id.Equals(combinerFid, StringComparison.OrdinalIgnoreCase))
                    sb.AppendLine($"    f_{f.Id} --> f_{combinerFid}");
                else
                    sb.AppendLine($"    f_{f.Id} --> out_{outputPins[0]}");
            }
        }
    }

    /// <summary>
    /// Score how well a parameter group matches a function name.
    /// Higher score = better match. Returns 0 for no match.
    /// </summary>
    private static int ScoreGroupFunction(string groupKey, List<ParamInfo> groupParams, string funcName)
    {
        var normFunc = funcName
            .Replace("MF_", "").Replace("MF ", "")
            .Replace("Common_", "").Replace("Common", "")
            .ToLower();
        var normGroup = groupKey.ToLower()
            .Replace("fx_", "").Replace("fx ", "").Replace("fx-", "")
            .Replace("vfx_", "").Replace("vfx ", "");

        if (normGroup.Length < 2) return 0;
        var score = 0;

        // Exact match
        if (normFunc.Equals(normGroup, StringComparison.OrdinalIgnoreCase))
            return 10;

        // Direct substring match (either direction)
        if (normFunc.Contains(normGroup) && normGroup.Length >= 3) score += 4;
        else if (normGroup.Contains(normFunc) && normFunc.Length >= 3) score += 4;

        // Tokenize and match individual keywords
        var funcTokens = Tokenize(normFunc);
        var groupTokens = Tokenize(normGroup);

        foreach (var gt in groupTokens)
        {
            if (gt.Length < 3) continue;
            foreach (var ft in funcTokens)
            {
                if (ft.Length < 3) continue;
                if (gt.Equals(ft, StringComparison.OrdinalIgnoreCase))
                    score += 3;
                else if (gt.Contains(ft) || ft.Contains(gt))
                    score += 2;
            }
        }

        // Check individual parameter names for matches (if params provided)
        if (groupParams != null && score < 2)
        {
            foreach (var p in groupParams.Take(5))
            {
                var pNorm = p.Name.ToLower()
                    .Replace("fx_", "").Replace("fx ", "").Replace("fx-", "")
                    .Replace("vfx_", "");
                if (pNorm.Contains(normFunc) && normFunc.Length >= 4)
                {
                    score += 2;
                    break;
                }
            }
        }

        return score;
    }

    /// <summary>
    /// Infer which output pin a function most likely connects to, based on its name.
    /// Returns null if no confident match.
    /// </summary>
    private static string InferOutputForFunction(string funcName, List<string> availablePins)
    {
        if (availablePins.Count == 0) return null;

        var lower = funcName.ToLower();
        var tokens = Tokenize(lower);

        // Check all tokens against our keyword map
        string bestPin = null;
        var bestPriority = 0;

        foreach (var token in tokens)
        {
            if (OutputKeywords.TryGetValue(token, out var pin) && availablePins.Contains(pin))
            {
                // Prioritize specific pins over generic ones
                var priority = pin switch
                {
                    "OpacityMask" => 5,
                    "EmissiveColor" => 4,
                    "Normal" => 4,
                    "WorldPositionOffset" => 4,
                    "AmbientOcclusion" => 3,
                    "Metallic" or "Roughness" or "Specular" => 3,
                    _ => 2
                };
                if (priority > bestPriority)
                {
                    bestPin = pin;
                    bestPriority = priority;
                }
            }
        }

        // Also check full name substrings for compound words
        foreach (var (keyword, pin) in OutputKeywords)
        {
            if (!availablePins.Contains(pin)) continue;
            if (lower.Contains(keyword) && keyword.Length >= 4)
            {
                var priority = pin switch
                {
                    "OpacityMask" => 5,
                    "EmissiveColor" => 4,
                    _ => 2
                };
                if (priority > bestPriority)
                {
                    bestPin = pin;
                    bestPriority = priority;
                }
            }
        }

        return bestPin;
    }

    private static string[] Tokenize(string s)
    {
        // Split on underscore, dash, space, and camelCase boundaries
        var tokens = new List<string>();
        var parts = s.Split(['_', '-', ' '], StringSplitOptions.RemoveEmptyEntries);
        foreach (var part in parts)
        {
            // Split camelCase: "FlowNoise" → ["flow", "noise"]
            var current = new StringBuilder();
            for (var i = 0; i < part.Length; i++)
            {
                if (i > 0 && char.IsUpper(part[i]) && char.IsLower(part[i - 1]))
                {
                    if (current.Length > 0) tokens.Add(current.ToString().ToLower());
                    current.Clear();
                }
                current.Append(part[i]);
            }
            if (current.Length > 0) tokens.Add(current.ToString().ToLower());
        }
        return tokens.ToArray();
    }

    // -------------------------------------------------------------------
    // Parameter grouping by name prefix
    // -------------------------------------------------------------------

    /// <summary>
    /// Build a human-readable title for a parameter group.
    /// Infers the category from parameter types and names.
    /// </summary>
    private static string FormatGroupTitle(string groupKey, List<ParamInfo> parms)
    {
        // Determine dominant type
        var types = parms.GroupBy(p => p.Type).OrderByDescending(g => g.Count()).ToList();
        var dominantType = types.FirstOrDefault()?.Key ?? "param";
        var isMixed = types.Count > 1;

        // Build a readable name from the raw key
        var displayName = groupKey;

        // If it's a numeric ID, prefix it
        if (groupKey.All(char.IsDigit))
        {
            displayName = $"FX {groupKey}";
        }
        else
        {
            // Title-case the key and add spaces at camelCase boundaries
            var sb = new StringBuilder();
            for (var i = 0; i < groupKey.Length; i++)
            {
                if (i > 0 && char.IsUpper(groupKey[i]) && char.IsLower(groupKey[i - 1]))
                    sb.Append(' ');
                sb.Append(i == 0 ? char.ToUpper(groupKey[i]) : groupKey[i]);
            }
            displayName = sb.ToString().Replace('_', ' ');
        }

        // Add type label
        var typeLabel = isMixed ? "Parameters" : dominantType switch
        {
            "scalar" => "Scalars",
            "vector" => "Vectors",
            "texture" => "Textures",
            "switch" or "static switch" => "Switches",
            _ => "Parameters"
        };

        // Check if any are overrides
        var overrideCount = parms.Count(p => p.IsOverride);
        if (overrideCount > 0 && overrideCount < parms.Count)
            return $"{displayName} - {typeLabel} - {overrideCount} overridden";
        if (overrideCount == parms.Count)
            return $"{displayName} - {typeLabel} - overrides";

        return $"{displayName} - {typeLabel}";
    }

    private static string ExtractGroupKey(string name)
    {
        var parts = name.Split(['_', ' ', '-'], StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length == 0) return name.ToLower();

        // Detect numeric feature ID pattern: FX_103851_*, Open_FX_102341_*, etc.
        // Numeric IDs ≥ 4 digits are treated as feature identifiers
        foreach (var part in parts)
        {
            if (part.Length >= 4 && part.All(char.IsDigit))
                return part;
        }

        // Skip common short prefixes (FX, VFX, SM, MF) to reach the actual feature name
        var idx = 0;
        while (idx < parts.Length)
        {
            var p = parts[idx].ToLower();
            if (p is "fx" or "vfx" or "sm" or "mf")
            {
                idx++;
                continue;
            }
            break;
        }

        // Find first meaningful token (>= 3 chars, strip trailing digits)
        for (var i = idx; i < parts.Length; i++)
        {
            var cleaned = parts[i].TrimEnd("0123456789".ToCharArray()).ToLower();
            if (cleaned.Length >= 3) return cleaned;
        }

        // Short tokens: combine first two
        if (parts.Length >= 2)
            return (parts[0] + "_" + parts[1]).ToLower();
        return name.ToLower();
    }

    private static Dictionary<string, List<ParamInfo>> GroupByPrefix(List<ParamInfo> allParams)
    {
        var raw = new Dictionary<string, List<ParamInfo>>(StringComparer.OrdinalIgnoreCase);
        foreach (var p in allParams)
        {
            var key = ExtractGroupKey(p.Name);
            if (!raw.ContainsKey(key)) raw[key] = [];
            raw[key].Add(p);
        }

        // Merge groups where one key is a prefix of another (min 3 chars to prevent
        // single-letter keys like "v" from swallowing "vfx", "vs", etc.)
        var keys = raw.Keys.OrderBy(k => k.Length).ToList();
        var mergeTarget = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        for (var i = 0; i < keys.Count; i++)
        {
            if (mergeTarget.ContainsKey(keys[i])) continue;
            if (keys[i].Length < 3) continue; // Don't merge on short keys
            for (var j = i + 1; j < keys.Count; j++)
            {
                if (mergeTarget.ContainsKey(keys[j])) continue;
                if (keys[j].StartsWith(keys[i], StringComparison.OrdinalIgnoreCase))
                    mergeTarget[keys[j]] = keys[i];
            }
        }

        if (mergeTarget.Count > 0)
        {
            var merged = new Dictionary<string, List<ParamInfo>>(StringComparer.OrdinalIgnoreCase);
            foreach (var (key, list) in raw)
            {
                var target = mergeTarget.GetValueOrDefault(key, key);
                if (!merged.ContainsKey(target)) merged[target] = [];
                merged[target].AddRange(list);
            }
            raw = merged;
        }

        return raw;
    }


    // -------------------------------------------------------------------
    // Graph helpers
    // -------------------------------------------------------------------

    private static HashSet<int> FindReachable(ExprGraph graph)
    {
        var reachable = new HashSet<int>();

        if (graph.OutputEdges.Count > 0)
        {
            // BFS backwards from output-connected nodes
            var queue = new Queue<int>();
            foreach (var (_, nodeId) in graph.OutputEdges)
            {
                queue.Enqueue(nodeId);
                reachable.Add(nodeId);
            }

            // Build reverse adjacency: for each destination, list its sources
            var inputsOf = new Dictionary<int, List<int>>();
            foreach (var edge in graph.Edges)
            {
                if (!inputsOf.ContainsKey(edge.ToId))
                    inputsOf[edge.ToId] = [];
                inputsOf[edge.ToId].Add(edge.FromId);
            }

            while (queue.Count > 0)
            {
                var id = queue.Dequeue();
                if (!inputsOf.TryGetValue(id, out var sources)) continue;
                foreach (var src in sources)
                {
                    if (reachable.Add(src))
                        queue.Enqueue(src);
                }
            }
        }
        else
        {
            // No output edges known - include all nodes that participate in at least one edge
            foreach (var edge in graph.Edges)
            {
                reachable.Add(edge.FromId);
                reachable.Add(edge.ToId);
            }
            // Also include isolated parameter nodes (they might be important)
            foreach (var (id, node) in graph.Nodes)
            {
                if (node.IsParameter) reachable.Add(id);
            }
        }

        return reachable;
    }

    private static string BuildGenericLabel(UMaterialExpression expr, string shortType)
    {
        // Try to extract useful info from common expression types
        var type = expr.ExportType;

        // Constants
        if (type.Contains("Constant") && !type.Contains("Parameter"))
        {
            var r = expr.GetOrDefault<float>("R", float.NaN);
            if (!float.IsNaN(r))
            {
                var g = expr.GetOrDefault<float>("G", float.NaN);
                var b = expr.GetOrDefault<float>("B", float.NaN);
                if (!float.IsNaN(b)) return $"Const3<br/>{r:G}, {g:G}, {b:G}";
                if (!float.IsNaN(g)) return $"Const2<br/>{r:G}, {g:G}";
                return $"Const<br/>{r:G}";
            }

            var constant = expr.GetOrDefault<FLinearColor>("Constant");
            if (constant.R != 0 || constant.G != 0 || constant.B != 0 || constant.A != 0)
                return $"Const4<br/>{constant.Hex}";
        }

        // Material function calls
        if (type.Contains("MaterialFunctionCall"))
        {
            var funcRef = expr.GetOrDefault<FPackageIndex>("MaterialFunction");
            if (funcRef?.ResolvedObject != null)
                return $"MF: {funcRef.ResolvedObject.Name}";
        }

        // Texture objects / samples without parameter
        if (type.Contains("TextureObject") || type.Contains("TextureSample"))
        {
            var texRef = expr.GetOrDefault<FPackageIndex>("Texture");
            if (texRef?.ResolvedObject != null)
                return $"{shortType}<br/>{texRef.ResolvedObject.Name}";
        }

        // Comment nodes
        if (type.Contains("Comment"))
        {
            var text = expr.GetOrDefault<string>("Text", "");
            if (!string.IsNullOrEmpty(text))
                return $"// {(text.Length > 40 ? text[..40] + "..." : text)}";
        }

        return shortType;
    }

    // -------------------------------------------------------------------
    // FExpressionInput extraction
    // -------------------------------------------------------------------

    private static FExpressionInput ExtractExpressionInput(FPropertyTag prop)
    {
        var val = prop.Tag?.GenericValue;

        // Direct FExpressionInput (includes FMaterialInput<T> which inherits from it)
        if (val is FExpressionInput input)
            return input;

        // FStructFallback containing expression input data
        if (val is FStructFallback fb)
        {
            var expr = fb.GetOrDefault<FPackageIndex>("Expression");
            if (expr != null && expr.Index != 0)
            {
                return new FExpressionInput
                {
                    Expression = expr,
                    OutputIndex = fb.GetOrDefault<int>("OutputIndex"),
                    Mask = fb.GetOrDefault<int>("Mask"),
                    MaskR = fb.GetOrDefault<int>("MaskR"),
                    MaskG = fb.GetOrDefault<int>("MaskG"),
                    MaskB = fb.GetOrDefault<int>("MaskB"),
                    MaskA = fb.GetOrDefault<int>("MaskA"),
                };
            }
        }

        return null;
    }

    private static string GetSwizzle(FExpressionInput input)
    {
        if (input.Mask == 0) return null;
        var ch = "";
        if (input.MaskR != 0) ch += "R";
        if (input.MaskG != 0) ch += "G";
        if (input.MaskB != 0) ch += "B";
        if (input.MaskA != 0) ch += "A";
        return ch.Length > 0 && ch != "RGBA" ? "." + ch : null;
    }

    private static string FormatEdgeLabel(ExprEdge edge)
    {
        var parts = new List<string>();
        if (!string.IsNullOrEmpty(edge.InputName) && edge.InputName != "Input")
            parts.Add(edge.InputName);
        if (!string.IsNullOrEmpty(edge.Swizzle))
            parts.Add(edge.Swizzle);
        return string.Join(" ", parts);
    }

    // -------------------------------------------------------------------
    // Shared data extraction helpers
    // -------------------------------------------------------------------

    private static Dictionary<string, string> GetBasePropertyOverrideStrings(UMaterialInstance mi)
    {
        var result = new Dictionary<string, string>();
        if (!mi.TryGetValue(out FStructFallback bpo, "BasePropertyOverrides"))
            return result;

        var overrideFlags = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var prop in bpo.Properties)
        {
            if (prop.Name.Text.StartsWith("bOverride_", StringComparison.OrdinalIgnoreCase) &&
                prop.Tag?.GetValue(typeof(bool)) is true)
                overrideFlags.Add(prop.Name.Text["bOverride_".Length..]);
        }

        foreach (var prop in bpo.Properties)
        {
            var name = prop.Name.Text;
            if (name.StartsWith("bOverride_") || name.StartsWith("bUse")) continue;
            if (!overrideFlags.Contains(name)) continue;
            result[name] = prop.Tag?.GenericValue?.ToString() ?? "?";
        }
        return result;
    }

    private static List<string> GetConnectedOutputPins(FStructFallback cachedExprData)
    {
        var pins = new List<string>();
        if (cachedExprData == null) return pins;
        try
        {
            var mask = cachedExprData.GetOrDefault<int>("PropertyConnectedMask");
            if (mask == 0) return pins;
            for (var i = 0; i < OutputPinNames.Length; i++)
            {
                if ((mask & (1 << i)) != 0)
                    pins.Add(OutputPinNames[i]);
            }
        }
        catch { }
        return pins;
    }

    /// <summary>
    /// Get material functions preserving call order (NOT deduplicated).
    /// The order in FunctionInfos reflects the compilation/evaluation order,
    /// which reveals the processing pipeline.
    /// </summary>
    private static List<(string Name, string Path)> GetMaterialFunctions(FStructFallback cachedExprData)
    {
        var result = new List<(string, string)>();
        if (cachedExprData == null) return result;
        try
        {
            if (!cachedExprData.TryGetValue(out FStructFallback[] funcInfos, "FunctionInfos"))
                return result;
            foreach (var info in funcInfos)
            {
                if (info == null) continue;
                var funcIdx = info.GetOrDefault<FPackageIndex>("Function");
                if (funcIdx?.ResolvedObject == null) continue;
                var name = funcIdx.ResolvedObject.Name.Text;
                var path = funcIdx.ResolvedObject.GetPathName(true);
                result.Add((name, path));
            }
        }
        catch { }
        return result;
    }

    /// <summary>
    /// Extract texture parameter → texture asset name mapping from CachedExpressionData.
    /// Uses RuntimeEntries[3] (texture params) and TextureValues (parallel array of asset paths).
    /// </summary>
    private static List<(string ParamName, string AssetName)> GetTextureParameterMapping(FStructFallback cachedExprData)
    {
        var result = new List<(string, string)>();
        if (cachedExprData == null) return result;
        try
        {
            if (!cachedExprData.TryGetAllValues(out FStructFallback[] runtimeEntries, "RuntimeEntries"))
                return result;
            if (runtimeEntries.Length <= 3 || runtimeEntries[3] == null) return result;

            if (!runtimeEntries[3].TryGetValue(out FStructFallback[] texParamInfos, "ParameterInfoSet"))
                return result;

            // TextureValues is a parallel array of FSoftObjectPath
            if (!cachedExprData.TryGetValue(out FSoftObjectPath[] textureValues, "TextureValues"))
                return result;

            for (var i = 0; i < texParamInfos.Length; i++)
            {
                if (!texParamInfos[i].TryGetValue(out FName paramName, "Name")) continue;
                if (string.IsNullOrEmpty(paramName.Text)) continue;

                var assetName = "Unknown";
                if (i < textureValues.Length)
                {
                    var path = textureValues[i].AssetPathName.Text;
                    if (!string.IsNullOrEmpty(path))
                    {
                        // Extract asset name from path like "/Game/.../T_Armour_9_300.T_Armour_9_300"
                        var lastSlash = path.LastIndexOf('/');
                        var dotPos = path.IndexOf('.', lastSlash >= 0 ? lastSlash : 0);
                        assetName = dotPos >= 0 ? path[(lastSlash + 1)..dotPos] : path[(lastSlash + 1)..];
                    }
                }

                result.Add((paramName.Text, assetName));
            }
        }
        catch { }
        return result;
    }

    private static Dictionary<string, (int UVChannel, float Scale)> GetTextureStreamingData(UMaterialInterface mat)
    {
        var result = new Dictionary<string, (int, float)>(StringComparer.OrdinalIgnoreCase);
        foreach (var entry in mat.TextureStreamingData)
        {
            var texName = entry.TextureName.Text;
            if (!string.IsNullOrEmpty(texName) && !result.ContainsKey(texName))
                result[texName] = (entry.UVChannelIndex, entry.SamplingScale);
        }
        return result;
    }

    private static List<(string TypeName, List<string> Names, List<string> Defaults)> GetCachedParametersByType(
        FStructFallback cachedExprData)
    {
        var result = new List<(string, List<string>, List<string>)>();
        if (cachedExprData == null) return result;

        var typeMap = new Dictionary<int, string>
            { [0] = "Scalar", [1] = "Vector", [3] = "Texture", [7] = "Static Switch" };
        var scalarDefaults = TryGetArray<float>(cachedExprData, "ScalarValues");
        var vectorDefaults = TryGetArray<FLinearColor>(cachedExprData, "VectorValues");

        try
        {
            if (!cachedExprData.TryGetAllValues(out FStructFallback[] runtimeEntries, "RuntimeEntries"))
                return result;
            var scalarIdx = 0;
            var vectorIdx = 0;

            for (var i = 0; i < runtimeEntries.Length; i++)
            {
                if (!typeMap.TryGetValue(i, out var typeName)) continue;
                var entry = runtimeEntries[i];
                if (entry == null) continue;
                var names = new List<string>();
                var defaults = new List<string>();
                if (!entry.TryGetValue(out FStructFallback[] paramInfos, "ParameterInfoSet")) continue;
                foreach (var info in paramInfos)
                {
                    if (!info.TryGetValue(out FName name, "Name")) continue;
                    if (string.IsNullOrEmpty(name.Text)) continue;
                    names.Add(name.Text);
                    string defStr = null;
                    switch (i)
                    {
                        case 0 when scalarDefaults != null && scalarIdx < scalarDefaults.Length:
                            defStr = scalarDefaults[scalarIdx++].ToString("G");
                            break;
                        case 1 when vectorDefaults != null && vectorIdx < vectorDefaults.Length:
                            defStr = vectorDefaults[vectorIdx++].Hex;
                            break;
                    }
                    defaults.Add(defStr);
                }
                if (names.Count > 0) result.Add((typeName, names, defaults));
            }
        }
        catch { }
        return result;
    }

    // -------------------------------------------------------------------
    // Utility
    // -------------------------------------------------------------------

    private static T[] TryGetArray<T>(FStructFallback fallback, string name)
    {
        try { return fallback.TryGetValue(out T[] arr, name) ? arr : null; }
        catch { return null; }
    }

    private static string FormatColor(FVectorParameterValue p)
    {
        if (p.ParameterValue == null) return "null";
        var c = p.ParameterValue.Value;
        return $"{c.Hex} R:{c.R:F2} G:{c.G:F2} B:{c.B:F2} A:{c.A:F2}";
    }

    private static string GetShortPath(string fullPath)
    {
        if (string.IsNullOrEmpty(fullPath)) return "";
        var parts = fullPath.Split('/', '.');
        if (parts.Length <= 3) return fullPath;
        return ".../" + string.Join("/", parts.Skip(parts.Length - 3).Take(2));
    }

    private static string Esc(string s) =>
        s?.Replace("\"", "'") ?? "";

    private static string SanitizeId(string s) =>
        new string(s?.Select(c => char.IsLetterOrDigit(c) ? c : '_').ToArray() ?? []);
}
