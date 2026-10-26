using Valence.Schema;

namespace Valence.Core;

public sealed class AnalyzeOptions
{
    public bool UseGit { get; set; } = true;
    public int ChurnDays { get; set; } = 90;
    public TimeSpan ExternalProbeTimeout { get; set; } = TimeSpan.FromMinutes(10);
}

/// <summary>
/// One entry point for the CLI and the viewer: run every relevant probe over a folder
/// (read-only), merge the results and add git history.
/// </summary>
public static class RepoAnalyzer
{
    /// <summary>~/.valence/graphs/&lt;folder&gt;.valence.json, which is never inside the analyzed repo.</summary>
    public static string DefaultOutputPath(string root) => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".valence", "graphs",
        $"{Path.GetFileName(Path.TrimEndingDirectorySeparator(root))}.valence.json");

    /// <summary>Built-in probes first; external probes with the same name as a built-in are skipped.</summary>
    public static List<IProbe> Combine(IEnumerable<IProbe> builtIn, IEnumerable<DiscoveredProbe> external, AnalyzeOptions options)
    {
        var probes = builtIn.ToList();
        var names = new HashSet<string>(probes.Select(p => p.Name), StringComparer.OrdinalIgnoreCase);
        foreach (var probe in external)
        {
            if (names.Add(probe.Manifest.Name)) probes.Add(new ExternalProbe(probe, options.ExternalProbeTimeout));
        }
        return probes;
    }

    public static Graph Analyze(string root, IReadOnlyList<IProbe> probes, AnalyzeOptions options, Action<string> log)
    {
        root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(root));
        if (!Directory.Exists(root)) throw new DirectoryNotFoundException($"Not a folder: {root}");

        var outputs = new List<ProbeOutput>();
        foreach (var probe in probes)
        {
            if (!probe.IsRelevant(root)) continue;
            log($"running probe '{probe.Name}' ({probe.Description})");
            try
            {
                if (probe.Run(root, log) is { } output) outputs.Add(output);
            }
            catch (Exception e)
            {
                log($"probe '{probe.Name}' failed: {e.Message}");
            }
        }

        if (outputs.Count == 0)
        {
            throw new InvalidOperationException(probes.Count == 0
                ? "No probes are available."
                : $"No probe found source files it understands in {root}.");
        }

        GitHistory? git = null;
        if (options.UseGit)
        {
            git = GitHistory.Open(root, log);
            git?.LoadChurn(options.ChurnDays, log);
        }

        var graph = GraphBuilder.Build(root, outputs, git, git != null ? options.ChurnDays : null);
        log($"{graph.Nodes.Count} classes, {graph.Edges.Count} dependencies, {graph.Modules.Count} modules");
        return graph;
    }
}
