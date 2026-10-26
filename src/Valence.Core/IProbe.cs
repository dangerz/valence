using Valence.Schema;

namespace Valence.Core;

/// <summary>
/// A language plugin. Built-in probes (like C#) run in-process; anything else is an
/// <see cref="ExternalProbe"/> described by a probe.json manifest and run as its own process.
/// Either way the output is the same <see cref="ProbeOutput"/>, and the repo must not be written to.
/// </summary>
public interface IProbe
{
    string Name { get; }

    /// <summary>Human-readable, e.g. "C# (Roslyn) 0.1.0, built in".</summary>
    string Description { get; }

    bool IsRelevant(string root);

    /// <summary>Returns null on failure (after logging why). Should honour context.Cancellation.</summary>
    ProbeOutput? Run(string root, AnalysisContext context);
}

/// <summary>A manifest-based probe run as a separate process (see docs/plugin-protocol.md).</summary>
public sealed class ExternalProbe : IProbe
{
    private readonly DiscoveredProbe _probe;
    private readonly TimeSpan _timeout;

    public ExternalProbe(DiscoveredProbe probe, TimeSpan timeout)
    {
        _probe = probe;
        _timeout = timeout;
    }

    public string Name => _probe.Manifest.Name;

    public string Description =>
        $"{_probe.Manifest.DisplayName} {_probe.Manifest.Version}, external: {_probe.Directory}";

    public bool IsRelevant(string root) =>
        _probe.Manifest.Extensions.Count == 0 || RepoFiles.ContainsAny(root, _probe.Manifest.Extensions);

    public ProbeOutput? Run(string root, AnalysisContext context)
    {
        // External probes report no progress of their own; show them as one step.
        var phase = $"Running {_probe.Manifest.DisplayName}";
        context.Report(phase, 0, force: true);
        var output = ProbeRunner.Run(_probe, root, _timeout, context.Log);
        context.Report(phase, 1, force: true);
        return output;
    }
}
