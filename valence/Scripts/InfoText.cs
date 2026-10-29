using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using Valence.Schema;

namespace Valence.Viewer;

/// <summary>BBCode for the inspector. Links use "node:&lt;id&gt;" and "atom:&lt;id&gt;" metas.</summary>
public static class InfoText
{
    private const int MaxLinksPerGroup = 40;
    private const int MaxMembers = 80;
    private static readonly string Muted = Palette.Html(Palette.MutedText);
    private static readonly string EntryColor = Palette.Html(Palette.Accent);

    /// <summary>"Risk 0.86: changed 1,240 lines in 90 days (top 4%), used by 37 classes (top 2%), 940 lines (top 10%)".</summary>
    private static void AppendRisk(StringBuilder sb, NodeData node, GraphModel model)
    {
        var risk = node.Risk;
        var churn = node.Metric(MetricKeys.Churn);
        var loc = node.Metric(MetricKeys.Loc);
        var parts = new List<string>();
        if (model.HasGit)
        {
            parts.Add(churn > 0
                ? $"changed {Num(churn)} lines in {model.Graph.Repo.ChurnWindowDays} days{Top(risk.ChurnTop)}"
                : $"unchanged in {model.Graph.Repo.ChurnWindowDays} days");
        }
        parts.Add(node.FanIn > 0 ? $"used by {node.FanIn} classes{Top(risk.FanInTop)}" : "nothing depends on it");
        parts.Add($"{Num(loc)} lines{(loc > 0 ? Top(risk.SizeTop) : "")}");
        var color = Palette.Html(Palette.Heat(risk.Score));
        sb.Append($"[color={color}][b]Risk {risk.Score:0.00}[/b][/color]: {string.Join(", ", parts)}\n");
    }

    /// <summary>Reach, authorship, trust and outside imports (the Reach, Authors and Trust lenses).</summary>
    private static void AppendInsights(StringBuilder sb, NodeData node, GraphModel model)
    {
        if (model.ReachByTrigger.Count > 0)
        {
            sb.Append(model.Reached.Contains(node)
                ? $"Reached from: {string.Join(", ", Insights.TriggersReaching(model, node))}\n"
                : $"[color={Palette.Html(Palette.Heat(1f))}]Not reached from any entry point[/color]" +
                  (node.FanIn == 0 ? " and nothing uses it\n" : "\n"));
        }

        var authors = node.Metric(MetricKeys.Authors);
        if (authors > 0)
        {
            var top = node.Info.Attributes.GetValueOrDefault(AttributeKeys.TopAuthor, "?");
            var agent = node.Metric(MetricKeys.AgentShare);
            sb.Append($"{Num(authors)} author{(authors == 1 ? "" : "s")} in {model.Graph.Repo.ChurnWindowDays} days, " +
                      $"mostly {Esc(top)} ({node.Metric(MetricKeys.TopAuthorShare):P0})");
            if (agent > 0) sb.Append($" · [color={Palette.Html(Palette.Heat((float)agent))}]{agent:P0} of changed lines by AI agents or bots[/color]");
            sb.Append('\n');
        }

        if (node.LinkCount > 0 && node.InferredShare > 0)
        {
            var inferred = (int)Math.Round(node.InferredShare * node.LinkCount);
            sb.Append($"[color={Muted}]{inferred} of {node.LinkCount} links inferred ({node.InferredShare:P0}): trust the map a little less here[/color]\n");
        }
        if (node.Info.Attributes.TryGetValue(AttributeKeys.ExternalImports, out var external))
        {
            sb.Append($"[color={Muted}]Imports from outside the repo: {Esc(external)}[/color]\n");
        }
    }

    private static string Top(float share) => $" (top {Math.Max(1, (int)Math.Ceiling(share * 100))}%)";

    private const int MaxEffectsPerKind = 8;
    private const int MaxPathSteps = 4;

    /// <summary>
    /// The Boundary lens: what the class does outside the program. Calls it makes itself are listed with
    /// the member and line; effects it only reaches through calls show the path to the code that makes them.
    /// </summary>
    private static void AppendBoundary(StringBuilder sb, NodeData node, GraphModel model)
    {
        if (node.Effects == EffectFlags.None) return;
        var boundary = model.Boundary;
        sb.Append("\n[b]Reaches outside the program[/b]\n");

        var direct = Boundary.DirectEffects(node).ToList();
        foreach (var kind in Boundary.In(node.DirectEffects))
        {
            var calls = direct.Where(d => Boundary.FlagOf(d.Effect.Kind) == kind.Flag).ToList();
            sb.Append($"[color={Palette.Html(kind.Color)}]●[/color] {kind.Label}\n");
            foreach (var (member, effect) in calls.Take(MaxEffectsPerKind))
            {
                var where = member != null ? $"{Esc(member.Name)}: " : "top level: ";
                var count = effect.Weight > 1 ? $" ×{effect.Weight}" : "";
                var likely = effect.Confidence != null ? $", {Esc(effect.Confidence)}" : "";
                sb.Append($"    [color={Muted}]{where}[/color]{Esc(effect.Api)}{count} [color={Muted}]line {effect.Line}{likely}[/color]\n");
            }
            if (calls.Count > MaxEffectsPerKind) sb.Append($"    [color={Muted}]+{calls.Count - MaxEffectsPerKind} more[/color]\n");
        }

        foreach (var kind in Boundary.In(node.Effects & ~node.DirectEffects))
        {
            var start = node.Info.Members.FirstOrDefault(m => (boundary.MemberReaches(m.Id) & kind.Flag) != 0);
            if (start == null) continue;
            var (path, effect) = boundary.Trace(start.Id, kind.Flag);
            var steps = path.Skip(1).Select(id => StepLink(id, model)).ToList();
            if (steps.Count > MaxPathSteps) steps = steps.Take(2).Append("…").Concat(steps.TakeLast(1)).ToList();
            var dim = Palette.Html(kind.Color.Lerp(Palette.MutedText, 0.5f));
            sb.Append($"[color={dim}]○[/color] {kind.Label} [color={Muted}]through calls[/color]\n");
            sb.Append($"    [color={Muted}]{Esc(start.Name)} →[/color] {string.Join($" [color={Muted}]→[/color] ", steps)}" +
                      (effect != null ? $" [color={Muted}]→ {Esc(effect.Api)}[/color]" : "") + "\n");
        }
    }

    /// <summary>The Boundary lens on a timeline frame: what started and stopped reaching outside since the previous sample.</summary>
    public static string ForBoundaryFrame(Valence.Core.TimelineFrame frame, int index, int frames, int reaching,
        List<BoundaryChange> changes, bool first)
    {
        var sb = new StringBuilder();
        sb.Append("[font_size=20][b]Reaching outside[/b][/font_size]\n");
        sb.Append($"[color={Muted}]{BoundaryHistory.Day(frame)} · {Esc(BoundaryHistory.Short(frame.Commit))} · by {Esc(frame.Author)} · frame {index + 1} of {frames}[/color]\n");
        sb.Append($"{Esc(frame.Subject)}\n\n");
        sb.Append($"{reaching} classes make outside calls at this commit.\n");
        if (first)
        {
            sb.Append($"\n[color={Muted}]The first sample: step forward to see what changes.[/color]\n");
            return sb.ToString();
        }
        if (changes.Count == 0)
        {
            sb.Append($"\n[color={Muted}]No outside calls added or removed since the previous sample.[/color]\n");
            return sb.ToString();
        }

        void AppendGroup(string title, Godot.Color color, IEnumerable<BoundaryChange> group)
        {
            var byClass = group.GroupBy(c => c.NodeId).ToList();
            if (byClass.Count == 0) return;
            sb.Append($"\n[b][color={Palette.Html(color)}]{title}[/color][/b] ({byClass.Count} class{(byClass.Count == 1 ? "" : "es")})\n");
            foreach (var cls in byClass.OrderBy(g => g.First().Name, StringComparer.OrdinalIgnoreCase).Take(60))
            {
                var head = cls.First();
                sb.Append(head.Node != null ? NodeLink(head.Node) : Esc(head.Name)).Append('\n');
                foreach (var change in cls.Take(MaxEffectsPerKind))
                {
                    sb.Append($"    [color={Palette.Html(change.Kind.Color)}]●[/color] {Esc(change.Kind.Label)}: {Esc(change.Api)}\n");
                }
            }
        }

        AppendGroup("Started reaching outside", Palette.Added, changes.Where(c => c.Added));
        AppendGroup("Stopped", Palette.Proton, changes.Where(c => !c.Added));
        sb.Append($"\n[color={Muted}]Somewhere between the previous sample and this commit. Insights ▸ Outside Effects Over Time lists every sample.[/color]\n");
        return sb.ToString();
    }

    /// <summary>"Storage.Save", linking to the class.</summary>
    private static string StepLink(string memberId, GraphModel model)
    {
        var name = model.Boundary.Member(memberId)?.Name ?? memberId;
        return model.MemberOwners.TryGetValue(memberId, out var owner)
            ? $"[url=node:{owner.Info.Id}]{Esc(owner.Info.Name)}.{Esc(name)}[/url]"
            : Esc(name);
    }

    public static string ForComparison(Comparison comparison, GraphModel model)
    {
        var sb = new StringBuilder();
        sb.Append("[font_size=20][b]Compare[/b][/font_size]\n");
        sb.Append($"[color={Muted}]now vs snapshot {Esc(comparison.Label)}[/color]\n\n");
        sb.Append($"[color={Palette.Html(Palette.Added)}]● {comparison.Added.Count} added[/color]   ");
        sb.Append($"[color={Palette.Html(Palette.Changed)}]● {comparison.Changed.Count} changed[/color]   ");
        sb.Append($"[color={Muted}]{comparison.Removed.Count} removed[/color]\n");

        void AppendGroup(string title, IEnumerable<string> ids, Godot.Color color)
        {
            var nodes = ids.Select(id => model.Nodes.GetValueOrDefault(id)).Where(n => n != null).Cast<NodeData>()
                .OrderBy(n => n.Info.Name, StringComparer.OrdinalIgnoreCase).ToList();
            if (nodes.Count == 0) return;
            sb.Append($"\n[b][color={Palette.Html(color)}]{title}[/color][/b] ({nodes.Count})\n");
            foreach (var node in nodes.Take(150))
            {
                sb.Append($"{NodeLink(node)} [color={Muted}]{Esc(node.Atom.ShortName)}[/color]\n");
            }
            if (nodes.Count > 150) sb.Append($"[color={Muted}]+{nodes.Count - 150} more[/color]\n");
        }

        AppendGroup("Added", comparison.Added, Palette.Added);
        AppendGroup("Changed", comparison.Changed, Palette.Changed);
        if (comparison.Removed.Count > 0)
        {
            sb.Append($"\n[b]Removed[/b] ({comparison.Removed.Count})\n");
            foreach (var node in comparison.Removed.Take(150)) sb.Append($"{Esc(node.Name)} [color={Muted}]{Esc(node.FullName)}[/color]\n");
            if (comparison.Removed.Count > 150) sb.Append($"[color={Muted}]+{comparison.Removed.Count - 150} more[/color]\n");
        }
        sb.Append($"\n[color={Muted}]View ▸ Stop Comparing to return to normal colors.[/color]\n");
        return sb.ToString();
    }

    public static string ForSimulation(FlowPlan plan, int tick, bool loop, IReadOnlyDictionary<NodeData, int> visits, GraphModel model)
    {
        var sb = new StringBuilder();
        sb.Append("[font_size=20][b]Simulation[/b][/font_size]\n");
        sb.Append($"{Esc(plan.Title)}\n");
        sb.Append($"[color={Muted}]{(loop ? $"tick {tick}, repeating" : tick > 1 ? $"run {tick}" : "single run")}[/color]\n\n");
        sb.Append($"{plan.Events.Count} pulses per run · {plan.Reached} points reached · {visits.Count} classes lit so far\n");
        if (plan.Truncated) sb.Append($"[color={Palette.Html(Palette.Covalent)}]Very large flow: capped at {plan.Events.Count} pulses.[/color]\n");

        sb.Append("\n[b]Pulses[/b]  ");
        sb.Append(string.Join("  ", Palette.FlowLegend.Select(kv => $"[color={Palette.Html(kv.Value)}]●[/color] {kv.Key}")));
        sb.Append('\n');

        var busiest = visits.OrderByDescending(kv => kv.Value).ThenBy(kv => kv.Key.Info.Name).Take(25).ToList();
        if (busiest.Count > 0)
        {
            sb.Append("\n[b]Busiest classes[/b]\n");
            foreach (var (node, count) in busiest)
            {
                sb.Append($"{NodeLink(node)} [color={Muted}]×{count} · {Esc(node.Atom.ShortName)}[/color]\n");
            }
        }

        sb.Append($"\n[color={Muted}]Static simulation: what the code can do, not a recording of a run. " +
                  "Smaller, dimmer pulses are \"likely\" links (virtual/interface dispatch, events). " +
                  "Space pause/resume · Esc or Simulate ▸ Stop to clear.[/color]\n");
        return sb.ToString();
    }

    public static string ForNode(NodeData node, GraphModel model)
    {
        var info = node.Info;
        var sb = new StringBuilder();
        sb.Append($"[font_size=20][b]{Esc(info.Name)}[/b][/font_size]\n");
        sb.Append($"[color={Muted}]{Esc(info.Kind)} · {Esc(info.Visibility)} · {(node.IsPublic ? "proton" : "neutron")}[/color]\n");
        sb.Append($"{Esc(info.FullName)}\n");
        sb.Append($"[color={Muted}]{Esc(info.File)}:{info.Line}[/color]\n");
        if (info.Files.Count > 1) sb.Append($"[color={Muted}]partial across {info.Files.Count} files[/color]\n");
        sb.Append($"System: {AtomLink(node.Atom)}\n");
        if (info.Attributes.TryGetValue("externalBase", out var externalBase))
        {
            sb.Append($"Extends (external): {Esc(externalBase)}\n");
        }
        if (info.Attributes.TryGetValue("externalInterfaces", out var externalInterfaces))
        {
            sb.Append($"Implements (external): {Esc(externalInterfaces)}\n");
        }

        sb.Append('\n');
        sb.Append($"{Num(node.Metric(MetricKeys.Loc))} lines · {Num(node.Metric(MetricKeys.Methods))} methods · " +
                  $"{Num(node.Metric(MetricKeys.Fields))} fields · {Num(node.Metric(MetricKeys.Properties))} properties\n");
        sb.Append($"Used by {node.FanIn} classes · depends on {node.FanOut}\n");
        if (model.HasGit)
        {
            sb.Append($"{Num(node.Metric(MetricKeys.Commits))} commits, {Num(node.Metric(MetricKeys.Churn))} lines churned " +
                      $"in the last {model.Graph.Repo.ChurnWindowDays} days");
            var last = node.Metric(MetricKeys.LastChanged);
            if (last > 0) sb.Append($" · last changed {Date(last)}");
            sb.Append('\n');
        }
        AppendRisk(sb, node, model);
        AppendInsights(sb, node, model);
        AppendBoundary(sb, node, model);

        AppendMembers(sb, node, model);
        AppendEdges(sb, "Depends on", node.Outgoing, e => e.Target, model);
        AppendEdges(sb, "Used by", node.Incoming, e => e.Source, model);
        return sb.ToString();
    }

    public static string ForAtom(AtomData atom, GraphModel model)
    {
        var sb = new StringBuilder();
        var protons = atom.Nodes.Count(n => n.IsPublic);
        sb.Append($"[font_size=20][b]{Esc(atom.Name)}[/b][/font_size]\n");
        sb.Append($"[color={Muted}]in {Esc(atom.Molecule.Name)}[/color]\n");
        sb.Append($"{atom.Nodes.Count} classes · {protons} protons (public) · {atom.Nodes.Count - protons} neutrons\n");
        sb.Append($"Valence {atom.Valence} (bonded systems) · layer {atom.Layer} of {model.MaxLayer}\n");
        if (atom.InCycle)
        {
            sb.Append($"[color={Palette.Html(Palette.Covalent)}]⚠ part of a dependency cycle[/color]\n");
        }

        var bonds = model.Bonds.Where(b => b.A == atom || b.B == atom).ToList();
        AppendBonds(sb, "Depends on systems", bonds.Where(b => b.WeightFrom(atom) > 0)
            .OrderBy(b => b.Other(atom).Name, StringComparer.OrdinalIgnoreCase).Select(b => (b, b.WeightFrom(atom))), atom);
        AppendBonds(sb, "Used by systems", bonds.Where(b => b.WeightFrom(b.Other(atom)) > 0)
            .OrderBy(b => b.Other(atom).Name, StringComparer.OrdinalIgnoreCase).Select(b => (b, b.WeightFrom(b.Other(atom)))), atom);

        var entryCount = atom.Nodes.SelectMany(n => n.Info.Members).Count(m => model.EntryPointsByMember.ContainsKey(m.Id));
        if (entryCount > 0) sb.Append($"\n[color={EntryColor}]▶ {entryCount} entry points in this system[/color]\n");

        var outside = Boundary.Kinds
            .Select(k => (Kind: k, Count: atom.Nodes.Count(n => (n.DirectEffects & k.Flag) != 0)))
            .Where(x => x.Count > 0)
            .Select(x => $"[color={Palette.Html(x.Kind.Color)}]●[/color] {x.Kind.Label.ToLowerInvariant()} {x.Count}")
            .ToList();
        if (outside.Count > 0) sb.Append($"\nReaches outside (classes making the call): {string.Join("  ", outside)}\n");

        sb.Append("\n[b]Classes[/b]\n");
        foreach (var node in atom.Nodes.OrderBy(n => n.Info.Name, StringComparer.OrdinalIgnoreCase).Take(80))
        {
            sb.Append($"{NodeLink(node)} [color={Muted}]{node.FanIn}↓ {node.FanOut}↑[/color]\n");
        }
        if (atom.Nodes.Count > 80) sb.Append($"[color={Muted}]+{atom.Nodes.Count - 80} more[/color]\n");
        return sb.ToString();
    }

    private static void AppendMembers(StringBuilder sb, NodeData node, GraphModel model)
    {
        var members = node.Info.Members;
        if (members.Count == 0) return;
        sb.Append($"\n[b]Members[/b] ({members.Count})\n");
        foreach (var member in members
                     .OrderBy(m => m.Name, StringComparer.OrdinalIgnoreCase)
                     .ThenBy(m => KindOrder(m.Kind))
                     .Take(MaxMembers))
        {
            sb.Append($"[color={Muted}]{KindGlyph(member.Kind)}[/color] {Esc(member.Signature ?? member.Name)}");
            if (member.IsStatic) sb.Append($" [color={Muted}]static[/color]");
            if (model.EntryPointsByMember.TryGetValue(member.Id, out var entries))
            {
                foreach (var entry in entries) sb.Append($" [color={EntryColor}]▶ {Esc(entry.Trigger)}[/color]");
            }
            foreach (var kind in Boundary.In(model.Boundary.MemberDirect(member.Id)))
            {
                sb.Append($" [color={Palette.Html(kind.Color)}]● {kind.Tag}[/color]");
            }
            sb.Append('\n');
        }
        if (members.Count > MaxMembers) sb.Append($"[color={Muted}]+{members.Count - MaxMembers} more[/color]\n");
    }

    private static void AppendBonds(StringBuilder sb, string title, IEnumerable<(BondData Bond, int Weight)> bonds, AtomData atom)
    {
        var list = bonds.ToList();
        if (list.Count == 0) return;
        sb.Append($"\n[b]{title}[/b] ({list.Count})\n");
        foreach (var (bond, weight) in list)
        {
            var mutual = bond.Covalent ? $" [color={Palette.Html(Palette.Covalent)}]⇄[/color]" : "";
            sb.Append($"{AtomLink(bond.Other(atom))} [color={Muted}]×{weight}[/color]{mutual}\n");
        }
    }

    private static void AppendEdges(StringBuilder sb, string title, List<EdgeInfo> edges, Func<EdgeInfo, string> other, GraphModel model)
    {
        var visible = edges.Where(e => e.Kind != EdgeKinds.Contains || title == "Depends on").ToList();
        if (visible.Count == 0) return;
        sb.Append($"\n[b]{title}[/b] ({visible.Select(other).Distinct().Count()})\n");
        foreach (var kind in Palette.EdgeKindOrder)
        {
            var group = visible.Where(e => e.Kind == kind)
                .Select(e => (Node: model.Nodes.GetValueOrDefault(other(e)), e.Weight, e.Confidence))
                .Where(x => x.Node != null)
                .OrderBy(x => x.Node!.Info.Name, StringComparer.OrdinalIgnoreCase)
                .ToList();
            if (group.Count == 0) continue;
            var links = group.Take(MaxLinksPerGroup)
                .Select(x => NodeLink(x.Node!)
                             + (x.Weight > 1 ? $" [color={Muted}]×{x.Weight}[/color]" : "")
                             + (x.Confidence != null ? $" [color={Muted}]({Esc(x.Confidence)})[/color]" : ""));
            sb.Append($"[color={Palette.Html(Palette.EdgeKind(kind))}]{kind}[/color]: {string.Join(", ", links)}");
            if (group.Count > MaxLinksPerGroup) sb.Append($" [color={Muted}]+{group.Count - MaxLinksPerGroup} more[/color]");
            sb.Append('\n');
        }
    }

    private static int KindOrder(string kind) => kind switch
    {
        MemberKinds.Constructor => 0,
        MemberKinds.Method or MemberKinds.Function => 1,
        MemberKinds.Event => 2,
        MemberKinds.Property => 3,
        _ => 4,
    };

    private static string KindGlyph(string kind) => kind switch
    {
        MemberKinds.Constructor => "new",
        MemberKinds.Method or MemberKinds.Function => "ƒ",
        MemberKinds.Event => "⚡",
        MemberKinds.Property => "◆",
        MemberKinds.Field => "▪",
        _ => "·",
    };

    private static string NodeLink(NodeData node) => $"[url=node:{node.Info.Id}]{Esc(node.Info.Name)}[/url]";

    private static string AtomLink(AtomData atom) => $"[url=atom:{atom.Id}]{Esc(atom.Name)}[/url]";

    private static string Num(double value) => value.ToString("N0", CultureInfo.InvariantCulture);

    private static string Date(double unixSeconds) =>
        DateTimeOffset.FromUnixTimeSeconds((long)unixSeconds).ToLocalTime().ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);

    /// <summary>Escapes BBCode: "[" becomes "[lb]".</summary>
    private static string Esc(string text) => text.Replace("[", "[lb]");
}
