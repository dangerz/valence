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
    private const double ProbeShare = 0.92;

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

    /// <exception cref="OperationCanceledException">When the context is cancelled.</exception>
    public static Graph Analyze(string root, IReadOnlyList<IProbe> probes, AnalyzeOptions options, AnalysisContext context)
    {
        root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(root));
        if (!Directory.Exists(root)) throw new DirectoryNotFoundException($"Not a folder: {root}");

        context.Report("Detecting languages", 0, force: true);
        var relevant = probes.Where(p => p.IsRelevant(root)).ToList();
        context.Cancellation.ThrowIfCancellationRequested();
        if (relevant.Count == 0)
        {
            throw new InvalidOperationException(probes.Count == 0
                ? "No probes are available."
                : $"No probe found source files it understands in {root}.");
        }

        var outputs = new List<ProbeOutput>();
        var share = ProbeShare / relevant.Count;
        for (var i = 0; i < relevant.Count; i++)
        {
            var probe = relevant[i];
            context.Log($"running probe '{probe.Name}' ({probe.Description})");
            try
            {
                if (probe.Run(root, context.Slice(i * share, share)) is { } output) outputs.Add(output);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception e)
            {
                context.Log($"probe '{probe.Name}' failed: {e.Message}");
            }
            context.Cancellation.ThrowIfCancellationRequested();
        }

        if (outputs.Count == 0) throw new InvalidOperationException("Every probe failed; see the log.");

        GitHistory? git = null;
        if (options.UseGit)
        {
            context.Report("Reading git history", ProbeShare, force: true);
            git = GitHistory.Open(root, context.Log);
            git?.LoadChurn(options.ChurnDays, context.Log);
        }
        context.Cancellation.ThrowIfCancellationRequested();

        context.Report("Building graph", 0.98, force: true);
        var graph = GraphBuilder.Build(root, outputs, git, git != null ? options.ChurnDays : null);
        context.Log($"{graph.Nodes.Count} types, {graph.Nodes.Sum(n => n.Members.Count)} members, " +
                    $"{graph.Edges.Count} type dependencies, {graph.MemberEdges.Count} member links, " +
                    $"{graph.EntryPoints.Count} entry points");
        context.Report("Done", 1, force: true);
        return graph;
    }
}
