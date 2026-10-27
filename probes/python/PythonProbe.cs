using Valence.Core;
using Valence.Schema;

namespace Valence.Probe.Python;

/// <summary>
/// Built-in Python probe, parsed in-process with tree-sitter (no Python install needed).
/// Currently a spike: structure only, no dependencies yet (see <see cref="PythonAnalyzer"/>).
/// </summary>
public sealed class PythonProbe : IProbe
{
    internal static readonly string[] Extensions = { ".py" };

    public string Name => "python";

    public string Description => $"Python (tree-sitter) {PythonAnalyzer.Version}, built in";

    public bool IsRelevant(string root) => RepoFiles.ContainsAny(root, Extensions);

    public ProbeOutput Run(string root, AnalysisContext context) => new PythonAnalyzer(root, context).Run();
}
