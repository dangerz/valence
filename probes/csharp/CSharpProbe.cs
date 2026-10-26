using Valence.Core;
using Valence.Schema;

namespace Valence.Probe.CSharp;

/// <summary>
/// In-process entry point for the C# probe, used by the CLI and the viewer so they need no
/// separately built probe. The same analyzer also runs standalone via Program.cs (probe.json).
/// </summary>
public sealed class CSharpProbe : IProbe
{
    private static readonly string[] Extensions = { ".cs" };

    public string Name => "csharp";

    public string Description => $"C# (Roslyn) {CSharpAnalyzer.Version}, built in";

    public bool IsRelevant(string root) => RepoFiles.ContainsAny(root, Extensions);

    public ProbeOutput Run(string root, AnalysisContext context) => new CSharpAnalyzer(root, context).Run();
}
