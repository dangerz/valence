using System;
using System.Collections.Generic;
using System.Linq;
using Godot;

namespace Valence.Viewer;

/// <summary>
/// Help ▸ User Guide (F1): the bundled user guide (res://help/HELP.md), rendered from Markdown, with a
/// contents list beside it. Links to sections scroll there; web links open in the browser.
/// The search box finds text anywhere in the guide: the list becomes the matches (section and a
/// snippet), every match is highlighted, Enter goes to the next one and Esc clears the search.
/// </summary>
public partial class HelpDialog : ModalDialog
{
    public const string GuidePath = "res://help/HELP.md";

    private const int MinSearchLength = 2;
    private const int MaxResults = 200;
    private const int SnippetChars = 60;

    private RichTextLabel? _text;
    private LineEdit? _search;
    private Label? _searchStatus;
    private ItemList? _contents;
    private ItemList? _results;
    private string? _markdown;
    private MarkdownDocument? _document;
    private readonly List<MarkdownBlock> _matches = new();
    private int _current = -1;

    public void BuildGuide()
    {
        Build("User Guide", 1240f);

        var row = new HBoxContainer();
        row.AddThemeConstantOverride("separation", 12);
        Body.AddChild(row);

        var side = new VBoxContainer { CustomMinimumSize = new Vector2(300f, 0f) };
        side.AddThemeConstantOverride("separation", 6);
        row.AddChild(side);

        var searchBox = UiKit.SearchField("Search the guide…", out var search);
        _search = search;
        _search.TextChanged += _ => Search();
        _search.TextSubmitted += _ => Step(Input.IsKeyPressed(Key.Shift) ? -1 : 1);
        side.AddChild(searchBox);

        _searchStatus = new Label { Visible = false };
        _searchStatus.AddThemeFontSizeOverride("font_size", 12);
        _searchStatus.AddThemeColorOverride("font_color", Palette.MutedText);
        side.AddChild(_searchStatus);

        _contents = NewList();
        _contents.ItemSelected += index =>
        {
            if (_contents.GetItemMetadata((int)index).AsString() is { Length: > 0 } anchor) ScrollTo(anchor);
        };
        side.AddChild(_contents);

        _results = NewList();
        _results.Visible = false;
        _results.ItemSelected += index => GoToMatch((int)index);
        side.AddChild(_results);

        _text = new RichTextLabel
        {
            BbcodeEnabled = true,
            ScrollActive = true,
            SelectionEnabled = true,
            CustomMinimumSize = new Vector2(ContentWidth - 300f - 12f, 640f),
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

    /// <summary>Loads the guide the first time, then shows it (at the top, or at a section) with the search box focused.</summary>
    public void ShowGuide(string? anchor = null)
    {
        if (_document == null) Load();
        Open();
        Callable.From(() =>
        {
            _search?.GrabFocus();
            _search?.SelectAll();
            if (anchor != null) ScrollTo(anchor);
        }).CallDeferred();
    }

    /// <summary>Esc in the search box clears it (or closes the guide when it's empty); ⌘F / Ctrl+F focuses it.</summary>
    public override void _Input(InputEvent @event)
    {
        if (Visible && _search != null && @event is InputEventKey { Pressed: true } key)
        {
            if (key.Keycode == Key.Escape && _search.HasFocus())
            {
                if (_search.Text.Length > 0)
                {
                    _search.Text = "";
                    Search();
                }
                else
                {
                    Close();
                }
                GetViewport().SetInputAsHandled();
                return;
            }
            if (key.Keycode == Key.F && key.IsCommandOrControlPressed())
            {
                _search.GrabFocus();
                _search.SelectAll();
                GetViewport().SetInputAsHandled();
                return;
            }
        }
        base._Input(@event);
    }

    private static ItemList NewList()
    {
        var list = new ItemList
        {
            CustomMinimumSize = new Vector2(300f, 590f),
            SizeFlagsVertical = SizeFlags.ExpandFill,
            AllowReselect = true,
            FocusMode = FocusModeEnum.None,
        };
        list.AddThemeFontSizeOverride("font_size", 13);
        return list;
    }

    private void Load()
    {
        if (FileAccess.FileExists(GuidePath))
        {
            _markdown = FileAccess.GetFileAsString(GuidePath);
        }
        else
        {
            _markdown = "# User Guide\n\nThe guide (`help/HELP.md`) isn't in this build. It's in the Valence repository on GitHub.";
            GD.PushWarning($"[valence] missing {GuidePath}");
        }
        Render(null);

        if (_contents == null || _document == null) return;
        _contents.Clear();
        foreach (var heading in _document.Headings)
        {
            if (heading.Level > 2) continue;
            var index = _contents.AddItem(heading.Level == 1 ? heading.Title : "    " + heading.Title);
            _contents.SetItemMetadata(index, heading.Anchor);
            if (heading.Level == 1) _contents.SetItemCustomFgColor(index, UiKit.TitleColor);
        }
    }

    private void Render(string? highlight)
    {
        if (_text == null || _markdown == null) return;
        _document = Markdown.Render(_markdown, highlight);
        _text.Text = _document.Bbcode;
    }

    // ---------------------------------------------------------------- search

    private string Query => _search?.Text.Trim() ?? "";

    /// <summary>Re-renders with the matches highlighted and lists them; under two characters, back to the contents.</summary>
    private void Search()
    {
        if (_document == null || _results == null || _contents == null || _searchStatus == null) return;
        var query = Query;
        _matches.Clear();
        _current = -1;
        _results.Clear();

        if (query.Length < MinSearchLength)
        {
            Render(null);
            _results.Visible = false;
            _searchStatus.Visible = false;
            _contents.Visible = true;
            return;
        }

        Render(query);
        _matches.AddRange(_document.Blocks.Where(b => b.Text.Contains(query, StringComparison.OrdinalIgnoreCase)));
        foreach (var match in _matches.Take(MaxResults))
        {
            var index = _results.AddItem($"{match.Section}: {Snippet(match.Text, query)}");
            _results.SetItemTooltip(index, match.Text);
        }
        _contents.Visible = false;
        _results.Visible = true;
        _searchStatus.Visible = true;
        _searchStatus.Text = _matches.Count switch
        {
            0 => "No matches",
            1 => "1 match",
            > MaxResults => $"{_matches.Count} matches (first {MaxResults} listed) · Enter for the next",
            _ => $"{_matches.Count} matches · Enter for the next, Shift+Enter for the previous",
        };
        if (_matches.Count > 0) GoToMatch(0);
    }

    /// <summary>Enter / Shift+Enter in the search box: the next or previous match, wrapping around.</summary>
    private void Step(int direction)
    {
        if (_matches.Count == 0) return;
        GoToMatch(((_current + direction) % _matches.Count + _matches.Count) % _matches.Count);
        _search?.GrabFocus();
    }

    private void GoToMatch(int index)
    {
        if (_text == null || index < 0 || index >= _matches.Count) return;
        _current = index;
        _text.ScrollToParagraph(_matches[index].Paragraph);
        if (_results != null && index < _results.ItemCount)
        {
            _results.Select(index);
            _results.EnsureCurrentIsVisible();
        }
        if (_searchStatus != null && _matches.Count > 1) _searchStatus.Text = $"{index + 1} of {_matches.Count} · Enter for the next, Shift+Enter for the previous";
    }

    /// <summary>About <see cref="SnippetChars"/> characters around the first match, with "…" where cut.</summary>
    private static string Snippet(string text, string query)
    {
        var at = text.IndexOf(query, StringComparison.OrdinalIgnoreCase);
        if (text.Length <= SnippetChars || at < 0) return text.Length <= SnippetChars ? text : text[..SnippetChars] + "…";
        var start = Math.Max(0, at - SnippetChars / 3);
        var end = Math.Min(text.Length, start + SnippetChars);
        start = Math.Max(0, end - SnippetChars);
        return (start > 0 ? "…" : "") + text[start..end] + (end < text.Length ? "…" : "");
    }

    private void ScrollTo(string anchor)
    {
        if (_text == null || _document?.Find(anchor) is not { } heading) return;
        _text.ScrollToParagraph(heading.Paragraph);
    }
}
