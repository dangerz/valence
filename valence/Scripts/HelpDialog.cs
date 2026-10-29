using System;
using Godot;

namespace Valence.Viewer;

/// <summary>
/// Help ▸ User Guide (F1): the bundled user guide (res://help/HELP.md), rendered from Markdown, with a
/// contents list beside it. Links to sections scroll there; web links open in the browser.
/// </summary>
public partial class HelpDialog : ModalDialog
{
    public const string GuidePath = "res://help/HELP.md";

    private RichTextLabel? _text;
    private ItemList? _contents;
    private MarkdownDocument? _document;

    public void BuildGuide()
    {
        Build("User Guide", 1240f);

        var row = new HBoxContainer();
        row.AddThemeConstantOverride("separation", 12);
        Body.AddChild(row);

        _contents = new ItemList
        {
            CustomMinimumSize = new Vector2(280f, 620f),
            AllowReselect = true,
            FocusMode = FocusModeEnum.None,
        };
        _contents.AddThemeFontSizeOverride("font_size", 13);
        _contents.ItemSelected += index =>
        {
            if (_document == null || _contents.GetItemMetadata((int)index).AsString() is not { Length: > 0 } anchor) return;
            ScrollTo(anchor);
        };
        row.AddChild(_contents);

        _text = new RichTextLabel
        {
            BbcodeEnabled = true,
            ScrollActive = true,
            SelectionEnabled = true,
            CustomMinimumSize = new Vector2(ContentWidth - 280f - 12f, 620f),
        };
        _text.AddThemeFontSizeOverride("normal_font_size", 14);
        _text.AddThemeFontSizeOverride("bold_font_size", 14);
        _text.AddThemeFontSizeOverride("italics_font_size", 14);
        _text.AddThemeFontSizeOverride("mono_font_size", 13);
        _text.AddThemeConstantOverride("table_h_separation", 4);
        _text.AddThemeConstantOverride("table_v_separation", 2);
        _text.MetaClicked += meta =>
        {
            var target = meta.AsString();
            if (target.StartsWith('#')) ScrollTo(target[1..]);
            else if (target.StartsWith("http", StringComparison.OrdinalIgnoreCase)) OS.ShellOpen(target);
        };
        row.AddChild(_text);
    }

    /// <summary>Loads the guide the first time, then shows it (at the top, or at a section).</summary>
    public void ShowGuide(string? anchor = null)
    {
        if (_document == null) Load();
        Open();
        if (anchor != null) Callable.From(() => ScrollTo(anchor)).CallDeferred();
    }

    private void Load()
    {
        if (_text == null || _contents == null) return;
        string markdown;
        if (FileAccess.FileExists(GuidePath))
        {
            markdown = FileAccess.GetFileAsString(GuidePath);
        }
        else
        {
            markdown = "# User Guide\n\nThe guide (`help/HELP.md`) isn't in this build. It's in the Valence repository on GitHub.";
            GD.PushWarning($"[valence] missing {GuidePath}");
        }

        _document = Markdown.Render(markdown);
        _text.Text = _document.Bbcode;
        _contents.Clear();
        foreach (var heading in _document.Headings)
        {
            if (heading.Level > 2) continue;
            var index = _contents.AddItem(heading.Level == 1 ? heading.Title : "    " + heading.Title);
            _contents.SetItemMetadata(index, heading.Anchor);
            if (heading.Level == 1) _contents.SetItemCustomFgColor(index, UiKit.TitleColor);
        }
    }

    private void ScrollTo(string anchor)
    {
        if (_text == null || _document?.Find(anchor) is not { } heading) return;
        _text.ScrollToParagraph(heading.Paragraph);
    }
}
