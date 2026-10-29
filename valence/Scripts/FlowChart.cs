using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using Godot;
using Valence.Schema;

namespace Valence.Viewer;

/// <summary>
/// A simulation's <see cref="FlowPlan"/> as a flow chart: one box per method (or per class when
/// grouped), arrows for the calls, creates, reads/writes and events between them. Laid out left to
/// right by hop from where the flow starts, top to bottom in the order things happen. Also writes
/// the chart as Mermaid and SVG.
/// </summary>
public sealed class FlowChart
{
    public const float BoxWidth = 210f;
    public const float BoxHeight = 46f;
    public const float ColumnGap = 80f;
    public const float RowGap = 14f;
    public const float Margin = 24f;

    /// <summary>Above this many methods the chart shows one box per class instead.</summary>
    public const int GroupAbove = 220;

    public sealed class Box
    {
        public Box(string key, string label, string sublabel, NodeData? owner)
        {
            Key = key;
            Label = label;
            Sublabel = sublabel;
            Owner = owner;
        }

        public string Key { get; }
        public string Label { get; }
        public string Sublabel { get; }
        public NodeData? Owner { get; }
        public int Column { get; set; }
        public int Row { get; set; }
        public Rect2 Rect { get; set; }

        /// <summary>When the flow first reaches it (seconds into one run); 0 for start points.</summary>
        public float ReachedAt { get; set; } = float.MaxValue;

        public bool IsStart { get; set; }

        /// <summary>What its code does outside the program itself (the method's, or the class's when grouped).</summary>
        public EffectFlags Effects { get; set; }
    }

    public sealed class Arrow
    {
        public Arrow(Box from, Box to, string kind, bool likely)
        {
            From = from;
            To = to;
            Kind = kind;
            Likely = likely;
        }

        public Box From { get; }
        public Box To { get; }
        public string Kind { get; }
        public bool Likely { get; set; }

        /// <summary>Every pulse along it: (start, arrive), in seconds into one run.</summary>
        public List<(float Start, float Arrive)> Pulses { get; } = new();

        public float FirstStart => Pulses.Count == 0 ? 0f : Pulses[0].Start;
    }

    private FlowChart(string title, bool grouped, bool truncated)
    {
        Title = title;
        Grouped = grouped;
        Truncated = truncated;
    }

    public string Title { get; }
    public bool Grouped { get; }
    public bool Truncated { get; }
    public List<Box> Boxes { get; } = new();
    public List<Arrow> Arrows { get; } = new();
    public Vector2 Size { get; private set; }

    /// <param name="group">One box per class; null decides by size (<see cref="GroupAbove"/>).</param>
    public static FlowChart Build(FlowPlan plan, FlowGraph graph, GraphModel model, bool? group = null)
    {
        var points = plan.Events.SelectMany(e => new[] { e.From, e.To }).Distinct(StringComparer.Ordinal).Count();
        var grouped = !graph.MemberLevel || (group ?? points > GroupAbove);
        var chart = new FlowChart(plan.Title, grouped, plan.Truncated);

        var memberNames = new Dictionary<string, string>(StringComparer.Ordinal);
        var boxes = new Dictionary<string, Box>(StringComparer.Ordinal);
        var arrows = new Dictionary<(string, string, string), Arrow>();
        var depth = new Dictionary<Box, int>();

        Box BoxFor(string point)
        {
            var owner = graph.OwnerOf(point);
            var key = grouped ? owner?.Info.Id ?? point : point;
            if (boxes.TryGetValue(key, out var box)) return box;
            var system = owner?.Atom.Name ?? "";
            var label = grouped || owner == null ? owner?.Info.Name ?? point : $"{owner.Info.Name}.{MemberName(point, owner)}";
            box = new Box(key, label, system, owner)
            {
                Effects = grouped ? owner?.DirectEffects ?? EffectFlags.None : model.Boundary.MemberDirect(point),
            };
            boxes[key] = box;
            chart.Boxes.Add(box);
            return box;
        }

        string MemberName(string id, NodeData owner)
        {
            if (memberNames.TryGetValue(id, out var name)) return name;
            name = owner.Info.Members.FirstOrDefault(m => m.Id == id)?.Name ?? id;
            memberNames[id] = name;
            return name;
        }

        // Events come in time order. Hops grow along the wave: downstream the new box is the
        // callee, upstream the caller, so give the unknown side its neighbour's hop ± 1.
        foreach (var e in plan.Events)
        {
            var from = BoxFor(e.From);
            var to = BoxFor(e.To);
            var knowsFrom = depth.TryGetValue(from, out var fromDepth);
            var knowsTo = depth.TryGetValue(to, out var toDepth);
            if (!knowsFrom && !knowsTo)
            {
                depth[from] = 0;
                depth[to] = from == to ? 0 : 1;
                from.IsStart = true;
                from.ReachedAt = 0f;
            }
            else if (!knowsTo)
            {
                depth[to] = fromDepth + 1;
            }
            else if (!knowsFrom)
            {
                depth[from] = toDepth - 1;
            }
            from.ReachedAt = Math.Min(from.ReachedAt, e.Start);
            to.ReachedAt = Math.Min(to.ReachedAt, e.Arrive);

            if (from == to) continue;   // calls within one box (grouped classes, recursion)
            var key = (from.Key, to.Key, e.Kind);
            if (!arrows.TryGetValue(key, out var arrow))
            {
                arrow = new Arrow(from, to, e.Kind, e.Likely);
                arrows[key] = arrow;
                chart.Arrows.Add(arrow);
            }
            arrow.Likely &= e.Likely;
            arrow.Pulses.Add((e.Start, e.Arrive));
        }

        chart.Layout(depth);
        return chart;
    }

    /// <summary>Columns by hop (shifted so the leftmost is 0), rows by when each box is first reached.</summary>
    private void Layout(Dictionary<Box, int> depth)
    {
        var min = depth.Count == 0 ? 0 : depth.Values.Min();
        var columns = Boxes
            .GroupBy(b => depth.GetValueOrDefault(b) - min)
            .OrderBy(g => g.Key)
            .ToList();
        var tallest = 0;
        foreach (var column in columns)
        {
            var row = 0;
            foreach (var box in column.OrderBy(b => b.ReachedAt).ThenBy(b => b.Label, StringComparer.OrdinalIgnoreCase))
            {
                box.Column = column.Key;
                box.Row = row++;
                box.Rect = new Rect2(
                    Margin + box.Column * (BoxWidth + ColumnGap),
                    Margin + box.Row * (BoxHeight + RowGap),
                    BoxWidth, BoxHeight);
            }
            tallest = Math.Max(tallest, row);
        }
        var width = columns.Count == 0 ? 0 : columns.Max(c => c.Key) + 1;
        Size = new Vector2(
            Margin * 2 + width * BoxWidth + Math.Max(0, width - 1) * ColumnGap,
            Margin * 2 + tallest * BoxHeight + Math.Max(0, tallest - 1) * RowGap);
    }

    // ---------------------------------------------------------------- export

    /// <summary>Mermaid flowchart text (GitHub, GitLab, Notion and Obsidian draw it in markdown).</summary>
    public string ToMermaid()
    {
        var ids = new Dictionary<Box, string>();
        for (var i = 0; i < Boxes.Count; i++) ids[Boxes[i]] = "n" + i;

        var sb = new StringBuilder();
        sb.Append("---\ntitle: ").Append(MermaidText(Title)).Append("\n---\n");
        sb.Append("flowchart LR\n");
        foreach (var box in Boxes.OrderBy(b => b.Column).ThenBy(b => b.Row))
        {
            var label = box.Sublabel.Length > 0 ? $"{MermaidText(box.Label)}<br/><small>{MermaidText(box.Sublabel)}</small>" : MermaidText(box.Label);
            if (box.Effects != EffectFlags.None)
            {
                label += $"<br/><small>↗ {string.Join(", ", Boundary.In(box.Effects).Select(k => k.Tag))}</small>";
            }
            sb.Append("    ").Append(ids[box]).Append("[\"").Append(label).Append("\"]\n");
        }
        foreach (var arrow in Arrows.OrderBy(a => a.FirstStart))
        {
            var link = arrow.Kind switch
            {
                MemberEdgeKinds.Reads or MemberEdgeKinds.Writes => "-.->",
                MemberEdgeKinds.Creates => "==>",
                _ => arrow.Likely ? "-.->" : "-->",
            };
            var label = arrow.Pulses.Count > 1 ? $"{arrow.Kind} ×{arrow.Pulses.Count}" : arrow.Kind;
            sb.Append("    ").Append(ids[arrow.From]).Append(' ').Append(link).Append("|").Append(label).Append("| ")
                .Append(ids[arrow.To]).Append('\n');
        }
        var starts = Boxes.Where(b => b.IsStart).Select(b => ids[b]).ToList();
        if (starts.Count > 0)
        {
            sb.Append("    classDef start stroke:#ffc83d,stroke-width:2px\n");
            sb.Append("    class ").Append(string.Join(",", starts)).Append(" start\n");
        }
        return sb.ToString();
    }

    /// <summary>An SVG drawing of the chart, laid out exactly as on screen.</summary>
    public string ToSvg()
    {
        var w = Size.X;
        var h = Size.Y + 34f;
        var sb = new StringBuilder();
        sb.Append(Inv($"<svg xmlns=\"http://www.w3.org/2000/svg\" width=\"{w:0}\" height=\"{h:0}\" viewBox=\"0 0 {w:0} {h:0}\" font-family=\"Helvetica, Arial, sans-serif\">\n"));
        sb.Append("<defs>\n");
        foreach (var kind in Arrows.Select(a => a.Kind).Distinct())
        {
            sb.Append(Inv($"  <marker id=\"arrow-{kind}\" viewBox=\"0 0 10 10\" refX=\"9\" refY=\"5\" markerWidth=\"7\" markerHeight=\"7\" orient=\"auto-start-reverse\">"))
              .Append(Inv($"<path d=\"M0,0 L10,5 L0,10 z\" fill=\"{Hex(Palette.FlowKind(kind))}\"/></marker>\n"));
        }
        sb.Append("</defs>\n");
        sb.Append(Inv($"<rect width=\"{w:0}\" height=\"{h:0}\" fill=\"#08121c\"/>\n"));
        sb.Append(Inv($"<text x=\"{Margin:0}\" y=\"22\" fill=\"#5cd1f5\" font-size=\"15\" font-weight=\"bold\">{Xml(Title)}</text>\n"));
        sb.Append("<g transform=\"translate(0,34)\">\n");

        foreach (var arrow in Arrows)
        {
            var (a, c1, c2, b) = Curve(arrow.From.Rect, arrow.To.Rect);
            var dashed = arrow.Likely || arrow.Kind is MemberEdgeKinds.Reads or MemberEdgeKinds.Writes;
            sb.Append(Inv($"  <path d=\"M{a.X:0.#},{a.Y:0.#} C{c1.X:0.#},{c1.Y:0.#} {c2.X:0.#},{c2.Y:0.#} {b.X:0.#},{b.Y:0.#}\" fill=\"none\" stroke=\"{Hex(Palette.FlowKind(arrow.Kind))}\" stroke-width=\"2\" stroke-opacity=\"0.85\""))
              .Append(dashed ? " stroke-dasharray=\"6 4\"" : "")
              .Append(Inv($" marker-end=\"url(#arrow-{arrow.Kind})\"/>\n"));
        }
        foreach (var box in Boxes)
        {
            var r = box.Rect;
            var border = box.IsStart ? "#ffc83d" : "#3a5a85";
            var hue = box.Owner != null ? Hex(Palette.MoleculeHue(box.Owner.Atom.Molecule.Id)) : "#7cc4ff";
            sb.Append(Inv($"  <rect x=\"{r.Position.X:0.#}\" y=\"{r.Position.Y:0.#}\" width=\"{r.Size.X:0}\" height=\"{r.Size.Y:0}\" rx=\"7\" fill=\"#0a1825\" stroke=\"{border}\" stroke-width=\"1.5\"/>\n"));
            sb.Append(Inv($"  <rect x=\"{r.Position.X + 1:0.#}\" y=\"{r.Position.Y + 6:0.#}\" width=\"3\" height=\"{r.Size.Y - 12:0}\" rx=\"1.5\" fill=\"{hue}\"/>\n"));
            sb.Append(Inv($"  <text x=\"{r.Position.X + 12:0.#}\" y=\"{r.Position.Y + 20:0.#}\" fill=\"#e0e8f2\" font-size=\"13\">{Xml(box.Label)}</text>\n"));
            sb.Append(Inv($"  <text x=\"{r.Position.X + 12:0.#}\" y=\"{r.Position.Y + 37:0.#}\" fill=\"#8b95a7\" font-size=\"11\">{Xml(box.Sublabel)}</text>\n"));

            // What it does outside the program: a small tag per kind, from the right edge.
            var right = r.End.X - 6f;
            foreach (var kind in Boundary.In(box.Effects).Reverse())
            {
                var width = 8f + kind.Tag.Length * 6f;
                right -= width;
                var color = Hex(kind.Color);
                sb.Append(Inv($"  <rect x=\"{right:0.#}\" y=\"{r.Position.Y + r.Size.Y - 18f:0.#}\" width=\"{width:0.#}\" height=\"13\" rx=\"3\" fill=\"{color}\" fill-opacity=\"0.18\" stroke=\"{color}\" stroke-width=\"0.8\"/>\n"));
                sb.Append(Inv($"  <text x=\"{right + width / 2f:0.#}\" y=\"{r.Position.Y + r.Size.Y - 8.5f:0.#}\" fill=\"{color}\" font-size=\"9\" text-anchor=\"middle\">{kind.Tag}</text>\n"));
                right -= 3f;
            }
        }
        sb.Append("</g>\n</svg>\n");
        return sb.ToString();
    }

    /// <summary>
    /// The arrow's curve: from the source's right edge to the target's left edge; arrows that go
    /// back to an earlier (or the same) column loop out over the boxes instead.
    /// </summary>
    public static (Vector2 A, Vector2 C1, Vector2 C2, Vector2 B) Curve(Rect2 from, Rect2 to)
    {
        if (to.Position.X > from.Position.X)
        {
            var a = new Vector2(from.End.X, from.GetCenter().Y);
            var b = new Vector2(to.Position.X, to.GetCenter().Y);
            var bend = Math.Max(30f, (b.X - a.X) * 0.5f);
            return (a, a + new Vector2(bend, 0f), b - new Vector2(bend, 0f), b);
        }
        var start = new Vector2(from.GetCenter().X, from.Position.Y);
        var end = new Vector2(to.GetCenter().X, to.Position.Y);
        var lift = 40f + Math.Abs(start.X - end.X) * 0.15f;
        return (start, start - new Vector2(0f, lift), end - new Vector2(0f, lift), end);
    }

    private static string MermaidText(string text) => text.Replace("\"", "#quot;").Replace("<", "#lt;").Replace(">", "#gt;");

    private static string Xml(string text) =>
        text.Replace("&", "&amp;").Replace("<", "&lt;").Replace(">", "&gt;").Replace("\"", "&quot;");

    private static string Hex(Color color) => "#" + color.ToHtml(false);

    private static string Inv(FormattableString text) => text.ToString(CultureInfo.InvariantCulture);
}
