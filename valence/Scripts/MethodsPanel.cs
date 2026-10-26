using System;
using System.Collections.Generic;
using System.Linq;
using Godot;
using Valence.Schema;

namespace Valence.Viewer;

/// <summary>
/// Right-hand list of methods with a ▶ button each, which simulates the flow from just that method.
/// Shows the selected class's methods (entry points first), or every entry point when nothing is
/// selected. Typing in the search box searches every method in the repo instead.
/// </summary>
public partial class MethodsPanel : PanelContainer
{
    private const int MaxRows = 300;

    private Label? _title;
    private LineEdit? _search;
    private VBoxContainer? _rows;
    private GraphModel? _model;
    private NodeData? _context; // null = entry points
    private Func<string, bool> _canPlay = _ => false;

    /// <summary>Simulate downstream from this member id.</summary>
    public event Action<string>? PlayRequested;

    /// <summary>A method's name was clicked: select its class ("node:&lt;id&gt;").</summary>
    public event Action<string>? ClassChosen;

    public void Build()
    {
        var column = new VBoxContainer();
        column.AddThemeConstantOverride("separation", 6);
        AddChild(column);

        _title = new Label { Text = "Methods", ClipText = true };
        _title.AddThemeColorOverride("font_color", Palette.Accent);
        column.AddChild(_title);

        _search = new LineEdit { PlaceholderText = "Search all methods… (e.g. _Process, Save)", ClearButtonEnabled = true };
        _search.TextChanged += _ => Refresh();
        _search.TextSubmitted += _ => PlayFirstResult();
        column.AddChild(_search);

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
        _model = model;
        _context = node;
        _canPlay = canPlay;
        Refresh();
    }

    public void ShowEntryPoints(GraphModel model, Func<string, bool> canPlay)
    {
        _model = model;
        _context = null;
        _canPlay = canPlay;
        Refresh();
    }

    private string Query => _search?.Text.Trim() ?? "";

    private void Refresh()
    {
        ClearRows();
        if (_model == null) return;
        if (Query.Length > 0) ShowSearch(_model, Query);
        else if (_context != null) RenderMembers(_context, _model);
        else RenderEntryPoints(_model);
    }

    // ---------------------------------------------------------------- views

    private void RenderMembers(NodeData node, GraphModel model)
    {
        SetTitle($"Methods · {node.Info.Name}");
        var members = node.Info.Members
            .Where(IsPlayableKind)
            .OrderBy(m => m.Name, StringComparer.OrdinalIgnoreCase)
            .ThenBy(m => m.Signature, StringComparer.OrdinalIgnoreCase)
            .ToList();
        if (members.Count == 0)
        {
            AddNote(NoDataMessage(model, "No methods recorded for this class."));
            return;
        }

        foreach (var member in members.Take(MaxRows))
        {
            AddRow(member, node, model, member.Signature ?? member.Name);
        }
        if (members.Count > MaxRows) AddNote($"+{members.Count - MaxRows} more");
    }

    private void RenderEntryPoints(GraphModel model)
    {
        var entries = model.Graph.EntryPoints;
        SetTitle($"Entry points ({entries.Count})");
        if (entries.Count == 0)
        {
            AddNote(NoDataMessage(model, "No entry points found. Select a class, or search above."));
            return;
        }

        foreach (var group in entries.GroupBy(e => e.Trigger).OrderBy(g => g.Key, StringComparer.Ordinal))
        {
            AddNote(group.Key);
            foreach (var entry in group.OrderBy(e => e.Label, StringComparer.OrdinalIgnoreCase).Take(MaxRows))
            {
                if (!model.MemberOwners.TryGetValue(entry.Member, out var owner)) continue;
                var member = owner.Info.Members.FirstOrDefault(m => m.Id == entry.Member);
                if (member != null) AddRow(member, owner, model, entry.Label);
            }
        }
    }

    private void ShowSearch(GraphModel model, string query)
    {
        var matches = new List<(MemberInfo Member, NodeData Owner)>();
        foreach (var node in model.Nodes.Values)
        {
            foreach (var member in node.Info.Members)
            {
                if (!IsPlayableKind(member)) continue;
                var qualified = $"{node.Info.Name}.{member.Name}";
                if (qualified.Contains(query, StringComparison.OrdinalIgnoreCase)
                    || member.Signature?.Contains(query, StringComparison.OrdinalIgnoreCase) == true)
                {
                    matches.Add((member, node));
                }
            }
        }

        SetTitle($"Search · {matches.Count} method{(matches.Count == 1 ? "" : "s")}");
        if (matches.Count == 0)
        {
            AddNote(NoDataMessage(model, $"No methods match \"{query}\"."));
            return;
        }

        var ordered = matches
            .OrderBy(m => m.Owner.Info.Name, StringComparer.OrdinalIgnoreCase)
            .ThenBy(m => m.Member.Name, StringComparer.OrdinalIgnoreCase)
            .ThenBy(m => m.Member.Signature, StringComparer.OrdinalIgnoreCase)
            .ToList();
        foreach (var (member, owner) in ordered.Take(MaxRows))
        {
            AddRow(member, owner, model, $"{owner.Info.Name}.{member.Signature ?? member.Name}");
        }
        if (ordered.Count > MaxRows) AddNote($"+{ordered.Count - MaxRows} more; keep typing to narrow it down");
    }

    /// <summary>Enter in the search box plays the best match.</summary>
    private void PlayFirstResult()
    {
        if (_rows == null) return;
        foreach (var child in _rows.GetChildren())
        {
            if (child is HBoxContainer row && row.GetChildCount() > 0 && row.GetChild(0) is Button { Disabled: false } play)
            {
                play.EmitSignal(BaseButton.SignalName.Pressed);
                return;
            }
        }
    }

    // ---------------------------------------------------------------- rows

    private static bool IsPlayableKind(MemberInfo member) =>
        member.Kind is MemberKinds.Method or MemberKinds.Constructor or MemberKinds.Function or MemberKinds.Property;

    private static string NoDataMessage(GraphModel model, string fallback) =>
        model.Graph.MemberEdges.Count == 0
            ? "No method data in this graph. Re-analyze the source folder (File ▸ Re-analyze)."
            : fallback;

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

    private void AddRow(MemberInfo member, NodeData owner, GraphModel model, string text)
    {
        if (_rows == null) return;
        var playable = _canPlay(member.Id);
        var row = new HBoxContainer();
        row.AddThemeConstantOverride("separation", 4);

        var play = new Button
        {
            Text = "▶",
            Flat = true,
            Disabled = !playable,
            TooltipText = playable ? "Simulate the flow from here" : "Nothing recorded to simulate from here",
            CustomMinimumSize = new Vector2(28f, 0f),
        };
        play.AddThemeColorOverride("font_color", Palette.Accent);
        var memberId = member.Id;
        play.Pressed += () => PlayRequested?.Invoke(memberId);
        row.AddChild(play);

        var name = new Button
        {
            Text = text,
            Flat = true,
            ClipText = true,
            Alignment = HorizontalAlignment.Left,
            SizeFlagsHorizontal = SizeFlags.ExpandFill,
            TooltipText = $"{owner.Info.FullName}\n{member.Kind} · {member.Visibility}{(member.IsStatic ? " · static" : "")}\n" +
                          $"{member.File}:{member.Line}\nClick to select the class",
        };
        name.AddThemeFontSizeOverride("font_size", 12);
        var nodeId = owner.Info.Id;
        name.Pressed += () => ClassChosen?.Invoke("node:" + nodeId);
        row.AddChild(name);

        if (model.EntryPointsByMember.TryGetValue(member.Id, out var entries))
        {
            var badge = new Label
            {
                Text = string.Join(" ", entries.Select(e => $"▶ {e.Trigger}")),
                VerticalAlignment = VerticalAlignment.Center,
            };
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
