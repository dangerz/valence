using System;
using System.Collections.Generic;
using System.Linq;
using Valence.Core;

namespace Valence.Viewer;

/// <summary>One outside call that appeared (or went away) in a class between two timeline samples.</summary>
public sealed record BoundaryChange(string NodeId, string Name, NodeData? Node, bool Added, EffectKind Kind, string Api);

/// <summary>
/// The Boundary lens over the git timeline: when and where code that reaches outside the program was
/// added or removed. Each timeline frame records every class's own outside calls ("kind api"); comparing
/// consecutive frames gives the changes. Frames are samples, so a change is "somewhere between the
/// previous sample and this commit".
/// </summary>
public static class BoundaryHistory
{
    private const int MaxCommits = 100;
    private const int MaxRowsPerCommit = 60;

    /// <summary>Each class's effect kinds at this frame.</summary>
    public static Dictionary<string, EffectFlags> Flags(TimelineFrame frame)
    {
        var flags = new Dictionary<string, EffectFlags>(frame.Effects.Count, StringComparer.Ordinal);
        foreach (var (id, effects) in frame.Effects)
        {
            var kinds = EffectFlags.None;
            foreach (var effect in effects) kinds |= Boundary.FlagOf(Parse(effect).Kind);
            if (kinds != EffectFlags.None) flags[id] = kinds;
        }
        return flags;
    }

    /// <summary>Outside calls added and removed since the previous frame (none for the first frame).</summary>
    public static List<BoundaryChange> Changes(TimelineFrame? previous, TimelineFrame frame, GraphModel model)
    {
        var changes = new List<BoundaryChange>();
        // An empty frame is a commit that couldn't be analyzed, not one where everything was deleted.
        if (previous == null || previous.Nodes.Count == 0 || frame.Nodes.Count == 0) return changes;
        var ids = new HashSet<string>(frame.Effects.Keys, StringComparer.Ordinal);
        ids.UnionWith(previous.Effects.Keys);
        foreach (var id in ids.OrderBy(i => i, StringComparer.Ordinal))
        {
            var now = frame.Effects.GetValueOrDefault(id) ?? new List<string>();
            var before = previous.Effects.GetValueOrDefault(id) ?? new List<string>();
            foreach (var effect in now.Except(before, StringComparer.Ordinal)) Add(id, effect, added: true);
            foreach (var effect in before.Except(now, StringComparer.Ordinal)) Add(id, effect, added: false);
        }
        return changes;

        void Add(string id, string effect, bool added)
        {
            var (kind, api) = Parse(effect);
            if (Boundary.Info(Boundary.FlagOf(kind)) is not { } info) return;
            var node = model.Nodes.GetValueOrDefault(id);
            changes.Add(new BoundaryChange(id, NameOf(id, node), node, added, info, api));
        }
    }

    /// <summary>Insights ▸ Outside Effects Over Time.</summary>
    public static Report Report(GraphModel model, Timeline? timeline)
    {
        if (timeline == null || timeline.Frames.Count == 0)
        {
            var none = new Report("Outside effects over time", "There's no timeline for this repo yet.");
            none.Section("How to get one").AddParagraph(
                "Timeline ▸ Build Timeline analyzes a sample of past commits. Then run this report again, or scrub the timeline with the Boundary lens on.");
            return none;
        }
        if (!timeline.HasEffects)
        {
            var old = new Report("Outside effects over time", "This timeline was built before Valence recorded outside effects.");
            old.Section("How to fix it").AddParagraph("Rebuild it (Timeline ▸ Build Timeline), then run this report again.");
            return old;
        }

        var frames = timeline.Frames;
        var history = new List<(int Index, TimelineFrame Frame, List<BoundaryChange> Changes)>();
        for (var i = 1; i < frames.Count; i++)
        {
            var changes = Changes(frames[i - 1], frames[i], model);
            if (changes.Count > 0) history.Add((i, frames[i], changes));
        }
        var added = history.Sum(h => h.Changes.Count(c => c.Added));
        var removed = history.Sum(h => h.Changes.Count(c => !c.Added));
        var first = frames[0];
        var last = frames[^1];
        var report = new Report("Outside effects over time",
            $"{frames.Count} sampled commits from {Day(first)} to {Day(last)}: outside calls were added {added} time{Plural(added)} " +
            $"and removed {removed} time{Plural(removed)}, at {history.Count} of the samples. Newest first.")
        {
            Spotlight = new HashSet<NodeData>(history.SelectMany(h => h.Changes).Select(c => c.Node).OfType<NodeData>()),
        };
        report.Section("Read this first").AddParagraph(
            "The timeline samples commits, so each change happened somewhere between the previous sample and the commit shown. " +
            "To see one in place, scrub the timeline to its frame with the Boundary lens on: classes that started an outside call glow, ones that stopped turn red.",
            muted: true);

        // When each kind of reach first showed up.
        var firstSeen = new List<List<ReportCell>>();
        var seen = BoundaryHistory.Kinds(first);
        if (seen != EffectFlags.None)
        {
            foreach (var kind in Boundary.In(seen))
            {
                firstSeen.Add(new List<ReportCell> { new(kind.Label, Color: kind.Color), new($"by the first sample ({Day(first)})"), new(""), new("") });
            }
        }
        foreach (var (index, frame, changes) in history)
        {
            foreach (var change in changes.Where(c => c.Added && (seen & c.Kind.Flag) == 0))
            {
                seen |= change.Kind.Flag;
                firstSeen.Add(new List<ReportCell>
                {
                    new(change.Kind.Label, Color: change.Kind.Color),
                    new($"{Day(frame)} · {Short(frame.Commit)} · frame {index + 1}"),
                    new(change.Name, change.Node),
                    new(change.Api),
                });
            }
        }
        report.Section("When each kind of reach first appeared").AddTable(new[] { "Effect", "First seen", "Class", "Call" }, firstSeen);

        foreach (var (index, frame, changes) in Enumerable.Reverse(history).Take(MaxCommits))
        {
            var plus = changes.Count(c => c.Added);
            var minus = changes.Count - plus;
            report.Section($"{Day(frame)} · {Short(frame.Commit)} · {frame.Subject}")
                .AddParagraph($"by {frame.Author} · frame {index + 1} of {frames.Count} · {plus} added, {minus} removed", muted: true)
                .AddTable(new[] { "", "Class", "Effect", "Call" }, changes
                    .OrderByDescending(c => c.Added)
                    .ThenBy(c => Array.IndexOf(Boundary.Kinds, c.Kind))
                    .ThenBy(c => c.Name, StringComparer.OrdinalIgnoreCase)
                    .Take(MaxRowsPerCommit)
                    .Select(c => new List<ReportCell>
                    {
                        c.Added ? new ReportCell("+ added", Color: Palette.Added) : new ReportCell("− removed", Color: Palette.Proton),
                        new(c.Name, c.Node),
                        new(c.Kind.Label, Color: c.Kind.Color),
                        new(c.Api),
                    }));
        }
        if (history.Count > MaxCommits)
        {
            report.Section("Older").AddParagraph($"{history.Count - MaxCommits} older samples with changes aren't listed.", muted: true);
        }
        return report;
    }

    /// <summary>Every effect kind in a frame.</summary>
    public static EffectFlags Kinds(TimelineFrame frame)
    {
        var all = EffectFlags.None;
        foreach (var flags in Flags(frame).Values) all |= flags;
        return all;
    }

    /// <summary>A class's name: from the graph, else from its id (it was deleted since).</summary>
    public static string NameOf(string id, NodeData? node)
    {
        if (node != null) return node.Info.Name;
        var colon = id.IndexOf(':');
        var name = colon >= 0 ? id[(colon + 1)..] : id;
        var paren = name.IndexOf('(');
        if (paren >= 0) name = name[..paren];
        var dot = name.LastIndexOf('.');
        return (dot >= 0 && dot < name.Length - 1 ? name[(dot + 1)..] : name) + " (deleted since)";
    }

    public static string Day(TimelineFrame frame) => frame.Date.ToLocalTime().ToString("yyyy-MM-dd");

    public static string Short(string commit) => commit.Length >= 7 ? commit[..7] : commit;

    /// <summary>"network requests.get" → ("network", "requests.get").</summary>
    private static (string Kind, string Api) Parse(string effect)
    {
        var space = effect.IndexOf(' ');
        return space < 0 ? (effect, "") : (effect[..space], effect[(space + 1)..]);
    }

    private static string Plural(int count) => count == 1 ? "" : "s";
}
