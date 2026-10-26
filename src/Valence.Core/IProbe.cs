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

    /// <summary>Returns null on failure (after logging why).</summary>
    ProbeOutput? Run(string root, Action<string> log);
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

    public ProbeOutput? Run(string root, Action<string> log) => ProbeRunner.Run(_probe, root, _timeout, log);
}
