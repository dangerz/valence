using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using Godot;

namespace Valence.Viewer;

/// <summary>
/// Runs the Valence CLI (`dotnet valence.dll analyze <repo>`) from the viewer.
/// The CLI and probes only read the repo; the graph goes to ~/.valence/graphs.
/// </summary>
public static class AnalyzerLauncher
{
    /// <summary>The repo checkout containing this Godot project (one level up from res://).</summary>
    private static string ToolsRoot => Path.GetFullPath(Path.Combine(ProjectSettings.GlobalizePath("res://"), ".."));

    public static string? FindCli()
    {
        var fromEnv = System.Environment.GetEnvironmentVariable("VALENCE_CLI");
        if (!string.IsNullOrEmpty(fromEnv) && File.Exists(fromEnv)) return fromEnv;

        var candidates = new[]
        {
            Path.Combine(ToolsRoot, "dist", "valence.dll"),
            Path.Combine(ToolsRoot, "src", "Valence.Cli", "bin", "Release", "net8.0", "valence.dll"),
            Path.Combine(ToolsRoot, "src", "Valence.Cli", "bin", "Debug", "net8.0", "valence.dll"),
        };
        return candidates.FirstOrDefault(File.Exists);
    }

    /// <summary>Returns the written graph path, or null on failure. Blocking: call from a worker thread.</summary>
    public static string? Run(string cliDll, string repoDir, Action<string> log)
    {
        var dotnet = FindDotnet();
        var startInfo = new ProcessStartInfo(dotnet)
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        startInfo.ArgumentList.Add(cliDll);
        startInfo.ArgumentList.Add("analyze");
        startInfo.ArgumentList.Add(repoDir);
        foreach (var dir in ProbeDirs())
        {
            startInfo.ArgumentList.Add("--probes");
            startInfo.ArgumentList.Add(dir);
        }

        // Apps launched from the macOS Finder don't inherit the shell PATH; make sure the probes can find dotnet.
        var dotnetDir = Path.GetDirectoryName(dotnet);
        if (!string.IsNullOrEmpty(dotnetDir))
        {
            var path = System.Environment.GetEnvironmentVariable("PATH") ?? "";
            startInfo.Environment["PATH"] = dotnetDir + Path.PathSeparator + path;
            startInfo.Environment["DOTNET_HOST_PATH"] = dotnet;
        }

        using var process = new Process { StartInfo = startInfo };
        string? lastLine = null;
        process.OutputDataReceived += (_, e) =>
        {
            if (!string.IsNullOrWhiteSpace(e.Data)) lastLine = e.Data.Trim();
        };
        process.ErrorDataReceived += (_, e) =>
        {
            if (!string.IsNullOrWhiteSpace(e.Data)) log(e.Data);
        };

        log($"$ {dotnet} {string.Join(" ", startInfo.ArgumentList)}");
        process.Start();
        process.BeginOutputReadLine();
        process.BeginErrorReadLine();
        process.WaitForExit();
        return process.ExitCode == 0 ? lastLine : null;
    }

    private static IEnumerable<string> ProbeDirs()
    {
        var candidates = new[]
        {
            Path.Combine(ToolsRoot, "dist", "probes"),
            Path.Combine(ToolsRoot, "probes", "csharp", "bin", "Release", "net8.0"),
            Path.Combine(ToolsRoot, "probes", "csharp", "bin", "Debug", "net8.0"),
        };
        return candidates.Where(Directory.Exists);
    }

    private static string FindDotnet()
    {
        var home = System.Environment.GetFolderPath(System.Environment.SpecialFolder.UserProfile);
        var dotnetRoot = System.Environment.GetEnvironmentVariable("DOTNET_ROOT");
        var executable = OperatingSystem.IsWindows() ? "dotnet.exe" : "dotnet";
        var candidates = new List<string?>
        {
            System.Environment.GetEnvironmentVariable("DOTNET_HOST_PATH"),
            string.IsNullOrEmpty(dotnetRoot) ? null : Path.Combine(dotnetRoot, executable),
            "/usr/local/share/dotnet/dotnet",
            "/opt/homebrew/bin/dotnet",
            "/usr/local/bin/dotnet",
            "/usr/share/dotnet/dotnet",
            "/usr/lib/dotnet/dotnet",
            Path.Combine(home, ".dotnet", executable),
            @"C:\Program Files\dotnet\dotnet.exe",
        };
        return candidates.FirstOrDefault(c => !string.IsNullOrEmpty(c) && File.Exists(c)) ?? executable;
    }
}
