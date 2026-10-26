using Valence.Schema;

namespace Valence.Cli;

internal static class ProbeRunner
{
    private static readonly HashSet<string> SkipDirs = new(StringComparer.OrdinalIgnoreCase)
    {
        "bin", "obj", "node_modules", "packages", "dist", "build", "artifacts",
    };

    /// <summary>True when the repo has at least one file with one of the probe's extensions.</summary>
    public static bool IsRelevant(ProbeManifest manifest, string root)
    {
        if (manifest.Extensions.Count == 0) return true;
        var extensions = new HashSet<string>(manifest.Extensions, StringComparer.OrdinalIgnoreCase);

        var pending = new Stack<string>();
        pending.Push(root);
        while (pending.Count > 0)
        {
            var dir = pending.Pop();
            try
            {
                if (Directory.EnumerateFiles(dir).Any(f => extensions.Contains(Path.GetExtension(f)))) return true;
                foreach (var sub in Directory.EnumerateDirectories(dir))
                {
                    var name = Path.GetFileName(sub);
                    if (name.StartsWith('.') || SkipDirs.Contains(name)) continue;
                    if (new DirectoryInfo(sub).LinkTarget != null) continue;
                    pending.Push(sub);
                }
            }
            catch (Exception e) when (e is UnauthorizedAccessException or IOException)
            {
                // Unreadable folder: skip it.
            }
        }
        return false;
    }

    /// <summary>
    /// GUI-launched processes (e.g. the Godot viewer on macOS) often lack dotnet on PATH,
    /// so a manifest's "dotnet" reuses the host that is running the CLI when possible.
    /// </summary>
    private static string ResolveExecutable(string name)
    {
        if (!string.Equals(name, "dotnet", StringComparison.OrdinalIgnoreCase)) return name;
        var host = Environment.GetEnvironmentVariable("DOTNET_HOST_PATH");
        if (!string.IsNullOrEmpty(host) && File.Exists(host)) return host;
        var self = Environment.ProcessPath;
        if (self != null && Path.GetFileNameWithoutExtension(self).Equals("dotnet", StringComparison.OrdinalIgnoreCase))
        {
            return self;
        }
        return name;
    }

    public static ProbeOutput? Run(DiscoveredProbe probe, string root, TimeSpan timeout, Action<string> log)
    {
        var manifest = probe.Manifest;
        var outFile = Path.Combine(Path.GetTempPath(), $"valence-{manifest.Name}-{Guid.NewGuid():N}.json");
        var command = manifest.Command.Select(part => part.Replace("{dir}", probe.Directory)).ToList();
        var arguments = command.Skip(1).Concat(new[] { "--root", root, "--out", outFile });

        log($"running probe '{manifest.Name}' ({manifest.DisplayName} {manifest.Version})");
        try
        {
            var result = ProcessRunner.Run(
                ResolveExecutable(command[0]), arguments, probe.Directory, timeout, line => log($"  {line}"));
            if (result.TimedOut)
            {
                log($"probe '{manifest.Name}' timed out after {timeout.TotalMinutes:F0} min");
                return null;
            }
            if (result.ExitCode != 0)
            {
                log($"probe '{manifest.Name}' exited with code {result.ExitCode}");
                return null;
            }
            if (!File.Exists(outFile))
            {
                log($"probe '{manifest.Name}' wrote no output file");
                return null;
            }

            var output = ValenceJson.Read<ProbeOutput>(outFile);
            if (output.Schema != SchemaIds.ProbeOutput)
            {
                log($"probe '{manifest.Name}' produced unsupported schema '{output.Schema}'");
                return null;
            }
            return output;
        }
        catch (System.ComponentModel.Win32Exception e)
        {
            log($"could not start probe '{manifest.Name}' ({command[0]}): {e.Message}");
            return null;
        }
        finally
        {
            try
            {
                File.Delete(outFile);
            }
            catch (IOException)
            {
                // Temp file cleanup is best effort.
            }
        }
    }
}
