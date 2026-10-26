using System;
using System.Collections.Generic;
using System.Linq;
using Godot;
using Valence.Schema;

namespace Valence.Viewer;

/// <summary>
/// Right-hand list of methods with a ▶ button each, which simulates the flow from just that method.
/// Shows the selected class's methods (entry points first), or every entry point when nothing is selected.
/// </summary>
public partial class MethodsPanel : PanelContainer
{
    private const int MaxRows = 300;

    private Label? _title;
    private VBoxContainer? _rows;

    /// <summary>Simulate downstream from this member id.</summary>
    public event Action<string>? PlayRequested;

    public void Build()
    {
        var column = new VBoxContainer();
        column.AddThemeConstantOverride("separation", 6);
        AddChild(column);

        _title = new Label { Text = "Methods", ClipText = true };
        _title.AddThemeColorOverride("font_color", Palette.Accent);
        column.AddChild(_title);

        var scroll = new ScrollContainer
        {
            SizeFlagsVertical = SizeFlags.ExpandFill,
            HorizontalScrollMode = ScrollContainer.ScrollMode.Disabled,
        };
        column.AddChild(scroll);

        _rows = new VBoxContainer { SizeFlagsHorizontal = SizeFlags.ExpandFill };
        _rows.AddThemeConstantOverride("separation", 2);
        scroll.AddChild(_rows);
    }

    /// <param name="canPlay">Whether a member has any flow to simulate.</param>
    public void ShowMembers(NodeData node, GraphModel model, Func<string, bool> canPlay)
    {
        SetTitle($"Methods · {node.Info.Name}");
        ClearRows();
        var members = node.Info.Members
            .Where(m => m.Kind is MemberKinds.Method or MemberKinds.Constructor or MemberKinds.Function or MemberKinds.Property)
            .OrderBy(m => model.EntryPointsByMember.ContainsKey(m.Id) ? 0 : 1)
            .ThenBy(m => m.Kind == MemberKinds.Property ? 1 : 0)
            .ThenBy(m => m.Line)
            .ToList();
        if (members.Count == 0)
        {
            AddNote(model.Graph.MemberEdges.Count == 0
                ? "No method data in this graph. Re-analyze the source folder (File ▸ Re-analyze)."
                : "No methods recorded for this class.");
            return;
        }

        foreach (var member in members.Take(MaxRows))
        {
            var badges = model.EntryPointsByMember.TryGetValue(member.Id, out var entries)
                ? string.Join(" ", entries.Select(e => $"▶ {e.Trigger}"))
                : "";
            AddRow(member.Id, member.Signature ?? member.Name, badges, canPlay(member.Id),
                   $"{member.Kind} · {member.Visibility}{(member.IsStatic ? " · static" : "")}\n{member.File}:{member.Line}");
        }
        if (members.Count > MaxRows) AddNote($"+{members.Count - MaxRows} more");
    }

    public void ShowEntryPoints(GraphModel model, Func<string, bool> canPlay)
    {
        var entries = model.Graph.EntryPoints;
        SetTitle($"Entry points ({entries.Count})");
        ClearRows();
        if (entries.Count == 0)
        {
            AddNote(model.Graph.MemberEdges.Count == 0
                ? "No method data in this graph. Re-analyze the source folder (File ▸ Re-analyze)."
                : "No entry points found. Select a class to see its methods.");
            return;
        }

        foreach (var group in entries.GroupBy(e => e.Trigger).OrderBy(g => g.Key, StringComparer.Ordinal))
        {
            AddNote(group.Key);
            foreach (var entry in group.Take(MaxRows))
            {
                var owner = model.MemberOwners.GetValueOrDefault(entry.Member);
                AddRow(entry.Member, entry.Label, "", canPlay(entry.Member),
                       owner != null ? $"{owner.Info.FullName}\n{owner.Info.File}" : entry.Member);
            }
        }
    }

    public void ShowMessage(string title, string message)
    {
        SetTitle(title);
        ClearRows();
        AddNote(message);
    }

    private void SetTitle(string text)
    {
        if (_title != null) _title.Text = text;
    }

    private void ClearRows()
    {
        if (_rows == null) return;
        foreach (var child in _rows.GetChildren())
        {
            _rows.RemoveChild(child);
            child.QueueFree();
        }
    }

    private void AddRow(string memberId, string text, string badges, bool playable, string tooltip)
    {
        if (_rows == null) return;
        var row = new HBoxContainer();
        row.AddThemeConstantOverride("separation", 6);

        var play = new Button
        {
            Text = "▶",
            Flat = true,
            Disabled = !playable,
            TooltipText = playable ? "Simulate the flow from here" : "Nothing recorded to simulate from here",
            CustomMinimumSize = new Vector2(28f, 0f),
        };
        play.AddThemeColorOverride("font_color", Palette.Accent);
        play.Pressed += () => PlayRequested?.Invoke(memberId);
        row.AddChild(play);

        var label = new Label
        {
            Text = text,
            ClipText = true,
            SizeFlagsHorizontal = SizeFlags.ExpandFill,
            TooltipText = tooltip,
            MouseFilter = MouseFilterEnum.Pass,
            VerticalAlignment = VerticalAlignment.Center,
        };
        label.AddThemeFontSizeOverride("font_size", 12);
        row.AddChild(label);

        if (badges.Length > 0)
        {
            var badge = new Label { Text = badges, VerticalAlignment = VerticalAlignment.Center };
            badge.AddThemeFontSizeOverride("font_size", 11);
            badge.AddThemeColorOverride("font_color", Palette.Accent);
            row.AddChild(badge);
        }
        _rows.AddChild(row);
    }

    private void AddNote(string text)
    {
        if (_rows == null) return;
        var note = new Label { Text = text, AutowrapMode = TextServer.AutowrapMode.WordSmart };
        note.AddThemeFontSizeOverride("font_size", 12);
        note.AddThemeColorOverride("font_color", Palette.MutedText);
        _rows.AddChild(note);
    }
}
