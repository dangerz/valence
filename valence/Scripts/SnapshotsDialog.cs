using System;
using System.Collections.Generic;
using System.Linq;
using Godot;

namespace Valence.Viewer;

/// <summary>
/// File ▸ Snapshots… (also View ▸ Compare With Snapshot ▸ Manage Snapshots…): every snapshot of the
/// open repo, newest first, with Compare, Rename and Delete. Delete asks for a second click and
/// moves the file to the trash. Main does the work; this only shows the list and raises events.
/// </summary>
public partial class SnapshotsDialog : ModalDialog
{
    private const float ButtonWidth = 92f;

    private VBoxContainer? _rows;
    private Label? _intro;
    private string _repoName = "";
    private List<SnapshotEntry> _entries = new();
    private string? _comparingPath;
    private string? _renamingPath;
    private string? _confirmDeletePath;

    public event Action<string>? CompareRequested;

    /// <summary>(path, new name); an empty name goes back to the date.</summary>
    public event Action<string, string>? RenameRequested;

    public event Action<string>? DeleteRequested;

    public event Action? TakeRequested;

    public void BuildList()
    {
        Build("Snapshots", 700f);

        _intro = AddText("");
        _intro.AddThemeColorOverride("font_color", Palette.MutedText);

        var scroll = new ScrollContainer
        {
            CustomMinimumSize = new Vector2(ContentWidth, 330f),
            HorizontalScrollMode = ScrollContainer.ScrollMode.Disabled,
        };
        _rows = new VBoxContainer { SizeFlagsHorizontal = SizeFlags.ExpandFill };
        _rows.AddThemeConstantOverride("separation", 6);
        scroll.AddChild(_rows);
        Body.AddChild(scroll);

        var take = new Button { Text = "Take Snapshot Now", SizeFlagsHorizontal = SizeFlags.ShrinkBegin };
        take.Pressed += () => TakeRequested?.Invoke();
        Body.AddChild(take);
    }

    /// <summary>Replaces the list (after opening, taking, renaming or deleting a snapshot).</summary>
    public void SetEntries(string repoName, IEnumerable<SnapshotEntry> entries, string? comparingPath)
    {
        _repoName = repoName;
        _entries = entries.ToList();
        _comparingPath = comparingPath;
        _renamingPath = null;
        _confirmDeletePath = null;
        Rebuild();
    }

    private void Rebuild()
    {
        if (_rows == null || _intro == null) return;
        foreach (var child in _rows.GetChildren())
        {
            _rows.RemoveChild(child);
            child.QueueFree();
        }

        _intro.Text = _entries.Count == 0
            ? $"No snapshots of {_repoName} yet. Take one now; after the code changes, re-analyze and compare."
            : $"{_entries.Count} snapshot{(_entries.Count == 1 ? "" : "s")} of {_repoName}, newest first. " +
              "Compare one with the current graph to see what was added and changed since.";
        foreach (var entry in _entries) _rows.AddChild(Row(entry));
    }

    private Control Row(SnapshotEntry entry)
    {
        var comparing = entry.Path == _comparingPath;
        var panel = new PanelContainer { SizeFlagsHorizontal = SizeFlags.ExpandFill };
        panel.AddThemeStyleboxOverride("panel",
            ValenceTheme.Box(ValenceTheme.PanelRaised, comparing ? Palette.Accent : Palette.PanelBorder, 1, 6, 8f));
        var row = new HBoxContainer();
        row.AddThemeConstantOverride("separation", 8);
        panel.AddChild(row);

        var text = new VBoxContainer { SizeFlagsHorizontal = SizeFlags.ExpandFill };
        text.AddThemeConstantOverride("separation", 2);
        row.AddChild(text);
        text.AddChild(entry.Path == _renamingPath ? NameEditor(entry) : new Label
        {
            Text = entry.Name ?? entry.Stamp,
            TextOverrunBehavior = TextServer.OverrunBehavior.TrimEllipsis,
            SizeFlagsHorizontal = SizeFlags.ExpandFill,
        });

        var detail = new Label
        {
            Text = Detail(entry, comparing),
            TextOverrunBehavior = TextServer.OverrunBehavior.TrimEllipsis,
            SizeFlagsHorizontal = SizeFlags.ExpandFill,
        };
        detail.AddThemeFontSizeOverride("font_size", 12);
        detail.AddThemeColorOverride("font_color", comparing ? Palette.Accent : Palette.MutedText);
        text.AddChild(detail);

        var compare = RowButton(comparing ? "Comparing" : "Compare", () => CompareRequested?.Invoke(entry.Path));
        compare.Disabled = comparing;
        row.AddChild(compare);

        row.AddChild(RowButton("Rename", () =>
        {
            _renamingPath = entry.Path;
            _confirmDeletePath = null;
            Later(Rebuild);
        }));

        var confirming = entry.Path == _confirmDeletePath;
        var delete = RowButton(confirming ? "Confirm" : "Delete", () =>
        {
            if (_confirmDeletePath == entry.Path)
            {
                _confirmDeletePath = null;
                Later(() => DeleteRequested?.Invoke(entry.Path));
            }
            else
            {
                _confirmDeletePath = entry.Path;
                _renamingPath = null;
                Later(Rebuild);
            }
        });
        if (confirming)
        {
            delete.AddThemeColorOverride("font_color", Palette.Proton);
            delete.AddThemeColorOverride("font_hover_color", Palette.Proton.Lightened(0.2f));
            delete.TooltipText = "Moves the snapshot file to the trash";
        }
        row.AddChild(delete);
        return panel;
    }

    /// <summary>Enter or leaving the field saves; Esc cancels. Empty goes back to the date.</summary>
    private LineEdit NameEditor(SnapshotEntry entry)
    {
        var edit = new LineEdit
        {
            Text = entry.Name ?? "",
            PlaceholderText = entry.Stamp,
            SelectAllOnFocus = true,
            SizeFlagsHorizontal = SizeFlags.ExpandFill,
        };
        var finished = false;
        void Finish(bool save)
        {
            if (finished) return;
            finished = true;
            _renamingPath = null;
            var name = edit.Text.Trim();
            if (save) Later(() => RenameRequested?.Invoke(entry.Path, name));
            else Later(Rebuild);
        }
        edit.TextSubmitted += _ => Finish(save: true);
        edit.FocusExited += () => Finish(save: true);
        edit.GuiInput += e =>
        {
            if (e is not InputEventKey { Pressed: true, Keycode: Key.Escape }) return;
            edit.AcceptEvent();
            Finish(save: false);
        };
        Later(() => edit.GrabFocus());
        return edit;
    }

    private static string Detail(SnapshotEntry entry, bool comparing)
    {
        var parts = new List<string>();
        if (entry.Name != null) parts.Add(entry.Stamp);
        if (entry.Commit != null) parts.Add("commit " + entry.Commit);
        parts.Add(FormatSize(entry.Bytes));
        if (comparing) parts.Add("being compared");
        return string.Join(" · ", parts);
    }

    private static string FormatSize(long bytes) => bytes >= 1024 * 1024
        ? $"{bytes / (1024.0 * 1024.0):0.0} MB"
        : $"{Math.Max(1, bytes / 1024)} KB";

    private static Button RowButton(string text, Action pressed)
    {
        var button = new Button { Text = text, CustomMinimumSize = new Vector2(ButtonWidth, 0f) };
        button.Pressed += pressed;
        return button;
    }

    /// <summary>Runs after the current input/signal: rows can't be rebuilt while one of their own controls is handling it.</summary>
    private static void Later(Action action) => Callable.From(action).CallDeferred();
}
