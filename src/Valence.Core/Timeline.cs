using System.Security.Cryptography;
using System.Text;
using Valence.Schema;

namespace Valence.Core;

/// <summary>The repo at one commit: which classes existed, and how big they were.</summary>
public sealed class TimelineFrame
{
    public string Commit { get; set; } = "";
    public DateTimeOffset Date { get; set; }
    public string Author { get; set; } = "";
    public string Subject { get; set; } = "";

    /// <summary>Node id → lines of code at this commit.</summary>
    public Dictionary<string, double> Nodes { get; set; } = new();

    /// <summary>
    /// Node id → its outside calls at this commit, as "kind api" ("network requests.get"): what its own
    /// code (members and top level) calls that reaches outside the program. Only nodes that have any.
    /// </summary>
    public Dictionary<string, List<string>> Effects { get; set; } = new();
}

/// <summary>Sampled history of a repo, oldest frame first.</summary>
public sealed class Timeline
{
    public const string SchemaId = "valence.timeline/1";

    public string Schema { get; set; } = SchemaId;
    public string Root { get; set; } = "";
    public DateTimeOffset GeneratedAt { get; set; }

    /// <summary>Frames record outside effects (<see cref="TimelineFrame.Effects"/>); false for older timelines.</summary>
    public bool HasEffects { get; set; }

    public List<TimelineFrame> Frames { get; set; } = new();
}

/// <summary>
/// Builds a timeline from the repo's local git history. Each sampled commit is exported with
/// `git archive` into a temp folder (the repo and its working copy are never touched), analyzed
/// with the same probes as a normal analysis, and reduced to the set of classes it contains.
/// </summary>
public static class TimelineBuilder
{
    public static string DefaultPath(string root)
    {
        root = Path.TrimEndingDirectorySeparator(root);
        var hash = Convert.ToHexString(SHA1.HashData(Encoding.UTF8.GetBytes(root)))[..8].ToLowerInvariant();
        var name = Path.GetFileName(root);
        foreach (var bad in Path.GetInvalidFileNameChars()) name = name.Replace(bad, '_');
        return Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            ".valence", "timelines", $"{name}-{hash}.timeline.json");
    }

    /// <param name="samples">How many commits to analyze, spread evenly from first to latest.</param>
    /// <exception cref="OperationCanceledException">When the context is cancelled.</exception>
    public static Timeline Build(string root, IReadOnlyList<IProbe> probes, int samples, AnalysisContext context)
    {
        root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(root));
        context.Report("Reading git history", 0, force: true);
        var commits = GitHistory.FirstParentCommits(root);
        if (commits.Count == 0) throw new InvalidOperationException("No git history found for this folder.");
        var picks = Sample(commits, Math.Max(2, samples));
        context.Log($"timeline: {commits.Count} commits; analyzing {picks.Count} of them");

        var prefix = GitHistory.PrefixOf(root);
        var timeline = new Timeline { Root = root, GeneratedAt = DateTimeOffset.UtcNow, HasEffects = true };
        var options = new AnalyzeOptions { UseGit = false };
        var scratch = Path.Combine(Path.GetTempPath(), "valence-timeline-" + Guid.NewGuid().ToString("N")[..8]);
        try
        {
            for (var i = 0; i < picks.Count; i++)
            {
                context.Cancellation.ThrowIfCancellationRequested();
                var commit = picks[i];
                var slice = context.Slice((double)i / picks.Count, 1.0 / picks.Count);
                var title = $"{commit.Date:yyyy-MM-dd} {commit.Hash[..7]} {commit.Subject}";
                slice.Report($"Commit {i + 1} of {picks.Count}: {title}", 0, force: true);

                var frame = new TimelineFrame
                {
                    Commit = commit.Hash,
                    Date = commit.Date,
                    Author = commit.Author,
                    Subject = commit.Subject,
                };
                var dir = Path.Combine(scratch, i.ToString());
                try
                {
                    if (!GitHistory.ExportTree(root, commit.Hash, prefix, dir))
                    {
                        context.Log($"timeline: could not export {commit.Hash[..7]}; skipping");
                        continue;
                    }
                    var graph = RepoAnalyzer.Analyze(dir, probes, options, slice);
                    foreach (var node in graph.Nodes)
                    {
                        frame.Nodes[node.Id] = node.Metrics.GetValueOrDefault(MetricKeys.Loc);
                        var effects = (node.Effects ?? Enumerable.Empty<EffectInfo>())
                            .Concat(node.Members.SelectMany(m => m.Effects ?? Enumerable.Empty<EffectInfo>()))
                            .Select(e => $"{e.Kind} {e.Api}")
                            .Distinct(StringComparer.Ordinal)
                            .OrderBy(e => e, StringComparer.Ordinal)
                            .ToList();
                        if (effects.Count > 0) frame.Effects[node.Id] = effects;
                    }
                }
                catch (Exception e) when (e is not OperationCanceledException)
                {
                    // Early commits often have no source files a probe understands yet, and some can't
                    // be exported here at all (names the OS rejects, odd symlinks): an empty frame,
                    // not a failed timeline.
                    context.Log($"timeline: {commit.Hash[..7]}: {e.Message}");
                }
                finally
                {
                    TryDelete(dir);
                }
                timeline.Frames.Add(frame);
            }
        }
        finally
        {
            TryDelete(scratch);
        }
        context.Report("Done", 1, force: true);
        return timeline;
    }

    private static List<GitHistory.CommitInfo> Sample(List<GitHistory.CommitInfo> commits, int samples)
    {
        if (commits.Count <= samples) return commits;
        var picks = new List<GitHistory.CommitInfo>();
        var last = -1;
        for (var i = 0; i < samples; i++)
        {
            var index = (int)Math.Round(i * (commits.Count - 1) / (double)(samples - 1));
            if (index == last) continue;
            picks.Add(commits[index]);
            last = index;
        }
        return picks;
    }

    private static void TryDelete(string path)
    {
        try
        {
            if (Directory.Exists(path)) Directory.Delete(path, recursive: true);
        }
        catch (IOException)
        {
            // Temp cleanup is best effort.
        }
        catch (UnauthorizedAccessException)
        {
            // Same.
        }
    }
}
