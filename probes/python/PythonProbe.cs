using Valence.Core;
using Valence.Schema;

namespace Valence.Probe.Python;

/// <summary>
/// Built-in Python probe, parsed in-process with tree-sitter (no Python install needed): classes and
/// files with free functions as nucleons, and bonds from statically resolved names
/// (see <see cref="PythonAnalyzer"/>). Member-level flow and entry points come later.
/// </summary>
public sealed class PythonProbe : IProbe
{
    internal static readonly string[] Extensions = { ".py" };

    public string Name => "python";

    public string Description => $"Python (tree-sitter) {PythonAnalyzer.Version}, built in";

    public bool IsRelevant(string root) => RepoFiles.ContainsAny(root, Extensions);

    public ProbeOutput Run(string root, AnalysisContext context) => new PythonAnalyzer(root, context).Run();
}
