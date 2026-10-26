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

    public static string ForSummary(GraphModel model)
    {
        var repo = model.Graph.Repo;
        var sb = new StringBuilder();
        sb.Append($"[font_size=20][b]{Esc(repo.Name)}[/b][/font_size]\n");
        sb.Append($"[color={Muted}]{Esc(repo.Root)}[/color]\n");
        if (!string.IsNullOrEmpty(repo.Commit))
        {
            sb.Append($"[color={Muted}]{Esc(repo.Branch ?? "")} @ {Esc(repo.Commit[..Math.Min(10, repo.Commit.Length)])}[/color]\n");
        }
        sb.Append('\n');
        sb.Append($"{model.Molecules.Count} projects · {model.AtomList.Count} systems · {model.Nodes.Count} classes\n");
        sb.Append($"{model.Bonds.Count} bonds, {model.Bonds.Count(b => b.Covalent)} of them cycles\n");
        var members = model.Nodes.Values.Sum(n => n.Info.Members.Count);
        if (members > 0) sb.Append($"{members} members · {model.Graph.MemberEdges.Count} member-level links\n");

        var entries = model.Graph.EntryPoints;
        if (entries.Count > 0)
        {
            sb.Append($"\n[b]Entry points[/b] ({entries.Count})\n");
            foreach (var group in entries.GroupBy(e => e.Trigger).OrderBy(g => TriggerOrder(g.Key)))
            {
                sb.Append($"[color={EntryColor}]▶ {Esc(group.Key)}[/color] ");
                var links = group.Take(MaxLinksPerGroup).Select(e =>
                    model.MemberOwners.TryGetValue(e.Member, out var owner)
                        ? $"[url=node:{owner.Info.Id}]{Esc(e.Label)}[/url]"
                        : Esc(e.Label));
                sb.Append(string.Join(", ", links));
                if (group.Count() > MaxLinksPerGroup) sb.Append($" [color={Muted}]+{group.Count() - MaxLinksPerGroup} more[/color]");
                sb.Append('\n');
            }
        }

        sb.Append($"\n[color={Muted}]Click a system to open it, or pick anything from the Outline.[/color]\n");
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
            .OrderByDescending(b => b.WeightFrom(atom)).Select(b => (b, b.WeightFrom(atom))), atom);
        AppendBonds(sb, "Used by systems", bonds.Where(b => b.WeightFrom(b.Other(atom)) > 0)
            .OrderByDescending(b => b.WeightFrom(b.Other(atom))).Select(b => (b, b.WeightFrom(b.Other(atom)))), atom);

        var entryCount = atom.Nodes.SelectMany(n => n.Info.Members).Count(m => model.EntryPointsByMember.ContainsKey(m.Id));
        if (entryCount > 0) sb.Append($"\n[color={EntryColor}]▶ {entryCount} entry points in this system[/color]\n");

        sb.Append("\n[b]Classes[/b] (most depended-on first)\n");
        foreach (var node in atom.Nodes.OrderByDescending(n => n.FanIn).ThenBy(n => n.Info.Name).Take(80))
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
                     .OrderBy(m => model.EntryPointsByMember.ContainsKey(m.Id) ? 0 : 1)
                     .ThenBy(m => KindOrder(m.Kind))
                     .ThenBy(m => m.Line)
                     .Take(MaxMembers))
        {
            sb.Append($"[color={Muted}]{KindGlyph(member.Kind)}[/color] {Esc(member.Signature ?? member.Name)}");
            if (member.IsStatic) sb.Append($" [color={Muted}]static[/color]");
            if (model.EntryPointsByMember.TryGetValue(member.Id, out var entries))
            {
                foreach (var entry in entries) sb.Append($" [color={EntryColor}]▶ {Esc(entry.Trigger)}[/color]");
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
                .OrderByDescending(x => x.Weight)
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

    private static int TriggerOrder(string trigger) => trigger switch
    {
        Triggers.Startup => 0,
        Triggers.Frame => 1,
        Triggers.Physics => 2,
        Triggers.Input => 3,
        Triggers.Draw => 4,
        Triggers.Event => 5,
        Triggers.Request => 6,
        Triggers.Job => 7,
        _ => 8,
    };

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
