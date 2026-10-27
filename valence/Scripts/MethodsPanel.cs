using System;
using System.Collections.Generic;
using System.Linq;
using Godot;
using Valence.Schema;

namespace Valence.Viewer;

/// <summary>
/// Right-hand list of methods with a ▶ button each, which simulates the flow from just that method.
/// With nothing selected it lists every entry point, tagged by trigger, with chips to narrow it to
/// one trigger; with a class selected, that class's methods. Typing in the search box searches every
/// method in the repo instead. Each row's ⋯ menu simulates, selects the class or copies its location.
/// </summary>
public partial class MethodsPanel : PanelContainer
{
    private const int MaxRows = 300;
    private const int MenuPlay = 0;
    private const int MenuSelectClass = 1;
    private const int MenuCopyLocation = 2;
    private const int PanelClear = 10;
    private const int PanelPlayFirst = 11;

    private Label? _title;
    private Label? _count;
    private Label? _subtitle;
    private LineEdit? _search;
    private HFlowContainer? _chips;
    private VBoxContainer? _rows;
    private PopupMenu? _rowMenu;
    private GraphModel? _model;
    private NodeData? _context; // null = entry points
    private string? _trigger;   // entry points: only this trigger (null = all)
    private Func<string, bool> _canPlay = _ => false;
    private (MemberInfo Member, NodeData Owner)? _menuTarget;

    /// <summary>Simulate downstream from this member id.</summary>
    public event Action<string>? PlayRequested;

    /// <summary>A method's name was clicked: select its class ("node:&lt;id&gt;").</summary>
    public event Action<string>? ClassChosen;

    public void Build()
    {
        var column = new VBoxContainer();
        column.AddThemeConstantOverride("separation", 10);
        AddChild(column);

        _title = UiKit.Title("Entry points");
        var header = UiKit.Header("grid", _title);
        header.AddChild(UiKit.Badge(out var count));
        _count = count;
        header.AddChild(UiKit.Spacer());
        var menu = UiKit.IconMenu("more-vertical", "More");
        var popup = menu.GetPopup();
        popup.AddItem("Clear Search and Filter", PanelClear);
        popup.AddItem("Play First Result", PanelPlayFirst);
        popup.IdPressed += id =>
        {
            if (id == PanelClear) ClearSearchAndFilter();
            else if (id == PanelPlayFirst) PlayFirstResult();
        };
        header.AddChild(menu);
        column.AddChild(header);

        _subtitle = new Label { Visible = false, TextOverrunBehavior = TextServer.OverrunBehavior.TrimEllipsis };
        _subtitle.AddThemeFontSizeOverride("font_size", 13);
        _subtitle.AddThemeColorOverride("font_color", Palette.MutedText);
        column.AddChild(_subtitle);

        column.AddChild(UiKit.SearchField("Search all methods… (e.g. _Process, Save)", out var search));
        _search = search;
        _search.TextChanged += _ => Refresh();
        _search.TextSubmitted += _ => PlayFirstResult();

        _chips = new HFlowContainer { Visible = false };
        _chips.AddThemeConstantOverride("h_separation", 6);
        _chips.AddThemeConstantOverride("v_separation", 6);
        column.AddChild(_chips);

        var scroll = new ScrollContainer
        {
            SizeFlagsVertical = SizeFlags.ExpandFill,
            HorizontalScrollMode = ScrollContainer.ScrollMode.Disabled,
        };
        column.AddChild(scroll);

        _rows = new VBoxContainer { SizeFlagsHorizontal = SizeFlags.ExpandFill };
        _rows.AddThemeConstantOverride("separation", 2);
        scroll.AddChild(_rows);

        // One menu shared by every row's ⋯ (cheaper than a popup per row).
        _rowMenu = new PopupMenu();
        _rowMenu.AddItem("Simulate from Here", MenuPlay);
        _rowMenu.AddItem("Select Class", MenuSelectClass);
        _rowMenu.AddItem("Copy Location (file:line)", MenuCopyLocation);
        _rowMenu.IdPressed += OnRowMenu;
        AddChild(_rowMenu);
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
        if (_chips != null) _chips.Visible = false;
        if (_model == null) return;
        if (Query.Length > 0) ShowSearch(_model, Query);
        else if (_context != null) RenderMembers(_context, _model);
        else RenderEntryPoints(_model);
    }

    private void ClearSearchAndFilter()
    {
        _trigger = null;
        if (_search != null) _search.Text = "";
        Refresh();
    }

    // ---------------------------------------------------------------- views

    private void RenderMembers(NodeData node, GraphModel model)
    {
        var members = node.Info.Members
            .Where(IsPlayableKind)
            .OrderBy(m => m.Name, StringComparer.OrdinalIgnoreCase)
            .ThenBy(m => m.Signature, StringComparer.OrdinalIgnoreCase)
            .ToList();
        SetTitle("Methods", members.Count, node.Info.Name);
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
        var all = model.Graph.EntryPoints;
        if (_trigger != null && all.All(e => e.Trigger != _trigger)) _trigger = null;
        var entries = all
            .Where(e => _trigger == null || e.Trigger == _trigger)
            .OrderBy(e => e.Trigger, StringComparer.Ordinal)
            .ThenBy(e => e.Label, StringComparer.OrdinalIgnoreCase)
            .ToList();
        SetTitle("Entry points", entries.Count, null);
        if (all.Count == 0)
        {
            AddNote(NoDataMessage(model, "No entry points found. Select a class, or search above."));
            return;
        }
        RenderChips(all);

        foreach (var entry in entries.Take(MaxRows))
        {
            if (!model.MemberOwners.TryGetValue(entry.Member, out var owner)) continue;
            var member = owner.Info.Members.FirstOrDefault(m => m.Id == entry.Member);
            if (member != null) AddRow(member, owner, model, entry.Label);
        }
        if (entries.Count > MaxRows) AddNote($"+{entries.Count - MaxRows} more; pick a trigger or search to narrow it down");
    }

    /// <summary>One chip per trigger ("draw 12"); once one is picked, just it (✕ to clear) and Clear.</summary>
    private void RenderChips(List<EntryPointInfo> entries)
    {
        if (_chips == null) return;
        foreach (var child in _chips.GetChildren())
        {
            _chips.RemoveChild(child);
            child.QueueFree();
        }

        if (_trigger != null)
        {
            var chosen = Chip(_trigger, active: true);
            chosen.Icon = UiAssets.Icon("close");
            chosen.IconAlignment = HorizontalAlignment.Right;
            chosen.TooltipText = "Show every trigger";
            chosen.Pressed += () => Later(() => SetTrigger(null));
            _chips.AddChild(chosen);

            var clear = new Button { Text = "Clear", Flat = true, FocusMode = FocusModeEnum.None };
            clear.AddThemeColorOverride("font_color", UiKit.TitleColor);
            clear.AddThemeFontSizeOverride("font_size", 13);
            clear.Pressed += () => Later(ClearSearchAndFilter);
            _chips.AddChild(clear);
        }
        else
        {
            foreach (var group in entries.GroupBy(e => e.Trigger).OrderBy(g => g.Key, StringComparer.Ordinal))
            {
                var trigger = group.Key;
                var chip = Chip($"{trigger}  {group.Count()}", active: false);
                chip.TooltipText = $"Only {trigger} entry points";
                chip.Pressed += () => Later(() => SetTrigger(trigger));
                _chips.AddChild(chip);
            }
        }
        _chips.Visible = true;
    }

    private void SetTrigger(string? trigger)
    {
        _trigger = trigger;
        Refresh();
    }

    private static Button Chip(string text, bool active)
    {
        var chip = new Button { Text = text, FocusMode = FocusModeEnum.None, CustomMinimumSize = new Vector2(0f, 28f) };
        chip.AddThemeFontSizeOverride("font_size", 13);
        chip.AddThemeStyleboxOverride("normal", UiAssets.Box(active ? Patch.Active : Patch.Panel, 10f, 3f));
        chip.AddThemeStyleboxOverride("hover", UiAssets.Box(Patch.Accent, 10f, 3f));
        chip.AddThemeStyleboxOverride("pressed", UiAssets.Box(Patch.Active, 10f, 3f));
        chip.AddThemeConstantOverride("icon_max_width", 14);
        return chip;
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

        SetTitle("Search", matches.Count, $"Methods matching \"{query}\"");
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

    private void SetTitle(string title, int count, string? subtitle)
    {
        if (_title != null) _title.Text = title.ToUpperInvariant();
        if (_count != null) _count.Text = count.ToString("N0");
        if (_subtitle == null) return;
        _subtitle.Text = subtitle ?? "";
        _subtitle.Visible = !string.IsNullOrEmpty(subtitle);
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
        var memberId = member.Id;
        var row = new HBoxContainer();
        row.AddThemeConstantOverride("separation", 6);

        var play = UiKit.IconButton("play",
            playable ? "Simulate the flow from here" : "Nothing recorded to simulate from here",
            () => PlayRequested?.Invoke(memberId), 28f, flat: true);
        play.Disabled = !playable;
        row.AddChild(play);

        var name = new Button
        {
            Text = text,
            Flat = true,
            ClipText = true,
            Alignment = HorizontalAlignment.Left,
            SizeFlagsHorizontal = SizeFlags.ExpandFill,
            FocusMode = FocusModeEnum.None,
            TooltipText = $"{owner.Info.FullName}\n{member.Kind} · {member.Visibility}{(member.IsStatic ? " · static" : "")}\n" +
                          $"{member.File}:{member.Line}\nClick to select the class",
        };
        name.AddThemeFontSizeOverride("font_size", 14);
        var nodeId = owner.Info.Id;
        name.Pressed += () => ClassChosen?.Invoke("node:" + nodeId);
        row.AddChild(name);

        // The trigger(s) for entry points, else the member kind.
        var isEntry = model.EntryPointsByMember.TryGetValue(member.Id, out var entries);
        var tag = new Label
        {
            Text = isEntry ? string.Join(" ", entries!.Select(e => e.Trigger).Distinct()) : member.Kind,
            VerticalAlignment = VerticalAlignment.Center,
        };
        tag.AddThemeFontSizeOverride("font_size", 13);
        tag.AddThemeColorOverride("font_color", isEntry ? UiKit.TitleColor : Palette.MutedText);
        row.AddChild(tag);

        var more = UiKit.IconButton("more", "More", null, 26f, flat: true);
        more.Pressed += () => OpenRowMenu(member, owner, playable);
        row.AddChild(more);
        _rows.AddChild(row);
    }

    private void OpenRowMenu(MemberInfo member, NodeData owner, bool playable)
    {
        if (_rowMenu == null) return;
        _menuTarget = (member, owner);
        _rowMenu.SetItemDisabled(_rowMenu.GetItemIndex(MenuPlay), !playable);
        _rowMenu.Position = (Vector2I)GetGlobalMousePosition();
        _rowMenu.Popup();
    }

    private void OnRowMenu(long id)
    {
        if (_menuTarget is not { } target) return;
        switch (id)
        {
            case MenuPlay:
                PlayRequested?.Invoke(target.Member.Id);
                break;
            case MenuSelectClass:
                ClassChosen?.Invoke("node:" + target.Owner.Info.Id);
                break;
            case MenuCopyLocation:
                DisplayServer.ClipboardSet($"{target.Member.File}:{target.Member.Line}");
                break;
        }
    }

    private void AddNote(string text)
    {
        if (_rows == null) return;
        var note = new Label { Text = text, AutowrapMode = TextServer.AutowrapMode.WordSmart };
        note.AddThemeFontSizeOverride("font_size", 12);
        note.AddThemeColorOverride("font_color", Palette.MutedText);
        _rows.AddChild(note);
    }

    /// <summary>After the current signal: a chip can't remove itself while it's being pressed.</summary>
    private static void Later(Action action) => Callable.From(action).CallDeferred();
}
