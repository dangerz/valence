using System;
using System.Collections.Generic;
using System.Linq;
using Godot;
using Valence.Schema;

namespace Valence.Viewer;

/// <summary>The Insights menu's reports.</summary>
public enum InsightKind
{
    BlastFromSelection,
    BlastFromComparison,
    BlastFromRecentChanges,
    UnreachedCode,
    Cycles,
    Coverage,
    Abstractions,
    SystemMetrics,
    ReadingGuide,
    Boundary,
    BoundaryHistory,
}

/// <summary>
/// Codebase insights from the graph, for people who didn't write the code: what entry points reach
/// (and what nothing reaches), how much of the linking is inferred, and the Insights menu's reports
/// (blast radius, unreached code, cycles, coverage, abstractions, system metrics, reading guide).
/// Computed from data the graph already has; nothing here reads the repo.
/// </summary>
public static class Insights
{
    private const int MaxRows = 300;
    private const int RecentDays = 14;

    private static readonly HashSet<string> ControlKinds = new(StringComparer.Ordinal)
    {
        MemberEdgeKinds.Calls, MemberEdgeKinds.Creates, MemberEdgeKinds.Raises, MemberEdgeKinds.Triggers,
    };

    private static readonly string[] TriggerOrder =
    {
        Triggers.Startup, Triggers.Frame, Triggers.Physics, Triggers.Draw, Triggers.Input,
        Triggers.Event, Triggers.Request, Triggers.Job, Triggers.Shutdown,
    };

    // ---------------------------------------------------------------- computed at load

    /// <summary>Which classes each trigger's entry points reach, over control links (calls, creates, events).</summary>
    public static void ComputeReach(GraphModel model)
    {
        model.ReachByTrigger.Clear();
        model.Reached.Clear();
        var entries = model.Graph.EntryPoints;
        if (entries.Count == 0) return;

        var memberLevel = model.Graph.MemberEdges.Count > 0;
        var adjacency = new Dictionary<string, List<string>>(StringComparer.Ordinal);
        if (memberLevel)
        {
            foreach (var edge in model.Graph.MemberEdges)
            {
                if (!ControlKinds.Contains(edge.Kind)) continue;
                if (!adjacency.TryGetValue(edge.Source, out var list)) adjacency[edge.Source] = list = new List<string>();
                list.Add(edge.Target);
            }
        }

        foreach (var group in entries.GroupBy(e => e.Trigger))
        {
            var reached = new HashSet<NodeData>();
            if (memberLevel)
            {
                var visited = new HashSet<string>(group.Select(e => e.Member), StringComparer.Ordinal);
                var queue = new Queue<string>(visited);
                while (queue.Count > 0)
                {
                    var member = queue.Dequeue();
                    if (model.MemberOwners.TryGetValue(member, out var owner)) reached.Add(owner);
                    if (!adjacency.TryGetValue(member, out var next)) continue;
                    foreach (var target in next)
                    {
                        if (visited.Add(target)) queue.Enqueue(target);
                    }
                }
            }
            else
            {
                // Class level (older graphs): follow calls and creates from the entry points' classes.
                var queue = new Queue<NodeData>();
                foreach (var entry in group)
                {
                    if (model.MemberOwners.TryGetValue(entry.Member, out var owner) && reached.Add(owner)) queue.Enqueue(owner);
                }
                while (queue.Count > 0)
                {
                    foreach (var edge in queue.Dequeue().Outgoing)
                    {
                        if (edge.Kind is not (EdgeKinds.Calls or EdgeKinds.Creates)) continue;
                        if (model.Nodes.TryGetValue(edge.Target, out var target) && reached.Add(target)) queue.Enqueue(target);
                    }
                }
            }
            model.ReachByTrigger[group.Key] = reached;
            model.Reached.UnionWith(reached);
        }
    }

    /// <summary>Per class: the share of its links (in and out) that are inferred rather than certain.</summary>
    public static void ComputeTrust(GraphModel model)
    {
        foreach (var node in model.Nodes.Values)
        {
            var links = 0;
            var inferred = 0;
            foreach (var edge in node.Outgoing.Concat(node.Incoming))
            {
                if (edge.Kind == EdgeKinds.Contains) continue;
                links++;
                if (edge.Confidence is not (null or Confidence.Certain)) inferred++;
            }
            node.LinkCount = links;
            node.InferredShare = links == 0 ? 0f : (float)inferred / links;
        }
    }

    /// <summary>Triggers that reach a class, in the usual order (for the inspector).</summary>
    public static List<string> TriggersReaching(GraphModel model, NodeData node) =>
        OrderedTriggers(model).Where(t => model.ReachByTrigger[t].Contains(node)).ToList();

    private static List<string> OrderedTriggers(GraphModel model) => model.ReachByTrigger.Keys
        .OrderBy(t => Array.IndexOf(TriggerOrder, t) is var i && i >= 0 ? i : TriggerOrder.Length)
        .ThenBy(t => t, StringComparer.Ordinal)
        .ToList();

    // ---------------------------------------------------------------- blast radius

    /// <summary>Recently changed classes: last changed within two weeks of the snapshot.</summary>
    public static List<NodeData> RecentlyChanged(GraphModel model)
    {
        var since = model.SnapshotTime - RecentDays * 86400L;
        return model.Nodes.Values.Where(n => n.Metric(MetricKeys.LastChanged) >= since && n.Metric(MetricKeys.LastChanged) > 0).ToList();
    }

    /// <summary>Everything that depends on the changed classes (directly or not), and the entry points that reach them.</summary>
    public static Report BlastRadius(GraphModel model, IReadOnlyCollection<NodeData> changed, string from)
    {
        if (changed.Count == 0)
        {
            var empty = new Report("Blast radius", $"Nothing to start from: {from}.");
            empty.Section("How to use it").AddParagraph(
                "Select a class (or open a system), compare with a snapshot, or analyze a repo with recent git history, then run Insights ▸ Blast Radius again.");
            return empty;
        }

        // Upstream over class links: who depends on the changed classes, hop by hop.
        var hops = new Dictionary<NodeData, int>();
        var via = new Dictionary<NodeData, HashSet<string>>();
        var queue = new Queue<NodeData>();
        foreach (var node in changed)
        {
            hops[node] = 0;
            queue.Enqueue(node);
        }
        while (queue.Count > 0)
        {
            var node = queue.Dequeue();
            foreach (var edge in node.Incoming)
            {
                if (edge.Kind == EdgeKinds.Contains || !model.Nodes.TryGetValue(edge.Source, out var dependent)) continue;
                if (hops[node] == 0)
                {
                    if (!via.TryGetValue(dependent, out var kinds)) via[dependent] = kinds = new HashSet<string>(StringComparer.Ordinal);
                    kinds.Add(edge.Kind);
                }
                if (hops.ContainsKey(dependent)) continue;
                hops[dependent] = hops[node] + 1;
                queue.Enqueue(dependent);
            }
        }
        var affected = hops.Where(kv => kv.Value > 0).Select(kv => kv.Key).ToList();
        var entries = EntryPointsReaching(model, changed);
        var systems = hops.Keys.GroupBy(n => n.Atom).ToList();

        var report = new Report("Blast radius",
            $"From {from}: {changed.Count} class{Plural(changed.Count, "es")} → {affected.Count} class{Plural(affected.Count, "es")} " +
            $"in {systems.Count} system{Plural(systems.Count)} depend on them, directly or indirectly; " +
            $"{entries.Count} entry point{Plural(entries.Count)} can reach the change. Re-test those first.")
        {
            Spotlight = new HashSet<NodeData>(hops.Keys),
        };

        report.Section($"Changed ({changed.Count})").AddList(changed
            .OrderBy(n => n.Info.Name, StringComparer.OrdinalIgnoreCase)
            .Take(MaxRows)
            .Select(n => new ReportCell($"{n.Info.Name}  ({n.Atom.Name})", n)));

        var direct = affected.Where(n => hops[n] == 1).OrderByDescending(n => n.FanIn).ToList();
        report.Section($"Depend on them directly ({direct.Count})").AddTable(
            new[] { "Class", "System", "Through", "Used by" },
            direct.Take(MaxRows).Select(n => new List<ReportCell>
            {
                new(n.Info.Name, n),
                new(n.Atom.Name),
                new(string.Join(", ", via.GetValueOrDefault(n) ?? new HashSet<string>())),
                new(n.FanIn.ToString()),
            }));

        var further = affected.Where(n => hops[n] > 1).ToList();
        if (further.Count > 0)
        {
            report.Section($"Further out ({further.Count})")
                .AddTable(new[] { "Hops away", "Classes" }, further.GroupBy(n => hops[n]).OrderBy(g => g.Key)
                    .Select(g => new List<ReportCell> { new(g.Key.ToString()), new(g.Count().ToString()) }))
                .AddParagraph("Most depended on among them:", muted: true)
                .AddList(further.OrderByDescending(n => n.FanIn).Take(15).Select(n => new ReportCell($"{n.Info.Name}  ({hops[n]} hops, used by {n.FanIn})", n)));
        }

        report.Section("Systems affected").AddTable(new[] { "System", "Changed", "Affected" }, systems
            .Select(g => (Atom: g.Key, Changed: g.Count(n => hops[n] == 0), Affected: g.Count(n => hops[n] > 0)))
            .OrderByDescending(s => s.Changed + s.Affected)
            .Take(MaxRows)
            .Select(s => new List<ReportCell> { new(s.Atom.Name), new(s.Changed.ToString()), new(s.Affected.ToString()) }));

        var entrySection = report.Section($"Entry points that reach the change ({entries.Count})");
        if (entries.Count == 0)
        {
            entrySection.AddParagraph(model.Graph.EntryPoints.Count == 0
                ? "This graph has no entry points (re-analyze to get them)."
                : "None of the entry points reach the changed classes.", muted: true);
        }
        else
        {
            entrySection.AddTable(new[] { "Trigger", "Entry point" }, entries
                .OrderBy(e => Array.IndexOf(TriggerOrder, e.Trigger)).ThenBy(e => e.Label, StringComparer.OrdinalIgnoreCase)
                .Take(MaxRows)
                .Select(e => new List<ReportCell>
                {
                    new(e.Trigger),
                    new(e.Label, model.MemberOwners.GetValueOrDefault(e.Member)),
                }));
        }
        return report;
    }

    /// <summary>Entry points whose flow reaches any of these classes (upstream over member links).</summary>
    private static List<EntryPointInfo> EntryPointsReaching(GraphModel model, IReadOnlyCollection<NodeData> targets)
    {
        if (model.Graph.EntryPoints.Count == 0) return new List<EntryPointInfo>();
        if (model.Graph.MemberEdges.Count == 0)
        {
            // Class level: entry points whose reach includes a target.
            return model.Graph.EntryPoints
                .Where(e => model.ReachByTrigger.TryGetValue(e.Trigger, out var reached) && targets.Any(reached.Contains))
                .ToList();
        }

        var callers = new Dictionary<string, List<string>>(StringComparer.Ordinal);
        foreach (var edge in model.Graph.MemberEdges)
        {
            if (!ControlKinds.Contains(edge.Kind)) continue;
            if (!callers.TryGetValue(edge.Target, out var list)) callers[edge.Target] = list = new List<string>();
            list.Add(edge.Source);
        }
        var visited = new HashSet<string>(targets.SelectMany(n => n.Info.Members).Select(m => m.Id), StringComparer.Ordinal);
        var queue = new Queue<string>(visited);
        while (queue.Count > 0)
        {
            if (!callers.TryGetValue(queue.Dequeue(), out var list)) continue;
            foreach (var caller in list)
            {
                if (visited.Add(caller)) queue.Enqueue(caller);
            }
        }
        return model.Graph.EntryPoints.Where(e => visited.Contains(e.Member)).ToList();
    }

    // ---------------------------------------------------------------- unreached code

    public static Report UnreachedCode(GraphModel model)
    {
        if (model.Graph.EntryPoints.Count == 0)
        {
            var none = new Report("Unreached code", "This graph has no entry points, so reachability can't be worked out.");
            none.Section("Why").AddParagraph(
                "Entry points come from the probes: Godot callbacks, __main__ blocks, routes, commands, handlers… Re-analyze the source (File ▸ Re-analyze) with a current Valence.");
            return none;
        }

        var unreached = model.Nodes.Values.Where(n => !model.Reached.Contains(n)).ToList();
        var unused = unreached.Where(n => n.FanIn == 0).ToList();
        var orphaned = unreached.Where(n => n.FanIn > 0).ToList();
        var triggers = OrderedTriggers(model);
        var report = new Report("Unreached code",
            $"{unreached.Count} of {model.Nodes.Count} classes ({Percent(unreached.Count, model.Nodes.Count)}) are never reached from the " +
            $"{model.Graph.EntryPoints.Count} entry points ({string.Join(", ", triggers)}).")
        {
            Spotlight = new HashSet<NodeData>(unreached),
        };
        report.Section("Read this first").AddParagraph(
            "Unreached isn't proof of dead code: reflection, dependency injection, serialization, scene files, tests and " +
            "framework callbacks the probes don't model can all call code statically invisible here. Treat it as a list to check.", muted: true);

        report.Section($"Nothing uses it ({unused.Count})").AddParagraph("Not reached, and no class depends on it: the most likely to be dead or wired up by name.", muted: true)
            .AddTable(new[] { "Class", "System", "Lines" }, ByAtomThenSize(unused).Take(MaxRows).Select(n => new List<ReportCell>
            {
                new(n.Info.Name, n), new(n.Atom.Name), new(Num(n.Metric(MetricKeys.Loc))),
            }));
        report.Section($"Used only by unreached code ({orphaned.Count})").AddParagraph("Something depends on it, but nothing an entry point reaches.", muted: true)
            .AddTable(new[] { "Class", "System", "Used by", "Lines" }, ByAtomThenSize(orphaned).Take(MaxRows).Select(n => new List<ReportCell>
            {
                new(n.Info.Name, n), new(n.Atom.Name), new(n.FanIn.ToString()), new(Num(n.Metric(MetricKeys.Loc))),
            }));
        if (unreached.Count > MaxRows) report.Section("More").AddParagraph($"Lists show the first {MaxRows}; use the Reach lens or Show Only These to see them all.", muted: true);
        return report;
    }

    private static IEnumerable<NodeData> ByAtomThenSize(IEnumerable<NodeData> nodes) => nodes
        .OrderBy(n => n.Atom.Name, StringComparer.OrdinalIgnoreCase)
        .ThenByDescending(n => n.Metric(MetricKeys.Loc));

    // ---------------------------------------------------------------- cycles

    /// <summary>Each two-way bond between systems, with the lighter direction: the links to cut.</summary>
    public static Report Cycles(GraphModel model)
    {
        var cycles = model.Bonds.Where(b => b.Covalent).OrderByDescending(b => b.Weight).ToList();
        var report = new Report("Cycles",
            cycles.Count == 0
                ? "No cycles between systems: every bond goes one way."
                : $"{cycles.Count} pair{Plural(cycles.Count)} of systems depend on each other. For each, the lighter direction is the cheaper one to cut.");
        var spotlight = new HashSet<NodeData>();
        foreach (var bond in cycles)
        {
            var (from, to, light, heavy) = bond.WeightAB <= bond.WeightBA
                ? (bond.A, bond.B, bond.WeightAB, bond.WeightBA)
                : (bond.B, bond.A, bond.WeightBA, bond.WeightAB);
            var links = from.Nodes
                .SelectMany(n => n.Outgoing.Select(e => (Source: n, Edge: e)))
                .Where(x => x.Edge.Kind != EdgeKinds.Contains && model.Nodes.TryGetValue(x.Edge.Target, out var t) && t.Atom == to)
                .GroupBy(x => (x.Source, Target: model.Nodes[x.Edge.Target]))
                .Select(g => (g.Key.Source, g.Key.Target, Kinds: string.Join(", ", g.Select(x => x.Edge.Kind).Distinct()), Weight: g.Sum(x => x.Edge.Weight)))
                .OrderByDescending(l => l.Weight)
                .ToList();
            foreach (var link in links)
            {
                spotlight.Add(link.Source);
                spotlight.Add(link.Target);
            }
            report.Section($"{bond.A.Name} ⇄ {bond.B.Name}")
                .AddParagraph($"{to.Name} → {from.Name}: {heavy} links. {from.Name} → {to.Name}: {light} links " +
                              $"across {links.Count} class pair{Plural(links.Count, "s")}; moving or inverting these breaks the cycle.")
                .AddTable(new[] { $"In {from.Name}", $"Depends on (in {to.Name})", "As", "Links" }, links.Take(MaxRows).Select(l => new List<ReportCell>
                {
                    new(l.Source.Info.Name, l.Source), new(l.Target.Info.Name, l.Target), new(l.Kinds), new(l.Weight.ToString()),
                }));
        }
        if (spotlight.Count > 0) report.Spotlight = spotlight;
        return report;
    }

    // ---------------------------------------------------------------- coverage

    /// <summary>Systems × triggers: how many of each system's classes each trigger's entry points reach.</summary>
    public static Report Coverage(GraphModel model)
    {
        if (model.ReachByTrigger.Count == 0)
        {
            return new Report("Coverage matrix", "This graph has no entry points; re-analyze the source to get them.");
        }
        var triggers = OrderedTriggers(model);
        var atoms = model.AtomList
            .Select(a => (Atom: a, Counts: triggers.Select(t => a.Nodes.Count(model.ReachByTrigger[t].Contains)).ToList()))
            .Where(x => x.Counts.Any(c => c > 0))
            .OrderByDescending(x => x.Counts.Sum())
            .ToList();
        var untouched = model.AtomList.Count - atoms.Count;
        var report = new Report("Coverage matrix",
            $"What each trigger runs: {atoms.Count} of {model.AtomList.Count} systems are reached by at least one trigger" +
            (untouched > 0 ? $"; {untouched} by none." : "."));

        var header = new List<string> { "System" };
        header.AddRange(triggers);
        header.Add("Classes");
        report.Section("Reached classes per system").AddParagraph(
            "Each cell: how many of the system's classes that trigger's entry points reach; the shade is the share of the system.", muted: true)
            .AddTable(header, atoms.Take(MaxRows).Select(x =>
            {
                var row = new List<ReportCell> { new(x.Atom.Name) };
                foreach (var count in x.Counts)
                {
                    var share = x.Atom.Nodes.Count == 0 ? 0f : (float)count / x.Atom.Nodes.Count;
                    row.Add(count == 0 ? new ReportCell("·", Color: Palette.MutedText) : new ReportCell(count.ToString(), Background: new Color(Palette.Heat(share), 0.35f + 0.4f * share)));
                }
                row.Add(new ReportCell(x.Atom.Nodes.Count.ToString()));
                return row;
            }));

        var startupOnly = atoms.Where(x => triggers.Select((t, i) => (t, i)).All(p => x.Counts[p.i] == 0 || p.t is Triggers.Startup or Triggers.Shutdown)).ToList();
        if (startupOnly.Count > 0 && triggers.Any(t => t is not (Triggers.Startup or Triggers.Shutdown)))
        {
            report.Section("Only at startup or shutdown").AddList(startupOnly.Select(x => new ReportCell(x.Atom.Name)));
        }
        return report;
    }

    // ---------------------------------------------------------------- abstractions

    public static Report Abstractions(GraphModel model)
    {
        var abstractions = model.Nodes.Values.Where(IsAbstract).ToList();
        var implementations = abstractions.ToDictionary(a => a, a => a.Incoming
            .Where(e => e.Kind is EdgeKinds.Implements or EdgeKinds.Inherits)
            .Select(e => model.Nodes.GetValueOrDefault(e.Source))
            .Where(n => n != null)
            .Cast<NodeData>()
            .Distinct()
            .ToList());
        var single = abstractions.Where(a => implementations[a].Count == 1).ToList();
        var none = abstractions.Where(a => implementations[a].Count == 0).ToList();

        var report = new Report("Abstractions",
            $"{abstractions.Count} interfaces and abstract classes: {single.Count} with a single implementation, " +
            $"{none.Count} with none in the repo.");
        report.Section($"Single implementation ({single.Count})").AddParagraph(
            "Indirection with one answer: fine for a seam you mean to test or swap, ceremony otherwise.", muted: true)
            .AddTable(new[] { "Abstraction", "Its only implementation", "System" }, single.OrderBy(a => a.Info.Name, StringComparer.OrdinalIgnoreCase).Take(MaxRows)
                .Select(a => new List<ReportCell> { new(a.Info.Name, a), new(implementations[a][0].Info.Name, implementations[a][0]), new(a.Atom.Name) }));
        report.Section("Most implemented").AddTable(new[] { "Abstraction", "Implementations", "System" }, abstractions
            .Where(a => implementations[a].Count > 1)
            .OrderByDescending(a => implementations[a].Count).Take(20)
            .Select(a => new List<ReportCell> { new(a.Info.Name, a), new(implementations[a].Count.ToString()), new(a.Atom.Name) }));
        report.Section($"No implementation in the repo ({none.Count})").AddParagraph(
            "Implemented outside the repo, unused, or implemented in ways the probe can't see.", muted: true)
            .AddList(none.OrderBy(a => a.Info.Name, StringComparer.OrdinalIgnoreCase).Take(MaxRows).Select(a => new ReportCell($"{a.Info.Name}  ({a.Atom.Name})", a)));

        // Calls that may run any of many methods (virtual dispatch fanned out by the probe).
        var members = model.Nodes.Values.SelectMany(n => n.Info.Members).GroupBy(m => m.Id).ToDictionary(g => g.Key, g => g.First());
        var wide = model.Graph.MemberEdges
            .Where(e => e.Kind == MemberEdgeKinds.Calls && members.ContainsKey(e.Target))
            .GroupBy(e => (e.Source, Name: members[e.Target].Name))
            .Select(g => (g.Key.Source, g.Key.Name, Targets: g.Select(e => e.Target).Distinct().Count()))
            .Where(x => x.Targets >= 4)
            .OrderByDescending(x => x.Targets)
            .Take(40)
            .ToList();
        report.Section($"Wide dispatch ({wide.Count})").AddParagraph(
            "Calls that could run any of 4+ implementations: where reading the code won't tell you what actually runs.", muted: true)
            .AddTable(new[] { "Caller", "Calls", "Possible targets" }, wide.Select(x =>
            {
                var owner = model.MemberOwners.GetValueOrDefault(x.Source);
                var caller = owner != null && members.TryGetValue(x.Source, out var m) ? $"{owner.Info.Name}.{m.Name}" : x.Source;
                return new List<ReportCell> { new(caller, owner), new(x.Name), new(x.Targets.ToString()) };
            }));
        return report;
    }

    public static bool IsAbstract(NodeData node) =>
        node.Info.Kind == NodeKinds.Interface
        || node.Info.Attributes.TryGetValue(AttributeKeys.Abstract, out var value) && value == "true";

    // ---------------------------------------------------------------- system metrics

    /// <summary>Robert Martin's package metrics per system: coupling in/out, instability, abstractness, distance.</summary>
    public static Report SystemMetrics(GraphModel model)
    {
        var rows = model.AtomList.Select(atom =>
        {
            var bonds = model.Bonds.Where(b => b.A == atom || b.B == atom).ToList();
            var efferent = bonds.Count(b => b.WeightFrom(atom) > 0);
            var afferent = bonds.Count(b => b.WeightFrom(b.Other(atom)) > 0);
            var instability = afferent + efferent == 0 ? 0f : (float)efferent / (afferent + efferent);
            var abstractness = atom.Nodes.Count == 0 ? 0f : (float)atom.Nodes.Count(IsAbstract) / atom.Nodes.Count;
            var distance = Math.Abs(abstractness + instability - 1f);
            var zone = abstractness < 0.3f && instability < 0.3f && afferent > 0 ? "rigid: concrete, many depend on it"
                : abstractness > 0.7f && instability > 0.7f ? "abstract, but little depends on it"
                : "";
            return (Atom: atom, Ca: afferent, Ce: efferent, I: instability, A: abstractness, D: distance, Zone: zone);
        }).OrderByDescending(r => r.D).ThenByDescending(r => r.Ca).ToList();

        var report = new Report("System metrics",
            "Per system: how many systems depend on it (in) and it depends on (out), instability I = out/(in+out), " +
            "abstractness A = share of interfaces/abstract classes, and distance D = |A + I − 1| from the healthy balance.");
        report.Section("How to read it").AddParagraph(
            "Stable systems (low I) that many depend on should be abstract; unstable ones (high I) can be concrete. " +
            "High D marks systems off that line: rigid concrete foundations that are painful to change, or abstractions nobody uses.", muted: true);
        report.Section("Systems, furthest from balance first").AddTable(
            new[] { "System", "Classes", "In", "Out", "I", "A", "D", "Note" },
            rows.Take(MaxRows).Select(r => new List<ReportCell>
            {
                new(r.Atom.Name), new(r.Atom.Nodes.Count.ToString()), new(r.Ca.ToString()), new(r.Ce.ToString()),
                new(r.I.ToString("0.00")), new(r.A.ToString("0.00")),
                new(r.D.ToString("0.00"), Color: Palette.Heat(r.D)),
                new(r.Zone, Color: Palette.MutedText),
            }));
        return report;
    }

    // ---------------------------------------------------------------- reading guide

    /// <summary>A Markdown-friendly tour: where to start reading code you didn't write.</summary>
    public static Report ReadingGuide(GraphModel model)
    {
        var repo = model.Graph.Repo;
        var languages = string.Join(", ", model.Graph.Probes.Select(p => p.Name).Distinct());
        var report = new Report($"Reading guide: {repo.Name}",
            $"{model.Molecules.Count} projects, {model.AtomList.Count} systems and {model.Nodes.Count} classes " +
            $"({languages}); {model.Graph.EntryPoints.Count} entry points; {model.Bonds.Count(b => b.Covalent)} cycles between systems.");

        var start = report.Section("Where to start");
        var firstSteps = new List<ReportCell>();
        if (model.Graph.EntryPoints.Count > 0)
        {
            firstSteps.Add(new ReportCell($"Entry points: what makes the code run ({string.Join(", ", OrderedTriggers(model))}); see below."));
        }
        var core = model.Nodes.Values.OrderByDescending(n => n.FanIn).Where(n => n.FanIn > 0).Take(5).ToList();
        if (core.Count > 0) firstSteps.Add(new ReportCell($"The core classes most others use: {string.Join(", ", core.Select(n => n.Info.Name))}."));
        firstSteps.Add(new ReportCell("Then the systems from the foundations up (below): each only depends on systems listed before it, except in cycles."));
        start.AddList(firstSteps, numbered: true);

        if (model.Graph.EntryPoints.Count > 0)
        {
            var entries = report.Section("Entry points");
            foreach (var trigger in OrderedTriggers(model))
            {
                var list = model.Graph.EntryPoints.Where(e => e.Trigger == trigger).OrderBy(e => e.Label, StringComparer.OrdinalIgnoreCase).ToList();
                entries.AddParagraph($"{trigger} ({list.Count})");
                entries.AddList(list.Take(15).Select(e => new ReportCell(e.Label, model.MemberOwners.GetValueOrDefault(e.Member))));
            }
        }

        report.Section("Systems, from the foundations up").AddTable(new[] { "Layer", "System", "Classes", "Key classes" }, model.AtomList
            .OrderBy(a => a.Layer).ThenBy(a => a.Name, StringComparer.OrdinalIgnoreCase)
            .Take(MaxRows)
            .Select(a => new List<ReportCell>
            {
                new(a.Layer.ToString()), new(a.Name), new(a.Nodes.Count.ToString()),
                new(string.Join(", ", a.Nodes.OrderByDescending(n => n.FanIn).Take(3).Select(n => n.Info.Name))),
            }));

        if (model.TopRisks.Count > 0)
        {
            report.Section("Hot spots (Risk lens)").AddList(model.TopRisks.Select(n =>
                new ReportCell($"{n.Info.Name}  (risk {n.Risk.Score:0.00}, used by {n.FanIn}, {Num(n.Metric(MetricKeys.Churn))} lines churned)", n)));
        }
        var cycles = model.Bonds.Where(b => b.Covalent).OrderByDescending(b => b.Weight).ToList();
        if (cycles.Count > 0)
        {
            report.Section("Cycles to know about").AddList(cycles.Take(20).Select(b => new ReportCell($"{b.A.Name} ⇄ {b.B.Name} ({b.WeightAB} / {b.WeightBA} links)")));
        }
        report.Section("Biggest classes").AddList(model.Nodes.Values.OrderByDescending(n => n.Metric(MetricKeys.Loc)).Take(10)
            .Select(n => new ReportCell($"{n.Info.Name}  ({Num(n.Metric(MetricKeys.Loc))} lines, {n.Atom.Name})", n)));

        var agentWritten = model.Nodes.Values.Where(n => n.Metric(MetricKeys.AgentShare) >= 0.5 && n.Metric(MetricKeys.Churn) > 0)
            .OrderByDescending(n => n.Metric(MetricKeys.Churn)).Take(15).ToList();
        if (agentWritten.Count > 0)
        {
            report.Section("Mostly changed by AI agents or bots").AddParagraph(
                $"At least half the lines changed in the last {repo.ChurnWindowDays} days came from agent or bot commits: worth a human read.", muted: true)
                .AddList(agentWritten.Select(n => new ReportCell(
                    $"{n.Info.Name}  ({n.Metric(MetricKeys.AgentShare):P0} of {Num(n.Metric(MetricKeys.Churn))} lines)", n)));
        }
        return report;
    }

    // ---------------------------------------------------------------- what reaches outside (the Boundary lens)

    /// <summary>
    /// Which code reaches outside the program (network, files, processes, environment, databases, native
    /// and dynamic code), what each entry point ends up touching, and the calls that do it, by kind.
    /// </summary>
    public static Report BoundaryReport(GraphModel model)
    {
        var boundary = model.Boundary;
        if (!boundary.Any)
        {
            var none = new Report("What reaches outside", "This graph records no outside effects.");
            none.Section("Why").AddParagraph(
                "Either the code never calls the network, files, processes, environment or databases, or the graph was made " +
                "by an older Valence. Re-analyze the source (File ▸ Re-analyze) to find out.");
            return none;
        }

        var direct = model.Nodes.Values.Where(n => n.DirectEffects != EffectFlags.None).ToList();
        var reaching = model.Nodes.Values.Count(n => n.Effects != EffectFlags.None);
        var entries = model.Graph.EntryPoints
            .Select(e => (Entry: e, Effects: boundary.MemberReaches(e.Member)))
            .ToList();
        var report = new Report("What reaches outside",
            $"{direct.Count} of {model.Nodes.Count} classes ({Percent(direct.Count, model.Nodes.Count)}) call out of the program themselves; " +
            $"{reaching} reach outside directly or through calls" +
            (entries.Count > 0 ? $"; {entries.Count(e => e.Effects != EffectFlags.None)} of {entries.Count} entry points end up there." : "."))
        {
            Spotlight = new HashSet<NodeData>(direct),
        };

        var present = KindsOf(direct);
        report.Section("Overview").AddTable(
            new[] { "Effect", "Classes making the call", "Reaching it through calls", "Entry points reaching it" },
            present.Select(k => new List<ReportCell>
            {
                new(k.Label, Color: k.Color),
                new(boundary.DirectCounts.GetValueOrDefault(k.Flag).ToString()),
                new(model.Nodes.Values.Count(n => (n.Effects & k.Flag) != 0 && (n.DirectEffects & k.Flag) == 0).ToString()),
                new(entries.Count(e => (e.Effects & k.Flag) != 0).ToString()),
            }));

        if (entries.Count > 0)
        {
            var header = new List<string> { "Trigger", "Entry point" };
            header.AddRange(present.Select(k => k.Tag));
            report.Section($"What each entry point touches ({entries.Count})").AddParagraph(
                "Following calls, creates and events from each entry point: ● it (or code it runs) makes that kind of call.", muted: true)
                .AddTable(header, entries
                    .OrderByDescending(e => present.Count(k => (e.Effects & k.Flag) != 0))
                    .ThenBy(e => Array.IndexOf(TriggerOrder, e.Entry.Trigger))
                    .ThenBy(e => e.Entry.Label, StringComparer.OrdinalIgnoreCase)
                    .Take(MaxRows)
                    .Select(e =>
                    {
                        var row = new List<ReportCell>
                        {
                            new(e.Entry.Trigger),
                            new(e.Entry.Label, model.MemberOwners.GetValueOrDefault(e.Entry.Member)),
                        };
                        row.AddRange(present.Select(k => (e.Effects & k.Flag) != 0
                            ? new ReportCell("●", Color: k.Color)
                            : new ReportCell("·", Color: Palette.MutedText)));
                        return row;
                    }));
        }

        foreach (var kind in present)
        {
            var classes = direct.Where(n => (n.DirectEffects & kind.Flag) != 0).ToList();
            var section = report.Section($"{kind.Label} ({classes.Count} class{Plural(classes.Count, "es")})");
            if (kind.Flag == EffectFlags.Dynamic)
            {
                section.AddParagraph("Code chosen at run time (reflection, eval, call by name): the map can't see what these run, so links from here may be missing.", muted: true);
            }
            section.AddTable(new[] { "Class", "System", "Calls", "In" }, classes
                .OrderBy(n => n.Atom.Name, StringComparer.OrdinalIgnoreCase)
                .ThenBy(n => n.Info.Name, StringComparer.OrdinalIgnoreCase)
                .Take(MaxRows)
                .Select(n =>
                {
                    var calls = Boundary.DirectEffects(n).Where(d => Boundary.FlagOf(d.Effect.Kind) == kind.Flag).ToList();
                    var apis = calls.Select(d => d.Effect.Api).Distinct(StringComparer.Ordinal).ToList();
                    var members = calls.Select(d => d.Member != null ? $"{d.Member.Name}:{d.Effect.Line}" : $"top level:{d.Effect.Line}")
                        .Distinct(StringComparer.Ordinal).ToList();
                    return new List<ReportCell>
                    {
                        new(n.Info.Name, n),
                        new(n.Atom.Name),
                        new(Shorten(apis, 3)),
                        new(Shorten(members, 3), Color: Palette.MutedText),
                    };
                }));
        }

        report.Section("How sure is this?").AddParagraph(
            "Found from calls into known APIs (the .NET libraries, Godot, Python's standard library and common packages). " +
            "C# calls are resolved by the compiler; Python calls through imports, and through what a variable was assigned " +
            "(marked \"likely\" in the inspector). Calls through wrappers outside the repo, or APIs the catalog doesn't know, are missed.",
            muted: true);
        return report;
    }

    /// <summary>The kinds any of these classes have, most notable first.</summary>
    private static List<EffectKind> KindsOf(IEnumerable<NodeData> nodes)
    {
        var all = EffectFlags.None;
        foreach (var node in nodes) all |= node.DirectEffects;
        return Boundary.In(all).ToList();
    }

    private static string Shorten(List<string> items, int max) =>
        items.Count <= max ? string.Join(", ", items) : string.Join(", ", items.Take(max)) + $" +{items.Count - max}";

    // ---------------------------------------------------------------- helpers

    private static string Plural(int count, string suffix = "s") => count == 1 ? "" : suffix;

    private static string Percent(int part, int whole) => whole == 0 ? "0%" : $"{100.0 * part / whole:0}%";

    private static string Num(double value) => value.ToString("N0");
}
