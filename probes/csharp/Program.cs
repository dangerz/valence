using System.Diagnostics;
using System.Text.Json;
using Valence.Probe.CSharp;
using Valence.Schema;

// Valence C# probe. Contract: see docs/plugin-protocol.md.
//   Valence.Probe.CSharp --root <repo> [--out <file>]
// Reads the repo only; writes nothing except the --out file (or stdout). Logs go to stderr.

string? root = null;
string? outPath = null;
for (var i = 0; i < args.Length; i++)
{
    switch (args[i])
    {
        case "--root" when i + 1 < args.Length:
            root = args[++i];
            break;
        case "--out" when i + 1 < args.Length:
            outPath = args[++i];
            break;
        case "--help" or "-h":
            PrintUsage();
            return 0;
        default:
            Console.Error.WriteLine($"unknown argument: {args[i]}");
            PrintUsage();
            return 2;
    }
}

if (root == null)
{
    PrintUsage();
    return 2;
}

root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(root));
if (!Directory.Exists(root))
{
    Console.Error.WriteLine($"not a directory: {root}");
    return 2;
}

var stopwatch = Stopwatch.StartNew();
try
{
    var output = new CSharpAnalyzer(root, message => Console.Error.WriteLine($"[csharp] {message}")).Run();
    if (outPath == null) Console.Out.Write(JsonSerializer.Serialize(output, ValenceJson.Options));
    else ValenceJson.Write(outPath, output);
    Console.Error.WriteLine(
        $"[csharp] {output.Nodes.Count} types, {output.Edges.Count} edges in {stopwatch.Elapsed.TotalSeconds:F1}s");
    return 0;
}
catch (Exception e)
{
    Console.Error.WriteLine($"[csharp] failed: {e}");
    return 1;
}

static void PrintUsage() =>
    Console.Error.WriteLine("usage: Valence.Probe.CSharp --root <repo> [--out <file>]");
