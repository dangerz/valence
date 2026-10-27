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

    /// <summary>
    /// Returns the written graph path. Blocking: call from a worker thread. Throws
    /// <see cref="OperationCanceledException"/> when the context is cancelled.
    /// </summary>
    public static string Analyze(string sourceFolder, AnalysisContext context)
    {
        // External probes are often "dotnet <dll>"; apps launched from the macOS Finder lack dotnet on PATH.
        if (string.IsNullOrEmpty(System.Environment.GetEnvironmentVariable("DOTNET_HOST_PATH")) && FindDotnet() is { } dotnet)
        {
            System.Environment.SetEnvironmentVariable("DOTNET_HOST_PATH", dotnet);
        }

        var options = new AnalyzeOptions();
        var external = ProbeRegistry.Discover(ProbeRegistry.SearchPaths(ExtraProbeDirs()), context.Log);
        var probes = RepoAnalyzer.Combine(new IProbe[] { new CSharpProbe() }, external, options);

        var graph = RepoAnalyzer.Analyze(sourceFolder, probes, options, context);
        var outPath = RepoAnalyzer.DefaultOutputPath(graph.Repo.Root);
        ValenceJson.Write(outPath, graph);
        context.Log($"saved {outPath}");
        return outPath;
    }

    /// <summary>
    /// Builds a git timeline (sampled commits exported to a temp folder and analyzed) and saves it to
    /// ~/.valence/timelines. Blocking: call from a worker thread.
    /// </summary>
    public static string BuildTimeline(string sourceFolder, int samples, AnalysisContext context)
    {
        var options = new AnalyzeOptions();
        var external = ProbeRegistry.Discover(ProbeRegistry.SearchPaths(ExtraProbeDirs()), context.Log);
        var probes = RepoAnalyzer.Combine(new IProbe[] { new CSharpProbe() }, external, options);
        var timeline = TimelineBuilder.Build(sourceFolder, probes, samples, context);
        var path = TimelineBuilder.DefaultPath(timeline.Root);
        ValenceJson.Write(path, timeline);
        context.Log($"saved {path}");
        return path;
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
