namespace Valence.Cli;

internal sealed class FileHistory
{
    public HashSet<int> Commits { get; } = new();
    public long Churn { get; set; }
    public long LastChanged { get; set; }
}

/// <summary>
/// Read-only git queries (rev-parse, log). GIT_OPTIONAL_LOCKS=0 stops git from
/// refreshing the index as a side effect, so the repo is left exactly as found.
/// </summary>
internal sealed class GitHistory
{
    private static readonly Dictionary<string, string> Env = new() { ["GIT_OPTIONAL_LOCKS"] = "0" };
    private static readonly TimeSpan Timeout = TimeSpan.FromMinutes(5);

    private readonly string _root;

    private GitHistory(string root, string prefix)
    {
        _root = root;
        Prefix = prefix;
    }

    /// <summary>Path of the analyzed root relative to the git top level ("" or "sub/dir/").</summary>
    public string Prefix { get; }
    public string? Commit { get; private set; }
    public string? Branch { get; private set; }

    /// <summary>Keyed by path relative to the analyzed root, forward slashes.</summary>
    public Dictionary<string, FileHistory> Files { get; } = new(StringComparer.Ordinal);

    public static GitHistory? Open(string root, Action<string> log)
    {
        var inside = Git(root, "rev-parse", "--is-inside-work-tree");
        if (inside == null || inside.Trim() != "true")
        {
            log("not a git repository; skipping history");
            return null;
        }
        var history = new GitHistory(root, (Git(root, "rev-parse", "--show-prefix") ?? "").Trim());
        history.Commit = Git(root, "rev-parse", "HEAD")?.Trim();
        history.Branch = Git(root, "rev-parse", "--abbrev-ref", "HEAD")?.Trim();
        return history;
    }

    public void LoadChurn(int days, Action<string> log)
    {
        var output = Git(_root, "log", $"--since={days}.days.ago", "--numstat", "--no-renames",
            "--format=@@%H %at", "--", ".");
        if (output == null)
        {
            log("git log failed; skipping churn");
            return;
        }

        var commitIndex = -1;
        long commitTime = 0;
        foreach (var raw in output.Split('\n'))
        {
            var line = raw.TrimEnd('\r');
            if (line.Length == 0) continue;
            if (line.StartsWith("@@", StringComparison.Ordinal))
            {
                commitIndex++;
                var space = line.LastIndexOf(' ');
                long.TryParse(line[(space + 1)..], out commitTime);
                continue;
            }

            // numstat: "<added>\t<deleted>\t<path>"; binary files use "-".
            var parts = line.Split('\t');
            if (parts.Length < 3 || commitIndex < 0) continue;
            var path = parts[2];
            if (Prefix.Length > 0)
            {
                if (!path.StartsWith(Prefix, StringComparison.Ordinal)) continue;
                path = path[Prefix.Length..];
            }

            if (!Files.TryGetValue(path, out var file))
            {
                file = new FileHistory();
                Files[path] = file;
            }
            file.Commits.Add(commitIndex);
            file.Churn += ParseCount(parts[0]) + ParseCount(parts[1]);
            file.LastChanged = Math.Max(file.LastChanged, commitTime);
        }
        log($"git: {commitIndex + 1} commits in the last {days} days touched {Files.Count} files");
    }

    private static long ParseCount(string value) => long.TryParse(value, out var n) ? n : 0;

    private static string? Git(string root, params string[] args)
    {
        try
        {
            var result = ProcessRunner.Run("git", new[] { "-C", root }.Concat(args), root, Timeout, environment: Env);
            return result.ExitCode == 0 ? result.StdOut : null;
        }
        catch (System.ComponentModel.Win32Exception)
        {
            return null; // git not installed
        }
    }
}
