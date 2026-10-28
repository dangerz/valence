namespace Valence.Core;

internal sealed class FileHistory
{
    public HashSet<int> Commits { get; } = new();
    public long Churn { get; set; }
    public long LastChanged { get; set; }

    /// <summary>Changed lines by author name.</summary>
    public Dictionary<string, long> AuthorLines { get; } = new(StringComparer.Ordinal);

    /// <summary>Changed lines from commits an AI agent or bot authored or co-authored.</summary>
    public long AgentLines { get; set; }
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

    // Authors and co-authors that are AI agents or bots (matched case-insensitively in name or email).
    private static readonly string[] AgentMarkers =
    {
        "[bot]", "bot@", "dependabot", "renovate", "github-actions", "copilot", "claude", "anthropic",
        "openai", "codex", "chatgpt", "devin", "cursoragent", "cursor agent", "aider", "sweep", "gemini", "jules",
    };

    internal static bool IsAgent(string text) =>
        AgentMarkers.Any(marker => text.Contains(marker, StringComparison.OrdinalIgnoreCase));

    public void LoadChurn(int days, Action<string> log)
    {
        // @@<hash> <time>\t<author>\t<email>\t<co-authors>, then numstat lines.
        var output = Git(_root, "log", $"--since={days}.days.ago", "--numstat", "--no-renames",
            "--format=@@%H %at%x09%an%x09%ae%x09%(trailers:key=Co-authored-by,valueonly,separator=%x2C)", "--", ".");
        if (output == null)
        {
            log("git log failed; skipping churn");
            return;
        }

        var commitIndex = -1;
        long commitTime = 0;
        var author = "";
        var agent = false;
        foreach (var raw in output.Split('\n'))
        {
            var line = raw.TrimEnd('\r');
            if (line.Length == 0) continue;
            if (line.StartsWith("@@", StringComparison.Ordinal))
            {
                commitIndex++;
                var fields = line.Split('\t');
                var space = fields[0].LastIndexOf(' ');
                long.TryParse(fields[0][(space + 1)..], out commitTime);
                author = fields.Length > 1 ? fields[1] : "";
                var email = fields.Length > 2 ? fields[2] : "";
                var coAuthors = fields.Length > 3 ? fields[3] : "";
                agent = IsAgent(author) || IsAgent(email) || IsAgent(coAuthors);
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
            var lines = ParseCount(parts[0]) + ParseCount(parts[1]);
            file.Commits.Add(commitIndex);
            file.Churn += lines;
            file.LastChanged = Math.Max(file.LastChanged, commitTime);
            if (author.Length > 0) file.AuthorLines[author] = file.AuthorLines.GetValueOrDefault(author) + lines;
            if (agent) file.AgentLines += lines;
        }
        log($"git: {commitIndex + 1} commits in the last {days} days touched {Files.Count} files");
    }

    private static long ParseCount(string value) => long.TryParse(value, out var n) ? n : 0;

    internal sealed record CommitInfo(string Hash, DateTimeOffset Date, string Author, string Subject);

    /// <summary>Commits on the current branch's first-parent line that touch this folder, oldest first.</summary>
    internal static List<CommitInfo> FirstParentCommits(string root)
    {
        var output = Git(root, "log", "--first-parent", "--format=%H%x09%at%x09%an%x09%s", "--", ".");
        var commits = new List<CommitInfo>();
        if (output == null) return commits;
        foreach (var raw in output.Split('\n'))
        {
            var parts = raw.TrimEnd('\r').Split('\t', 4);
            if (parts.Length < 4 || !long.TryParse(parts[1], out var unix)) continue;
            commits.Add(new CommitInfo(parts[0], DateTimeOffset.FromUnixTimeSeconds(unix), parts[2], parts[3]));
        }
        commits.Reverse();
        return commits;
    }

    /// <summary>Path of <paramref name="root"/> relative to the git top level ("" or "sub/dir/").</summary>
    internal static string PrefixOf(string root) => (Git(root, "rev-parse", "--show-prefix") ?? "").Trim();

    /// <summary>
    /// Writes the folder's files as of <paramref name="commit"/> into <paramref name="target"/> using
    /// `git archive`, which only reads the repository: no checkout, no worktree, nothing in .git changes.
    /// </summary>
    internal static bool ExportTree(string root, string commit, string prefix, string target)
    {
        Directory.CreateDirectory(target);
        var tar = target.TrimEnd(Path.DirectorySeparatorChar) + ".tar";
        var treeish = string.IsNullOrEmpty(prefix) ? commit : $"{commit}:{prefix.TrimEnd('/')}";
        if (Git(root, "archive", "--format=tar", "-o", tar, treeish) == null || !File.Exists(tar)) return false;
        try
        {
            System.Formats.Tar.TarFile.ExtractToDirectory(tar, target, overwriteFiles: true);
            return true;
        }
        finally
        {
            File.Delete(tar);
        }
    }

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
