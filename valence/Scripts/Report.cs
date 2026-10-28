using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using Godot;

namespace Valence.Viewer;

/// <summary>A piece of report text; with a class attached it's a link that selects it.</summary>
public sealed record ReportCell(string Text, NodeData? Node = null, Color? Color = null, Color? Background = null);

public abstract record ReportBlock;

public sealed record ReportParagraph(string Text, bool Muted = false) : ReportBlock;

public sealed record ReportList(List<ReportCell> Items, bool Numbered = false) : ReportBlock;

public sealed record ReportTable(List<string> Headers, List<List<ReportCell>> Rows) : ReportBlock;

public sealed class ReportSection
{
    public ReportSection(string heading)
    {
        Heading = heading;
    }

    public string Heading { get; }
    public List<ReportBlock> Blocks { get; } = new();

    public ReportSection AddParagraph(string text, bool muted = false)
    {
        Blocks.Add(new ReportParagraph(text, muted));
        return this;
    }

    public ReportSection AddList(IEnumerable<ReportCell> items, bool numbered = false)
    {
        Blocks.Add(new ReportList(items.ToList(), numbered));
        return this;
    }

    public ReportSection AddTable(IEnumerable<string> headers, IEnumerable<List<ReportCell>> rows)
    {
        Blocks.Add(new ReportTable(headers.ToList(), rows.ToList()));
        return this;
    }
}

/// <summary>
/// What an Insights report says, independent of where it's shown: rendered as BBCode for the report
/// window (classes are links) and as Markdown for export. <see cref="Spotlight"/> is the set of
/// classes "Show Only These" filters the 3D view to.
/// </summary>
public sealed class Report
{
    public Report(string title, string summary)
    {
        Title = title;
        Summary = summary;
    }

    public string Title { get; }
    public string Summary { get; }
    public List<ReportSection> Sections { get; } = new();
    public HashSet<NodeData>? Spotlight { get; set; }

    public ReportSection Section(string heading)
    {
        var section = new ReportSection(heading);
        Sections.Add(section);
        return section;
    }

    // ---------------------------------------------------------------- BBCode

    public string ToBbcode()
    {
        var muted = Palette.Html(Palette.MutedText);
        var accent = Palette.Html(UiKit.TitleColor);
        var sb = new StringBuilder();
        sb.Append($"[color={muted}]{Bb(Summary)}[/color]\n");
        foreach (var section in Sections)
        {
            sb.Append($"\n[font_size=16][b][color={accent}]{Bb(section.Heading)}[/color][/b][/font_size]\n");
            foreach (var block in section.Blocks)
            {
                switch (block)
                {
                    case ReportParagraph p:
                        sb.Append(p.Muted ? $"[color={muted}]{Bb(p.Text)}[/color]\n" : $"{Bb(p.Text)}\n");
                        break;
                    case ReportList list:
                        for (var i = 0; i < list.Items.Count; i++)
                        {
                            sb.Append(list.Numbered ? $"{i + 1}. " : "•  ").Append(CellBb(list.Items[i])).Append('\n');
                        }
                        break;
                    case ReportTable table:
                        sb.Append($"[table={table.Headers.Count}]");
                        foreach (var header in table.Headers) sb.Append($"[cell][b][color={muted}]{Bb(header)}   [/color][/b][/cell]");
                        foreach (var row in table.Rows)
                        {
                            foreach (var cell in row)
                            {
                                sb.Append(cell.Background is { } bg ? $"[cell bg=#{bg.ToHtml(true)}]" : "[cell]")
                                  .Append(CellBb(cell)).Append("   [/cell]");
                            }
                        }
                        sb.Append("[/table]\n");
                        break;
                }
            }
        }
        return sb.ToString();
    }

    private static string CellBb(ReportCell cell)
    {
        var text = Bb(cell.Text);
        if (cell.Node != null) text = $"[url=node:{cell.Node.Info.Id}]{text}[/url]";
        if (cell.Color is { } color) text = $"[color=#{color.ToHtml(false)}]{text}[/color]";
        return text;
    }

    private static string Bb(string text) => text.Replace("[", "[lb]");

    // ---------------------------------------------------------------- Markdown

    public string ToMarkdown()
    {
        var sb = new StringBuilder();
        sb.Append("# ").Append(Title).Append("\n\n").Append(Summary).Append("\n");
        foreach (var section in Sections)
        {
            sb.Append("\n## ").Append(section.Heading).Append("\n\n");
            foreach (var block in section.Blocks)
            {
                switch (block)
                {
                    case ReportParagraph p:
                        sb.Append(p.Muted ? $"_{p.Text}_" : p.Text).Append("\n\n");
                        break;
                    case ReportList list:
                        for (var i = 0; i < list.Items.Count; i++)
                        {
                            sb.Append(list.Numbered ? $"{i + 1}. " : "- ").Append(CellMd(list.Items[i])).Append('\n');
                        }
                        sb.Append('\n');
                        break;
                    case ReportTable table:
                        sb.Append("| ").Append(string.Join(" | ", table.Headers.Select(MdCell))).Append(" |\n");
                        sb.Append('|').Append(string.Concat(table.Headers.Select(_ => " --- |"))).Append('\n');
                        foreach (var row in table.Rows)
                        {
                            sb.Append("| ").Append(string.Join(" | ", row.Select(CellMd))).Append(" |\n");
                        }
                        sb.Append('\n');
                        break;
                }
            }
        }
        sb.Append("---\n_Generated by Valence, ").Append(DateTime.Now.ToString("yyyy-MM-dd")).Append("._\n");
        return sb.ToString();
    }

    private static string CellMd(ReportCell cell) => cell.Node != null ? $"`{MdCell(cell.Text)}`" : MdCell(cell.Text);

    private static string MdCell(string text) => text.Replace("|", "\\|").Replace("\n", " ");
}
