using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using Godot;

namespace Valence.Viewer;

/// <summary>A heading in a rendered Markdown document: where it is, for the contents list and #anchor links.</summary>
public sealed record MarkdownHeading(int Level, string Title, string Anchor, int Paragraph);

/// <summary>One searchable line of a rendered document: its paragraph, its plain text, and the section it's in.</summary>
public sealed record MarkdownBlock(int Paragraph, string Text, string Section);

/// <summary>Markdown rendered as RichTextLabel BBCode, with its headings and searchable text.</summary>
public sealed record MarkdownDocument(string Bbcode, List<MarkdownHeading> Headings, List<MarkdownBlock> Blocks)
{
    public MarkdownHeading? Find(string anchor) => Headings.FirstOrDefault(h => h.Anchor == anchor);
}

/// <summary>
/// Renders the Markdown subset the in-app help uses (docs written for GitHub read the same here):
/// headings (#–####), paragraphs, nested - / * / 1. lists, pipe tables, **bold**, *italic*, `code`,
/// [links](#anchor) and [links](https://…), fenced code, &gt; quotes and --- rules. Links become
/// "#anchor" or URL metas; anchors follow GitHub's rules so the same links work on both.
/// Each heading's paragraph index is recorded so the viewer can scroll to it, and each line's plain
/// text is kept for search; a search term passed in is highlighted wherever it appears in the text.
/// </summary>
public static class Markdown
{
    private static readonly Regex Heading = new(@"^(#{1,6})\s+(.*?)\s*#*\s*$", RegexOptions.Compiled);
    private static readonly Regex ListItem = new(@"^(\s*)([-*+]|\d+[.)])\s+(.*)$", RegexOptions.Compiled);
    private static readonly Regex Rule = new(@"^\s*([-*_])(\s*\1){2,}\s*$", RegexOptions.Compiled);
    private static readonly Regex TableSeparator = new(@"^\s*\|?\s*:?-{3,}:?\s*(\|\s*:?-{3,}:?\s*)*\|?\s*$", RegexOptions.Compiled);
    private static readonly Regex Link = new(@"\[lb\]([^\[\]]+?)\]\(([^)\s]+)\)", RegexOptions.Compiled);
    private static readonly Regex Bold = new(@"\*\*(?=\S)(.+?)(?<=\S)\*\*", RegexOptions.Compiled);
    private static readonly Regex Italic = new(@"(?<![\w*])\*(?=\S)(.+?)(?<=\S)\*(?![\w*])", RegexOptions.Compiled);
    private static readonly Regex AnchorDrop = new(@"[^\p{L}\p{N}\s_-]", RegexOptions.Compiled);

    private const string Rule80 = "────────────────────────────────────────────────────────────────────────────────";

    /// <param name="highlight">Text to highlight (case-insensitive), e.g. the help window's search.</param>
    public static MarkdownDocument Render(string markdown, string? highlight = null)
    {
        var lines = markdown.Replace("\r\n", "\n").Replace('\r', '\n').Split('\n');
        var output = new Output(string.IsNullOrWhiteSpace(highlight) ? null : highlight.Trim());
        var headings = new List<MarkdownHeading>();
        var anchors = new Dictionary<string, int>(StringComparer.Ordinal);
        var muted = Palette.Html(Palette.MutedText);
        var accent = Palette.Html(UiKit.TitleColor);

        var i = 0;
        while (i < lines.Length)
        {
            var line = lines[i];
            if (string.IsNullOrWhiteSpace(line))
            {
                i++;
                continue;
            }

            // Fenced code.
            if (line.TrimStart().StartsWith("```", StringComparison.Ordinal))
            {
                var code = new List<string>();
                for (i++; i < lines.Length && !lines[i].TrimStart().StartsWith("```", StringComparison.Ordinal); i++) code.Add(lines[i]);
                i++;
                foreach (var codeLine in code)
                {
                    output.Line($"[indent][bgcolor=#0d1b2a][code][color=#c9d6e3]{Escape(codeLine.Length == 0 ? " " : codeLine)}[/color][/code][/bgcolor][/indent]", codeLine);
                }
                output.Gap();
                continue;
            }

            // Headings.
            var heading = Heading.Match(line);
            if (heading.Success)
            {
                var level = Math.Min(4, heading.Groups[1].Value.Length);
                var title = heading.Groups[2].Value;
                if (output.Paragraphs > 0) output.Gap();
                var anchor = UniqueAnchor(AnchorOf(title), anchors);
                headings.Add(new MarkdownHeading(level, Plain(title), anchor, output.Paragraphs));
                output.Section = Plain(title);
                var size = level switch { 1 => 26, 2 => 21, 3 => 17, _ => 15 };
                var color = level <= 2 ? accent : "#e0e8f2";
                output.Line($"[font_size={size}][b][color={color}]{Inline(title)}[/color][/b][/font_size]", Plain(title));
                if (level <= 2) output.Line($"[color=#2a4560]{Rule80}[/color]");
                i++;
                continue;
            }

            // Horizontal rule.
            if (Rule.IsMatch(line))
            {
                output.Line($"[color=#2a4560]{Rule80}[/color]");
                i++;
                continue;
            }

            // Tables: a row of pipes followed by a --- separator.
            if (line.Contains('|') && i + 1 < lines.Length && TableSeparator.IsMatch(lines[i + 1]))
            {
                var header = Cells(line);
                var rows = new List<List<string>>();
                for (i += 2; i < lines.Length && lines[i].Contains('|') && !string.IsNullOrWhiteSpace(lines[i]); i++) rows.Add(Cells(lines[i]));
                var columns = header.Count;
                var sb = new StringBuilder($"[table={columns}]");
                foreach (var cell in header) sb.Append($"[cell bg=#16283a][b]{Inline(cell)}[/b]   [/cell]");
                for (var r = 0; r < rows.Count; r++)
                {
                    var bg = r % 2 == 0 ? "#0b1622" : "#0e1b29";
                    for (var c = 0; c < columns; c++)
                    {
                        sb.Append($"[cell bg={bg}]{Inline(c < rows[r].Count ? rows[r][c] : "")}   [/cell]");
                    }
                }
                sb.Append("[/table]");
                output.Line(sb.ToString(), string.Join(" · ", header.Concat(rows.SelectMany(row => row)).Select(Plain)));
                output.Gap();
                continue;
            }

            // Quotes.
            if (line.TrimStart().StartsWith('>'))
            {
                var quote = new List<string>();
                for (; i < lines.Length && lines[i].TrimStart().StartsWith('>'); i++) quote.Add(lines[i].TrimStart()[1..].Trim());
                output.Line($"[indent][color={muted}]▍ [i]{Inline(string.Join(" ", quote))}[/i][/color][/indent]", Plain(string.Join(" ", quote)));
                output.Gap();
                continue;
            }

            // Lists (nested by indentation; indented lines that aren't items continue the item above).
            if (ListItem.IsMatch(line))
            {
                var baseIndent = ListItem.Match(line).Groups[1].Value.Length;
                while (i < lines.Length && !string.IsNullOrWhiteSpace(lines[i]))
                {
                    var item = ListItem.Match(lines[i]);
                    if (!item.Success) break;
                    var depth = Math.Max(0, (item.Groups[1].Value.Replace("\t", "  ").Length - baseIndent) / 2);
                    var marker = item.Groups[2].Value;
                    var text = new StringBuilder(item.Groups[3].Value);
                    for (i++; i < lines.Length && !string.IsNullOrWhiteSpace(lines[i]) && !ListItem.IsMatch(lines[i])
                                && (lines[i].StartsWith(' ') || lines[i].StartsWith('\t')); i++)
                    {
                        text.Append(' ').Append(lines[i].Trim());
                    }
                    var bullet = char.IsDigit(marker[0]) ? marker.TrimEnd(')', '.') + "." : depth == 0 ? "•" : "◦";
                    var open = string.Concat(Enumerable.Repeat("[indent]", depth + 1));
                    var close = string.Concat(Enumerable.Repeat("[/indent]", depth + 1));
                    output.Line($"{open}[color={accent}]{bullet}[/color] {Inline(text.ToString())}{close}", Plain(text.ToString()));
                    // A blank line between items keeps the list going.
                    if (i + 1 < lines.Length && string.IsNullOrWhiteSpace(lines[i]) && ListItem.IsMatch(lines[i + 1])) i++;
                }
                output.Gap();
                continue;
            }

            // A paragraph: lines up to the next blank line or block.
            var paragraph = new StringBuilder(line.Trim());
            for (i++; i < lines.Length && !string.IsNullOrWhiteSpace(lines[i]) && !StartsBlock(lines, i); i++)
            {
                paragraph.Append(' ').Append(lines[i].Trim());
            }
            output.Line(Inline(paragraph.ToString()), Plain(paragraph.ToString()));
            output.Gap();
        }
        return new MarkdownDocument(output.ToString(), headings, output.Blocks);
    }

    /// <summary>GitHub's heading anchors: lowercase, punctuation dropped, spaces to hyphens.</summary>
    public static string AnchorOf(string title) =>
        AnchorDrop.Replace(Plain(title).ToLowerInvariant(), "").Trim().Replace(' ', '-');

    private static string UniqueAnchor(string anchor, Dictionary<string, int> seen)
    {
        if (!seen.TryGetValue(anchor, out var count))
        {
            seen[anchor] = 0;
            return anchor;
        }
        seen[anchor] = ++count;
        return $"{anchor}-{count}";
    }

    private static bool StartsBlock(string[] lines, int i)
    {
        var line = lines[i];
        var trimmed = line.TrimStart();
        return Heading.IsMatch(line) || ListItem.IsMatch(line) || Rule.IsMatch(line) || trimmed.StartsWith('>')
               || trimmed.StartsWith("```", StringComparison.Ordinal)
               || line.Contains('|') && i + 1 < lines.Length && TableSeparator.IsMatch(lines[i + 1]);
    }

    private static List<string> Cells(string row)
    {
        var text = row.Trim();
        if (text.StartsWith('|')) text = text[1..];
        if (text.EndsWith('|') && !text.EndsWith("\\|", StringComparison.Ordinal)) text = text[..^1];
        var cells = new List<string>();
        var current = new StringBuilder();
        for (var c = 0; c < text.Length; c++)
        {
            if (text[c] == '\\' && c + 1 < text.Length && text[c + 1] == '|')
            {
                current.Append('|');
                c++;
            }
            else if (text[c] == '|')
            {
                cells.Add(current.ToString().Trim());
                current.Clear();
            }
            else
            {
                current.Append(text[c]);
            }
        }
        cells.Add(current.ToString().Trim());
        return cells;
    }

    /// <summary>Inline formatting: `code` first (kept literal), then links, bold and italic.</summary>
    private static string Inline(string text)
    {
        var parts = text.Split('`');
        var sb = new StringBuilder();
        for (var p = 0; p < parts.Length; p++)
        {
            // An odd part is code, unless it's an unclosed trailing backtick.
            if (p % 2 == 1 && p < parts.Length - 1)
            {
                sb.Append($"[code][color=#ffd27f]{Escape(parts[p])}[/color][/code]");
                continue;
            }
            if (p % 2 == 1) sb.Append('`');
            var span = Escape(parts[p]);
            span = Link.Replace(span, m =>
            {
                var target = m.Groups[2].Value;
                var meta = target.StartsWith('#') ? target : target.StartsWith("http", StringComparison.OrdinalIgnoreCase) ? target : "";
                return meta.Length == 0
                    ? m.Groups[1].Value
                    : $"[url={meta}][color={Palette.Html(Palette.Accent)}]{m.Groups[1].Value}[/color][/url]";
            });
            span = Bold.Replace(span, "[b]$1[/b]");
            span = Italic.Replace(span, "[i]$1[/i]");
            sb.Append(span);
        }
        return sb.ToString();
    }

    /// <summary>Heading text without Markdown marks, for the contents list and anchors.</summary>
    private static string Plain(string text)
    {
        var plain = Regex.Replace(text, @"\[([^\]]+)\]\([^)]*\)", "$1");
        return plain.Replace("`", "").Replace("**", "").Replace("*", "").Trim();
    }

    private static string Escape(string text) => text.Replace("[", "[lb]");

    private const string HighlightOpen = "[bgcolor=#8a6d00][color=#ffffff]";
    private const string HighlightClose = "[/color][/bgcolor]";

    /// <summary>
    /// Wraps each case-insensitive occurrence of <paramref name="term"/> in the text between BBCode tags
    /// (never inside a tag, so links and colors stay intact; "[lb]" counts as a tag).
    /// </summary>
    private static string Highlight(string bbcode, string term)
    {
        var sb = new StringBuilder(bbcode.Length + 64);
        var i = 0;
        while (i < bbcode.Length)
        {
            if (bbcode[i] == '[')
            {
                var end = bbcode.IndexOf(']', i);
                if (end < 0) end = bbcode.Length - 1;
                sb.Append(bbcode, i, end - i + 1);
                i = end + 1;
                continue;
            }
            var next = bbcode.IndexOf('[', i);
            if (next < 0) next = bbcode.Length;
            var run = bbcode[i..next];
            var at = 0;
            for (var hit = run.IndexOf(term, StringComparison.OrdinalIgnoreCase); hit >= 0;
                 hit = run.IndexOf(term, at, StringComparison.OrdinalIgnoreCase))
            {
                sb.Append(run, at, hit - at).Append(HighlightOpen).Append(run, hit, term.Length).Append(HighlightClose);
                at = hit + term.Length;
            }
            sb.Append(run, at, run.Length - at);
            i = next;
        }
        return sb.ToString();
    }

    /// <summary>
    /// BBCode lines; RichTextLabel makes each line a paragraph, which is what headings and blocks record.
    /// Lines with text are kept as <see cref="MarkdownBlock"/>s for search, and the search term is highlighted.
    /// </summary>
    private sealed class Output
    {
        private readonly StringBuilder _text = new();
        private readonly string? _highlight;
        private bool _gap = true;

        public Output(string? highlight)
        {
            _highlight = highlight;
        }

        public int Paragraphs { get; private set; }

        /// <summary>The heading the next lines belong to.</summary>
        public string Section { get; set; } = "";

        public List<MarkdownBlock> Blocks { get; } = new();

        /// <param name="text">The line's plain text, for search (null for rules and gaps).</param>
        public void Line(string bbcode, string? text = null)
        {
            if (Paragraphs > 0) _text.Append('\n');
            _text.Append(_highlight != null && text != null ? Highlight(bbcode, _highlight) : bbcode);
            if (!string.IsNullOrWhiteSpace(text)) Blocks.Add(new MarkdownBlock(Paragraphs, text, Section));
            Paragraphs++;
            _gap = false;
        }

        /// <summary>One empty line between blocks (never two in a row).</summary>
        public void Gap()
        {
            if (_gap) return;
            Line("");
            _gap = true;
        }

        public override string ToString() => _text.ToString();
    }
}
