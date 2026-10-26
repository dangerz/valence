using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Godot;
using Valence.Core;
using Valence.Probe.CSharp;
using Valence.Schema;

namespace Valence.Viewer;

/// <summary>
/// Analyzes a source folder in-process (built-in C# probe + any external probes found) and
/// writes the graph to ~/.valence/graphs. The source folder itself is only read.
/// </summary>
public static class AnalyzerLauncher
{
    /// <summary>The repo checkout containing this Godot project (one level up from res://).</summary>
    private static string ToolsRoot => Path.GetFullPath(Path.Combine(ProjectSettings.GlobalizePath("res://"), ".."));

    /// <summary>Returns the written graph path. Blocking and may throw: call from a worker thread.</summary>
    public static string Analyze(string sourceFolder, Action<string> log)
    {
        // External probes are often "dotnet <dll>"; apps launched from the macOS Finder lack dotnet on PATH.
        if (string.IsNullOrEmpty(System.Environment.GetEnvironmentVariable("DOTNET_HOST_PATH")) && FindDotnet() is { } dotnet)
        {
            System.Environment.SetEnvironmentVariable("DOTNET_HOST_PATH", dotnet);
        }

        var options = new AnalyzeOptions();
        var external = ProbeRegistry.Discover(ProbeRegistry.SearchPaths(ExtraProbeDirs()), log);
        var probes = RepoAnalyzer.Combine(new IProbe[] { new CSharpProbe() }, external, options);

        var graph = RepoAnalyzer.Analyze(sourceFolder, probes, options, log);
        var outPath = RepoAnalyzer.DefaultOutputPath(graph.Repo.Root);
        ValenceJson.Write(outPath, graph);
        log($"saved {outPath}");
        return outPath;
    }

    /// <summary>Probes published by build.sh, if any (other languages later).</summary>
    private static IEnumerable<string> ExtraProbeDirs()
    {
        var dist = Path.Combine(ToolsRoot, "dist", "probes");
        return Directory.Exists(dist) ? new[] { dist } : Array.Empty<string>();
    }

    private static string? FindDotnet()
    {
        var home = System.Environment.GetFolderPath(System.Environment.SpecialFolder.UserProfile);
        var dotnetRoot = System.Environment.GetEnvironmentVariable("DOTNET_ROOT");
        var executable = OperatingSystem.IsWindows() ? "dotnet.exe" : "dotnet";
        var candidates = new List<string?>
        {
            string.IsNullOrEmpty(dotnetRoot) ? null : Path.Combine(dotnetRoot, executable),
            "/usr/local/share/dotnet/dotnet",
            "/opt/homebrew/bin/dotnet",
            "/usr/local/bin/dotnet",
            "/usr/share/dotnet/dotnet",
            "/usr/lib/dotnet/dotnet",
            Path.Combine(home, ".dotnet", executable),
            @"C:\Program Files\dotnet\dotnet.exe",
        };
        return candidates.FirstOrDefault(c => !string.IsNullOrEmpty(c) && File.Exists(c));
    }
}
