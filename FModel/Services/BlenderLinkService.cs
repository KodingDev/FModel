using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;
using Microsoft.Win32;
using Serilog;

namespace FModel.Services;

public static class BlenderLinkService
{
    private const string AddonFileName = "blenderlink_addon.py";
    private const string AddonVersionMarker = "ADDON_VERSION = ";
    private const string EmbeddedResourceName = "FModel.Resources.blenderlink_addon.py";

    /// <summary>
    /// Returns the base Blender config directory: %APPDATA%/Blender
    /// </summary>
    private static string BlenderConfigRoot =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Blender Foundation", "Blender");

    /// <summary>
    /// Discover all Blender version directories that have a scripts/addons folder.
    /// </summary>
    public static List<BlenderInstall> DetectBlenderVersions()
    {
        var results = new List<BlenderInstall>();

        // Check both possible config roots (Blender 4.x+ and legacy)
        var roots = new[]
        {
            BlenderConfigRoot,
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Blender"),
        };

        foreach (var root in roots.Where(Directory.Exists))
        foreach (var dir in Directory.GetDirectories(root))
        {
            var versionName = Path.GetFileName(dir);
            var addonsDir = Path.Combine(dir, "scripts", "addons");

            // Only include versions that have the scripts/addons structure
            // (or where we could create it)
            var hasAddonsDir = Directory.Exists(addonsDir);
            var hasScriptsDir = Directory.Exists(Path.Combine(dir, "scripts"));

            // Accept any version dir that looks like a Blender version (X.Y or similar)
            if (!hasAddonsDir && !hasScriptsDir)
            {
                // Check if there are other Blender-like files in this directory
                var hasPrefs = File.Exists(Path.Combine(dir, "config", "userpref.blend"));
                if (!hasPrefs) continue;
            }

            var install = new BlenderInstall
            {
                Version = versionName,
                AddonsPath = addonsDir,
                AddonInstalled = File.Exists(Path.Combine(addonsDir, AddonFileName)),
            };

            if (install.AddonInstalled)
            {
                install.InstalledVersion = ReadInstalledVersion(Path.Combine(addonsDir, AddonFileName));
            }

            results.Add(install);
        }

        return results.OrderByDescending(x => x.Version).ToList();
    }

    /// <summary>
    /// Read the version string from an installed addon file.
    /// </summary>
    private static string ReadInstalledVersion(string filePath)
    {
        try
        {
            foreach (var line in File.ReadLines(filePath))
            {
                if (line.StartsWith(AddonVersionMarker))
                {
                    var val = line[AddonVersionMarker.Length..].Trim().Trim('"', '\'');
                    return val;
                }
            }
        }
        catch { }
        return "unknown";
    }

    /// <summary>
    /// Get the bundled addon version from the embedded resource.
    /// </summary>
    public static string GetBundledVersion()
    {
        try
        {
            using var stream = Assembly.GetExecutingAssembly().GetManifestResourceStream(EmbeddedResourceName);
            if (stream == null) return "unknown";
            using var reader = new StreamReader(stream);
            while (reader.ReadLine() is { } line)
            {
                if (line.StartsWith(AddonVersionMarker))
                {
                    return line[AddonVersionMarker.Length..].Trim().Trim('"', '\'');
                }
            }
        }
        catch { }
        return "unknown";
    }

    /// <summary>
    /// Install the addon to a specific Blender version's addons directory.
    /// Falls back to the %APPDATA% addons path if the primary path is not writable.
    /// </summary>
    public static bool InstallAddon(string addonsPath)
    {
        // Try primary path first, fall back to APPDATA if it fails (e.g. Program Files permissions)
        var pathsToTry = new List<string> { addonsPath };

        // Extract version from the path to build an APPDATA fallback
        // e.g. "C:\...\Blender 4.5\4.5\scripts\addons" -> version "4.5"
        var parts = addonsPath.Replace('\\', '/').Split('/');
        for (var i = 0; i < parts.Length; i++)
        {
            if (parts[i] == "scripts" && i > 0)
            {
                var version = parts[i - 1];
                if (version.Length > 0 && char.IsDigit(version[0]))
                {
                    var appDataFallback = Path.Combine(BlenderConfigRoot, version, "scripts", "addons");
                    if (appDataFallback != addonsPath)
                        pathsToTry.Add(appDataFallback);
                }
                break;
            }
        }

        foreach (var path in pathsToTry)
        {
            try
            {
                using var stream = Assembly.GetExecutingAssembly().GetManifestResourceStream(EmbeddedResourceName);
                if (stream == null)
                {
                    Log.Error("[BlenderLink] Embedded resource not found: {Name}", EmbeddedResourceName);
                    return false;
                }

                Directory.CreateDirectory(path);
                var targetPath = Path.Combine(path, AddonFileName);
                using var fs = File.Create(targetPath);
                stream.CopyTo(fs);

                Log.Information("[BlenderLink] Addon installed to {Path}", targetPath);
                return true;
            }
            catch (UnauthorizedAccessException)
            {
                Log.Warning("[BlenderLink] No write access to {Path}, trying fallback...", path);
                continue;
            }
            catch (Exception ex)
            {
                Log.Error(ex, "[BlenderLink] Failed to install addon to {Path}", path);
                return false;
            }
        }

        Log.Error("[BlenderLink] Failed to install addon to any path");
        return false;
    }

    /// <summary>
    /// Install the addon to all detected Blender versions.
    /// </summary>
    public static (int success, int failed) InstallAddonToAll()
    {
        var versions = DetectBlenderVersions();
        int success = 0, failed = 0;

        foreach (var v in versions)
        {
            if (InstallAddon(v.AddonsPath))
                success++;
            else
                failed++;
        }

        return (success, failed);
    }

    /// <summary>
    /// Get a human-readable status summary for the UI.
    /// </summary>
    public static string GetStatusText()
    {
        var versions = DetectBlenderVersions();
        if (versions.Count == 0)
            return "No Blender installations detected";

        var bundledVersion = GetBundledVersion();
        var installed = versions.Where(v => v.AddonInstalled).ToList();
        var notInstalled = versions.Where(v => !v.AddonInstalled).ToList();
        var outdated = installed.Where(v => v.InstalledVersion != bundledVersion).ToList();

        var parts = new List<string>();
        parts.Add($"Blender {string.Join(", ", versions.Select(v => v.Version))} detected");

        if (installed.Count == 0)
            parts.Add("plugin not installed");
        else if (outdated.Count > 0)
            parts.Add($"update available ({outdated.Count})");
        else if (notInstalled.Count > 0)
            parts.Add($"installed in {installed.Count}/{versions.Count}");
        else
            parts.Add($"installed (v{bundledVersion})");

        return string.Join(" — ", parts);
    }

    /// <summary>
    /// Determine the appropriate button label for the UI.
    /// </summary>
    public static string GetButtonLabel() => "Install Plugin";

    /// <summary>
    /// Derive the addons path from a blender.exe path.
    /// e.g. C:\Program Files\Blender Foundation\Blender 4.2\blender.exe
    ///   -> C:\Program Files\Blender Foundation\Blender 4.2\4.2\scripts\addons
    /// Falls back to %APPDATA%/Blender/{version}/scripts/addons if the local path doesn't exist.
    /// </summary>
    public static string GetAddonsPathFromExe(string blenderExePath)
    {
        var blenderDir = Path.GetDirectoryName(blenderExePath);
        if (string.IsNullOrEmpty(blenderDir))
            return "";

        // Look for a version subdirectory (e.g. "4.2", "3.6") next to blender.exe
        foreach (var sub in Directory.GetDirectories(blenderDir))
        {
            var subName = Path.GetFileName(sub);
            if (subName.Contains('.') && char.IsDigit(subName[0]))
            {
                // Prefer APPDATA path (always writable) over Program Files
                var appDataPath = Path.Combine(BlenderConfigRoot, subName, "scripts", "addons");
                return appDataPath;
            }
        }

        // Fallback: try to extract version from folder name like "Blender 4.2"
        var dirName = Path.GetFileName(blenderDir);
        var parts = dirName.Split(' ');
        foreach (var part in parts)
        {
            if (part.Contains('.') && char.IsDigit(part[0]))
            {
                var appDataPath = Path.Combine(BlenderConfigRoot, part, "scripts", "addons");
                return appDataPath;
            }
        }

        return "";
    }

    /// <summary>
    /// Try to find blender.exe automatically via the Windows registry and common install paths.
    /// </summary>
    public static string FindBlenderExe()
    {
        // 1. Check registry — file association
        try
        {
            using var key = Registry.ClassesRoot.OpenSubKey(@"blendfile\shell\open\command");
            var val = key?.GetValue(null) as string;
            if (!string.IsNullOrEmpty(val))
            {
                // Value looks like: "C:\...\blender.exe" "%1"
                var exePath = val.Split('"').FirstOrDefault(s => s.EndsWith("blender.exe", StringComparison.OrdinalIgnoreCase));
                if (!string.IsNullOrEmpty(exePath) && File.Exists(exePath))
                    return exePath;
            }
        }
        catch { }

        // 2. Check common install locations
        var programFiles = new[]
        {
            Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
            Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86),
        };

        foreach (var pf in programFiles)
        {
            var foundation = Path.Combine(pf, "Blender Foundation");
            if (!Directory.Exists(foundation)) continue;

            // Pick the newest version directory
            foreach (var dir in Directory.GetDirectories(foundation).OrderByDescending(d => d))
            {
                var exe = Path.Combine(dir, "blender.exe");
                if (File.Exists(exe)) return exe;
            }
        }

        // 3. Check PATH
        try
        {
            var pathDirs = Environment.GetEnvironmentVariable("PATH")?.Split(';') ?? [];
            foreach (var dir in pathDirs)
            {
                var exe = Path.Combine(dir.Trim(), "blender.exe");
                if (File.Exists(exe)) return exe;
            }
        }
        catch { }

        return "";
    }

    /// <summary>
    /// Enable the BlenderLink addon in Blender by running Blender in background mode.
    /// </summary>
    public static bool EnableAddon(string blenderExePath)
    {
        if (string.IsNullOrEmpty(blenderExePath) || !File.Exists(blenderExePath))
        {
            Log.Warning("[BlenderLink] Cannot enable addon — blender.exe not found at {Path}", blenderExePath);
            return false;
        }

        try
        {
            // Write a temp Python script — avoids quote-escaping issues with --python-expr
            var tempScript = Path.Combine(Path.GetTempPath(), "blenderlink_enable.py");
            File.WriteAllText(tempScript,
                "import bpy\n" +
                "bpy.ops.preferences.addon_enable(module='blenderlink_addon')\n" +
                "bpy.ops.wm.save_userpref()\n" +
                "print('[BlenderLink] Addon enabled successfully')\n");

            var psi = new ProcessStartInfo
            {
                FileName = blenderExePath,
                Arguments = $"--background --python \"{tempScript}\"",
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true,
            };

            using var process = Process.Start(psi);
            if (process == null) return false;

            process.WaitForExit(30_000); // 30s timeout
            var output = process.StandardOutput.ReadToEnd();
            var error = process.StandardError.ReadToEnd();

            Log.Information("[BlenderLink] Enable addon output: {Output}", output);
            if (!string.IsNullOrEmpty(error))
                Log.Warning("[BlenderLink] Enable addon stderr: {Error}", error);

            try { File.Delete(tempScript); } catch { }

            return process.ExitCode == 0 || output.Contains("Addon enabled successfully");
        }
        catch (Exception ex)
        {
            Log.Error(ex, "[BlenderLink] Failed to enable addon via Blender");
            return false;
        }
    }

    /// <summary>
    /// Full install + enable flow. Returns (installed, enabled).
    /// </summary>
    public static (bool installed, bool enabled) InstallAndEnable(string addonsPath, string blenderExePath)
    {
        var installed = InstallAddon(addonsPath);
        if (!installed) return (false, false);

        var enabled = EnableAddon(blenderExePath);
        return (true, enabled);
    }

    /// <summary>
    /// Full install + enable for all detected versions.
    /// </summary>
    public static (int installed, int enabled, int failed) InstallAndEnableAll(string blenderExeOverride = "")
    {
        var versions = DetectBlenderVersions();
        var blenderExe = !string.IsNullOrEmpty(blenderExeOverride) ? blenderExeOverride : FindBlenderExe();
        int installed = 0, enabled = 0, failed = 0;

        foreach (var v in versions)
        {
            if (InstallAddon(v.AddonsPath))
                installed++;
            else
            {
                failed++;
                continue;
            }
        }

        // Enable once — it applies to all versions sharing the same Blender exe
        if (installed > 0 && !string.IsNullOrEmpty(blenderExe))
        {
            if (EnableAddon(blenderExe))
                enabled = installed;
        }

        return (installed, enabled, failed);
    }
}

public class BlenderInstall
{
    public string Version { get; set; } = "";
    public string AddonsPath { get; set; } = "";
    public bool AddonInstalled { get; set; }
    public string InstalledVersion { get; set; } = "";
}
