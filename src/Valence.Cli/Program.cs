using System.Diagnostics;
using Valence.Cli;
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

var probes = ProbeRegistry.Discover(ProbeRegistry.SearchPaths(probeDirs), Log);

switch (command)
{
    case "probes":
        if (probes.Count == 0) Log("no probes found (searched --probes, $VALENCE_PROBES, and probes/ next to valence)");
        foreach (var probe in probes)
        {
            var m = probe.Manifest;
            Console.WriteLine($"{m.Name,-12} {m.Version,-8} {string.Join(",", m.Extensions),-16} {probe.Directory}");
        }
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
    outPath ??= Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".valence", "graphs",
        $"{Path.GetFileName(root)}.valence.json");

    var stopwatch = Stopwatch.StartNew();
    var timeout = TimeSpan.FromMinutes(timeoutMinutes);
    var outputs = new List<ProbeOutput>();
    foreach (var probe in probes)
    {
        if (!ProbeRunner.IsRelevant(probe.Manifest, root)) continue;
        if (ProbeRunner.Run(probe, root, timeout, Log) is { } output) outputs.Add(output);
    }

    if (outputs.Count == 0)
    {
        Log(probes.Count == 0
            ? "no probes found; pass --probes <dir> or set $VALENCE_PROBES"
            : "no probe produced output for this repo");
        return 1;
    }

    GitHistory? git = null;
    if (useGit)
    {
        git = GitHistory.Open(root, Log);
        git?.LoadChurn(churnDays, Log);
    }

    var graph = GraphBuilder.Build(root, outputs, git, useGit ? churnDays : null);
    ValenceJson.Write(outPath, graph);

    Log($"{graph.Nodes.Count} nodes, {graph.Edges.Count} edges, {graph.Modules.Count} modules " +
        $"in {stopwatch.Elapsed.TotalSeconds:F1}s");
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
