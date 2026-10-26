using System.Diagnostics;
using Valence.Core;
using Valence.Probe.CSharp;
using Valence.Schema;

// valence — runs language probes over a repo (read-only) and writes a merged graph for the viewer.
//
//   valence analyze <repo> [--out <file>] [--probes <dir>]... [--churn-days <n>] [--no-git] [--timeout <min>]
//   valence probes [--probes <dir>]...

if (args.Length == 0 || args[0] is "--help" or "-h" or "help")
{
    PrintUsage();
    return args.Length == 0 ? 2 : 0;
}

var command = args[0];
var positional = new List<string>();
var probeDirs = new List<string>();
string? outPath = null;
var churnDays = 90;
var useGit = true;
var timeoutMinutes = 10;

for (var i = 1; i < args.Length; i++)
{
    var arg = args[i];
    string Next() => i + 1 < args.Length ? args[++i] : throw new ArgumentException($"{arg} needs a value");
    try
    {
        switch (arg)
        {
            case "--out" or "-o":
                outPath = Next();
                break;
            case "--probes":
                probeDirs.Add(Next());
                break;
            case "--churn-days":
                churnDays = int.Parse(Next());
                break;
            case "--timeout":
                timeoutMinutes = int.Parse(Next());
                break;
            case "--no-git":
                useGit = false;
                break;
            default:
                if (arg.StartsWith('-')) throw new ArgumentException($"unknown option {arg}");
                positional.Add(arg);
                break;
        }
    }
    catch (Exception e) when (e is ArgumentException or FormatException)
    {
        Console.Error.WriteLine(e.Message);
        return 2;
    }
}

void Log(string message) => Console.Error.WriteLine(message);

var options = new AnalyzeOptions
{
    UseGit = useGit,
    ChurnDays = churnDays,
    ExternalProbeTimeout = TimeSpan.FromMinutes(timeoutMinutes),
};
// C# is built in; other languages come from probe.json manifests (--probes, $VALENCE_PROBES, probes/).
var probes = RepoAnalyzer.Combine(
    new IProbe[] { new CSharpProbe() },
    ProbeRegistry.Discover(ProbeRegistry.SearchPaths(probeDirs), Log),
    options);

switch (command)
{
    case "probes":
        foreach (var probe in probes) Console.WriteLine($"{probe.Name,-12} {probe.Description}");
        return 0;

    case "analyze":
        return Analyze();

    default:
        Log($"unknown command '{command}'");
        PrintUsage();
        return 2;
}

int Analyze()
{
    if (positional.Count != 1)
    {
        Log("analyze needs exactly one repo path");
        return 2;
    }

    var root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(positional[0]));
    if (!Directory.Exists(root))
    {
        Log($"not a directory: {root}");
        return 2;
    }

    // Never write into the repo being analyzed unless the user explicitly points --out there.
    outPath ??= RepoAnalyzer.DefaultOutputPath(root);

    var stopwatch = Stopwatch.StartNew();
    Graph graph;
    try
    {
        graph = RepoAnalyzer.Analyze(root, probes, options, Log);
    }
    catch (Exception e) when (e is InvalidOperationException or DirectoryNotFoundException)
    {
        Log(e.Message);
        return 1;
    }
    ValenceJson.Write(outPath, graph);

    Log($"done in {stopwatch.Elapsed.TotalSeconds:F1}s");
    Console.WriteLine(Path.GetFullPath(outPath));
    return 0;
}

static void PrintUsage() => Console.Error.WriteLine("""
    usage:
      valence analyze <repo> [--out <file>] [--probes <dir>]... [--churn-days <n>] [--no-git] [--timeout <min>]
      valence probes [--probes <dir>]...

    Default output: ~/.valence/graphs/<repo>.valence.json (the analyzed repo is never written to).
    The graph path is printed to stdout; progress goes to stderr.
    """);
