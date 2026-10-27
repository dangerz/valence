using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Godot;

namespace Valence.Viewer;

/// <summary>
/// Screen-space UI: a traditional menu bar, docked Outline (left) and Inspector (right) panels,
/// a legend over the 3D view, a status bar, hover tooltips, and the analysis progress dialog.
/// </summary>
public partial class Hud : Control
{
    // Menu item ids.
    private const int FileOpenSource = 1;
    private const int FileOpenGraph = 2;
    private const int FileReanalyze = 3;
    private const int FileQuit = 9;
    private const int ViewFrameAll = 20;
    private const int ViewResetLayout = 21;
    private const int ViewOutline = 22;
    private const int ViewInspector = 23;
    private const int ViewLegend = 24;
    private const int ViewMethods = 25;
    private const int ViewShowAll = 26;
    private const int ViewStopCompare = 27;
    private const int ViewUsageFilter = 28;
    private const int FileSnapshot = 4;
    private const int TimelineBuild10 = 110;
    private const int TimelineBuild25 = 111;
    private const int TimelineBuild50 = 112;
    private const int TimelineShow = 113;
    private const int TimelineHide = 114;
    private const int CompareBase = 2000;

    private static readonly float[] Brightnesses = { 0.25f, 0.5f, 0.75f, 1f, 1.25f };

    private static readonly float[] QuietFades = { 0.05f, 0.1f, 0.15f, 0.25f, 0.4f };
    private const int NavigateFind = 40;
    private const int NavigateBack = 41;
    private const int NavigateClear = 42;
    private const int HelpControls = 60;
    private const int HelpAbout = 61;
    private const int SimDownstream = 80;
    private const int SimUpstream = 81;
    private const int SimGameLoop = 82;
    private const int SimPause = 86;
    private const int SimStop = 87;
    private const int SimData = 88;
    private const int TriggerBase = 100;
    private const int RecentBase = 1000;

    private static readonly float[] Speeds = { 0.25f, 0.5f, 1f, 2f, 4f };

    private static readonly (ColorMode Mode, string Label)[] ColorModes =
    {
        (ColorMode.Visibility, "Protons / Neutrons (public vs internal)"),
        (ColorMode.Kind, "Kind (class, interface, struct…)"),
        (ColorMode.Churn, "Churn (recent git changes)"),
        (ColorMode.Coupling, "Coupling (how many depend on it)"),
    };

    private readonly List<string> _recentFolders = new();
    private readonly List<string> _triggers = new();
    private PopupMenu? _simulateMenu;
    private PopupMenu? _fadeMenu;
    private PopupMenu? _triggerMenu;
    private PopupMenu? _speedMenu;
    private PopupMenu? _fileMenu;
    private PopupMenu? _viewMenu;
    private PopupMenu? _recentMenu;
    private PopupMenu? _colorMenu;
    private PopupMenu? _depthMenu;
    private Label? _repoLabel;
    private Label? _status;
    private Label? _selectionLabel;
    private PanelContainer? _inspector;
    private RichTextLabel? _info;
    private PanelContainer? _legend;
    private RichTextLabel? _legendNucleons;
    private PanelContainer? _tooltip;
    private Label? _tooltipLabel;
    private FileDialog? _graphDialog;
    private FileDialog? _sourceDialog;
    private ModalDialog? _helpDialog;
    private ModalDialog? _aboutDialog;

    public OutlinePanel Outline { get; } = new();
    public MethodsPanel Methods { get; } = new();
    private VBoxContainer? _rightColumn;
    private PanelResizer? _outlineResizer;
    private PanelResizer? _rightResizer;
    private PopupMenu? _untouchedMenu;
    private PopupMenu? _brightnessMenu;
    private PopupMenu? _labelsMenu;
    private PopupMenu? _compareMenu;
    private readonly List<string> _snapshotPaths = new();
    private PanelContainer? _timelineBar;
    private HSlider? _timelineSlider;
    private Label? _timelineLabel;
    private Button? _timelinePlay;
    private bool _suppressTimelineSlider;
    private PanelContainer? _filterBar;
    private RangeSlider? _filterRange;
    private OptionButton? _filterMode;
    private Label? _filterValue;
    private Label? _filterCount;
    private const float MinPanelWidth = 180f;
    private const float MaxPanelWidth = 900f;
    public ProgressOverlay Progress { get; } = new();

    public event Action<string>? SourceFolderChosen;
    public event Action<string>? GraphFileChosen;
    public event Action<string>? RecentFolderChosen;
    public event Action? ReanalyzeRequested;
    public event Action? QuitRequested;
    public event Action<ColorMode>? ColorModeChanged;
    public event Action<int>? LinkDepthChanged;
    public event Action? FrameAllRequested;
    public event Action? ResetLayoutRequested;
    public event Action? BackRequested;
    public event Action? ClearSelectionRequested;
    public event Action<string, bool>? EdgeKindToggled;
    public event Action<FlowDirection>? FlowRequested;
    public event Action? GameLoopRequested;
    public event Action<string>? TriggerRequested;
    public event Action? PauseToggleRequested;
    public event Action? StopSimulationRequested;
    public event Action<float>? SpeedChanged;
    public event Action<bool>? IncludeDataChanged;
    public event Action<float>? QuietFadeChanged;
    public event Action? ShowAllRequested;
    public event Action<UntouchedMode>? UntouchedModeChanged;
    public event Action<float>? BrightnessChanged;
    public event Action<LabelMode>? LabelModeChanged;
    public event Action? SnapshotRequested;
    public event Action<string>? CompareRequested;
    public event Action? StopCompareRequested;
    public event Action<int>? TimelineBuildRequested;
    public event Action? TimelineShowRequested;
    public event Action? TimelineHideRequested;
    public event Action<int>? TimelineFrameChanged;
    public event Action? TimelinePlayToggled;

    /// <summary>Usage filter: active, min used-by, max used-by, ghost (else hide).</summary>
    public event Action<bool, int, int, bool>? UsageFilterChanged;

    /// <summary>Outline width, right column width (after a border drag).</summary>
    public event Action<float, float>? PanelWidthsChanged;

    /// <summary>A "node:" / "atom:" link was clicked in the inspector or picked in the outline.</summary>
    public event Action<string>? LinkClicked;

    /// <summary>Outline, methods, inspector, legend visibility changed (for saving).</summary>
    public event Action<bool, bool, bool, bool>? PanelsChanged;

    /// <summary>The pointer moved onto a UI panel (so 3D hover should end).</summary>
    public event Action? PointerEnteredUi;

    /// <summary>Builds the UI. Call once, before adding to the tree.</summary>
    public void Build(bool showOutline, bool showMethods, bool showInspector, bool showLegend, float outlineWidth, float rightWidth)
    {
        SetAnchorsPreset(LayoutPreset.FullRect);
        MouseFilter = MouseFilterEnum.Ignore;
        // Menus, popups and dialogs below inherit this.
        Theme = ValenceTheme.Build();

        var root = new VBoxContainer { MouseFilter = MouseFilterEnum.Ignore };
        root.SetAnchorsPreset(LayoutPreset.FullRect);
        root.AddThemeConstantOverride("separation", 0);
        AddChild(root);

        root.AddChild(BuildMenuBar());

        var middle = new HBoxContainer { MouseFilter = MouseFilterEnum.Ignore, SizeFlagsVertical = SizeFlags.ExpandFill };
        middle.AddThemeConstantOverride("separation", 0);
        root.AddChild(middle);

        Outline.AddThemeStyleboxOverride("panel", PanelStyle(0.92f, 0));
        Outline.CustomMinimumSize = new Vector2(Mathf.Clamp(outlineWidth, MinPanelWidth, MaxPanelWidth), 0f);
        Outline.Build();
        Outline.ItemChosen += meta => LinkClicked?.Invoke(meta);
        Outline.MouseEntered += () => PointerEnteredUi?.Invoke();
        Outline.Visible = showOutline;
        middle.AddChild(Outline);

        // The Outline's right border: drag to resize.
        _outlineResizer = new PanelResizer();
        _outlineResizer.Dragged += dx => ResizePanel(Outline, dx);
        _outlineResizer.DragFinished += RaiseWidthsChanged;
        _outlineResizer.MouseEntered += () => PointerEnteredUi?.Invoke();
        middle.AddChild(_outlineResizer);

        // The 3D view shows through this area; it passes all input to the scene.
        var viewport = new Control { MouseFilter = MouseFilterEnum.Ignore, SizeFlagsHorizontal = SizeFlags.ExpandFill };
        middle.AddChild(viewport);
        BuildLegend(viewport, showLegend);

        // The right column's left border: drag to resize (dragging left widens it).
        _rightResizer = new PanelResizer();
        _rightResizer.Dragged += dx => ResizePanel(_rightColumn, -dx);
        _rightResizer.DragFinished += RaiseWidthsChanged;
        _rightResizer.MouseEntered += () => PointerEnteredUi?.Invoke();
        middle.AddChild(_rightResizer);

        // Right column: Methods (with ▶ simulate buttons) above the Inspector.
        _rightColumn = new VBoxContainer
        {
            CustomMinimumSize = new Vector2(Mathf.Clamp(rightWidth, MinPanelWidth, MaxPanelWidth), 0f),
            MouseFilter = MouseFilterEnum.Ignore,
        };
        _rightColumn.AddThemeConstantOverride("separation", 0);
        middle.AddChild(_rightColumn);

        Methods.AddThemeStyleboxOverride("panel", PanelStyle(0.92f, 0, borderBottom: 2));
        Methods.SizeFlagsVertical = SizeFlags.ExpandFill;
        Methods.SizeFlagsStretchRatio = 0.8f;
        Methods.Build();
        Methods.MouseEntered += () => PointerEnteredUi?.Invoke();
        Methods.Visible = showMethods;
        _rightColumn.AddChild(Methods);

        BuildInspector(showInspector);
        _rightColumn.AddChild(_inspector!);
        UpdateRightColumn();

        root.AddChild(BuildFilterBar());
        root.AddChild(BuildTimelineBar());
        root.AddChild(BuildStatusBar());

        BuildTooltip();
        BuildDialogs();

        Progress.Build();
        AddChild(Progress);

        SetChecked(_viewMenu, ViewOutline, showOutline);
        SetChecked(_viewMenu, ViewMethods, showMethods);
        SetChecked(_viewMenu, ViewInspector, showInspector);
        SetChecked(_viewMenu, ViewLegend, showLegend);
        SetColorMode(ColorMode.Visibility);
        SetLinkDepth(1);
        SetSpeed(1f);
        SetSimulationState(false, false);
        SetTriggers(Array.Empty<(string, int)>(), false);
    }

    // ---------------------------------------------------------------- public API

    public void SetRepo(string text)
    {
        if (_repoLabel != null) _repoLabel.Text = text;
    }

    public void SetStatus(string text)
    {
        if (_status != null) _status.Text = text;
    }

    public void SetSelection(string text)
    {
        if (_selectionLabel != null) _selectionLabel.Text = text;
    }

    public void ShowInfo(string bbcode)
    {
        if (_info == null) return;
        _info.Text = bbcode;
        _info.ScrollToLine(0);
    }

    public void SetReanalyzeEnabled(bool enabled) => SetDisabled(_fileMenu, FileReanalyze, !enabled);

    public void SetRecent(IEnumerable<string> folders)
    {
        _recentFolders.Clear();
        _recentFolders.AddRange(folders);
        if (_recentMenu == null) return;
        _recentMenu.Clear();
        if (_recentFolders.Count == 0)
        {
            _recentMenu.AddItem("(none yet)", RecentBase - 1);
            _recentMenu.SetItemDisabled(0, true);
            return;
        }
        for (var i = 0; i < _recentFolders.Count; i++) _recentMenu.AddItem(_recentFolders[i], RecentBase + i);
    }

    /// <summary>Fills Simulate ▸ Play Trigger with the entry-point triggers this graph has, e.g. "request (4)".</summary>
    public void SetTriggers(IEnumerable<(string Trigger, int Count)> triggers, bool hasGameLoop)
    {
        _triggers.Clear();
        SetDisabled(_simulateMenu, SimGameLoop, !hasGameLoop);
        if (_triggerMenu == null) return;
        _triggerMenu.Clear();
        foreach (var (trigger, count) in triggers)
        {
            _triggerMenu.AddItem($"{trigger} ({count})", TriggerBase + _triggers.Count);
            _triggers.Add(trigger);
        }
        if (_triggers.Count == 0)
        {
            _triggerMenu.AddItem("(no entry points; re-analyze the source)", TriggerBase - 1);
            _triggerMenu.SetItemDisabled(0, true);
        }
    }

    public void SetSimulationState(bool active, bool playing)
    {
        SetDisabled(_simulateMenu, SimPause, !active);
        SetDisabled(_simulateMenu, SimStop, !active);
        if (_simulateMenu == null) return;
        var index = _simulateMenu.GetItemIndex(SimPause);
        if (index >= 0) _simulateMenu.SetItemText(index, active && !playing ? "Resume" : "Pause");
    }

    public void SetLabelMode(LabelMode mode)
    {
        if (_labelsMenu == null) return;
        foreach (var each in new[] { LabelMode.All, LabelMode.NearMouse, LabelMode.OnClick })
        {
            SetChecked(_labelsMenu, (int)each, each == mode);
        }
    }

    public void SetBrightness(float brightness)
    {
        if (_brightnessMenu == null) return;
        var closest = 0;
        for (var i = 1; i < Brightnesses.Length; i++)
        {
            if (Mathf.Abs(Brightnesses[i] - brightness) < Mathf.Abs(Brightnesses[closest] - brightness)) closest = i;
        }
        for (var i = 0; i < Brightnesses.Length; i++) _brightnessMenu.SetItemChecked(i, i == closest);
    }

    /// <summary>Fills View ▸ Compare With Snapshot (newest first).</summary>
    public void SetSnapshots(IEnumerable<(string Label, string Path)> snapshots)
    {
        _snapshotPaths.Clear();
        if (_compareMenu == null) return;
        _compareMenu.Clear();
        foreach (var (label, path) in snapshots)
        {
            _compareMenu.AddItem(label, CompareBase + _snapshotPaths.Count);
            _snapshotPaths.Add(path);
        }
        if (_snapshotPaths.Count == 0)
        {
            _compareMenu.AddItem("(no snapshots yet: File ▸ Take Snapshot)", CompareBase - 1);
            _compareMenu.SetItemDisabled(0, true);
        }
    }

    public void SetCompareActive(bool active) => SetDisabled(_viewMenu, ViewStopCompare, !active);

    public void ShowTimeline(int frames)
    {
        if (_timelineBar == null || _timelineSlider == null) return;
        _suppressTimelineSlider = true;
        _timelineSlider.MaxValue = Math.Max(0, frames - 1);
        _suppressTimelineSlider = false;
        _timelineBar.Visible = true;
    }

    public void HideTimeline()
    {
        if (_timelineBar != null) _timelineBar.Visible = false;
    }

    public void SetTimelineFrame(int index, string label)
    {
        if (_timelineSlider != null)
        {
            _suppressTimelineSlider = true;
            _timelineSlider.Value = index;
            _suppressTimelineSlider = false;
        }
        if (_timelineLabel != null) _timelineLabel.Text = label;
    }

    public void SetTimelinePlaying(bool playing)
    {
        if (_timelinePlay != null) _timelinePlay.Text = playing ? "❚❚" : "▶";
    }

    private void OnCompare(int id)
    {
        var index = id - CompareBase;
        if (index >= 0 && index < _snapshotPaths.Count) CompareRequested?.Invoke(_snapshotPaths[index]);
    }

    /// <summary>Resets the range to 0..the highest used-by count in the graph (nothing filtered).</summary>
    public void SetUsageFilterRange(int max)
    {
        if (_filterRange == null) return;
        _filterRange.MaxValue = max;
        _filterRange.SetRange(0, _filterRange.MaxValue);
        RaiseUsageFilter();
    }

    /// <summary>"1,234 of 5,000 classes" next to the slider.</summary>
    public void SetUsageFilterCount(string text)
    {
        if (_filterCount != null) _filterCount.Text = text;
    }

    public void SetUsageFilterVisible(bool visible)
    {
        if (_filterBar == null) return;
        _filterBar.Visible = visible;
        SetChecked(_viewMenu, ViewUsageFilter, visible);
        RaiseUsageFilter();
    }

    private Control BuildFilterBar()
    {
        _filterBar = new PanelContainer { Visible = false };
        _filterBar.AddThemeStyleboxOverride("panel", PanelStyle(0.96f, 0, 6f, borderTop: 2));
        _filterBar.MouseEntered += () => PointerEnteredUi?.Invoke();

        var row = new HBoxContainer();
        row.AddThemeConstantOverride("separation", 10);
        _filterBar.AddChild(row);

        var title = new Label { Text = "Filter: used by", VerticalAlignment = VerticalAlignment.Center };
        title.AddThemeColorOverride("font_color", Palette.Accent);
        title.TooltipText = "How many other classes depend on a class (calls, creates, inherits, has a field of it, …)";
        row.AddChild(title);

        _filterRange = new RangeSlider
        {
            SizeFlagsHorizontal = SizeFlags.ExpandFill,
            SizeFlagsVertical = SizeFlags.ShrinkCenter,
            TooltipText = "Drag the handles to narrow the used-by range",
        };
        _filterRange.RangeChanged += (_, _) => RaiseUsageFilter();
        _filterRange.MouseEntered += () => PointerEnteredUi?.Invoke();
        row.AddChild(_filterRange);

        _filterValue = new Label { CustomMinimumSize = new Vector2(80f, 0f), VerticalAlignment = VerticalAlignment.Center };
        row.AddChild(_filterValue);

        _filterMode = new OptionButton();
        _filterMode.AddItem("Hide others", 0);
        _filterMode.AddItem("Ghost others", 1);
        _filterMode.ItemSelected += _ => RaiseUsageFilter();
        row.AddChild(_filterMode);

        _filterCount = new Label { CustomMinimumSize = new Vector2(200f, 0f), VerticalAlignment = VerticalAlignment.Center };
        _filterCount.AddThemeFontSizeOverride("font_size", 12);
        _filterCount.AddThemeColorOverride("font_color", Palette.MutedText);
        row.AddChild(_filterCount);

        var close = new Button { Text = "✕", Flat = true, TooltipText = "Turn the filter off" };
        close.Pressed += () => SetUsageFilterVisible(false);
        row.AddChild(close);
        return _filterBar;
    }

    private void RaiseUsageFilter()
    {
        if (_filterRange == null) return;
        if (_filterValue != null) _filterValue.Text = $"{_filterRange.Low} – {_filterRange.High}";
        UsageFilterChanged?.Invoke(_filterBar?.Visible ?? false, _filterRange.Low, _filterRange.High, _filterMode?.Selected == 1);
    }

    private Control BuildTimelineBar()
    {
        _timelineBar = new PanelContainer { Visible = false };
        _timelineBar.AddThemeStyleboxOverride("panel", PanelStyle(0.96f, 0, 6f, borderTop: 2));
        _timelineBar.MouseEntered += () => PointerEnteredUi?.Invoke();

        var row = new HBoxContainer();
        row.AddThemeConstantOverride("separation", 10);
        _timelineBar.AddChild(row);

        var title = new Label { Text = "Timeline", VerticalAlignment = VerticalAlignment.Center };
        title.AddThemeColorOverride("font_color", Palette.Accent);
        row.AddChild(title);

        _timelinePlay = new Button { Text = "▶", Flat = true, TooltipText = "Play through history", CustomMinimumSize = new Vector2(32f, 0f) };
        _timelinePlay.Pressed += () => TimelinePlayToggled?.Invoke();
        row.AddChild(_timelinePlay);

        _timelineSlider = new HSlider
        {
            MinValue = 0,
            MaxValue = 0,
            Step = 1,
            TickCount = 0,
            SizeFlagsHorizontal = SizeFlags.ExpandFill,
            SizeFlagsVertical = SizeFlags.ShrinkCenter,
            CustomMinimumSize = new Vector2(240f, 0f),
        };
        _timelineSlider.ValueChanged += value =>
        {
            if (!_suppressTimelineSlider) TimelineFrameChanged?.Invoke((int)Math.Round(value));
        };
        row.AddChild(_timelineSlider);

        _timelineLabel = new Label { ClipText = true, CustomMinimumSize = new Vector2(460f, 0f), VerticalAlignment = VerticalAlignment.Center };
        _timelineLabel.AddThemeFontSizeOverride("font_size", 12);
        row.AddChild(_timelineLabel);

        var close = new Button { Text = "✕", Flat = true, TooltipText = "Hide the timeline (back to the current code)" };
        close.Pressed += () => TimelineHideRequested?.Invoke();
        row.AddChild(close);
        return _timelineBar;
    }

    public void SetUntouchedMode(UntouchedMode mode)
    {
        if (_untouchedMenu == null) return;
        foreach (var each in new[] { UntouchedMode.Show, UntouchedMode.Ghost, UntouchedMode.Hide })
        {
            SetChecked(_untouchedMenu, (int)each, each == mode);
        }
    }

    public void SetQuietFade(float fade)
    {
        if (_fadeMenu == null) return;
        var closest = 0;
        for (var i = 1; i < QuietFades.Length; i++)
        {
            if (Mathf.Abs(QuietFades[i] - fade) < Mathf.Abs(QuietFades[closest] - fade)) closest = i;
        }
        for (var i = 0; i < QuietFades.Length; i++) _fadeMenu.SetItemChecked(i, i == closest);
    }

    public void SetSpeed(float speed)
    {
        if (_speedMenu == null) return;
        for (var i = 0; i < Speeds.Length; i++) _speedMenu.SetItemChecked(i, Mathf.IsEqualApprox(Speeds[i], speed));
    }

    public void SetColorMode(ColorMode mode)
    {
        if (_colorMenu == null) return;
        foreach (var (each, _) in ColorModes) SetChecked(_colorMenu, (int)each, each == mode);
    }

    public void SetLinkDepth(int depth)
    {
        if (_depthMenu == null) return;
        for (var d = 1; d <= 3; d++) SetChecked(_depthMenu, d, d == depth);
    }

    public void SetLegend(ColorMode mode, bool hasGit, int? churnDays)
    {
        if (_legendNucleons == null) return;
        var text = mode switch
        {
            ColorMode.Visibility =>
                $"[color={Palette.Html(Palette.Proton)}]●[/color] proton = public   " +
                $"[color={Palette.Html(Palette.Neutron)}]●[/color] neutron = internal/private",
            ColorMode.Kind => string.Join("  ",
                Palette.NodeKindLegend
                    .GroupBy(kv => kv.Value)
                    .Select(g => $"[color={Palette.Html(g.Key)}]●[/color] {g.First().Key}")),
            ColorMode.Churn => hasGit
                ? $"[color={Palette.Html(Palette.Heat(0))}]●[/color] cold → [color={Palette.Html(Palette.Heat(1))}]●[/color] hot = lines changed in the last {churnDays} days"
                : "No git history in this graph",
            ColorMode.Coupling =>
                $"[color={Palette.Html(Palette.Heat(0))}]●[/color] few → [color={Palette.Html(Palette.Heat(1))}]●[/color] many classes depend on it",
            _ => "",
        };
        text += "\nSize = lines of code";
        if (hasGit) text += " · [color=#ffffff]✦ glow[/color] = changed in the 14 days before this snapshot";
        _legendNucleons.Text = text;
    }

    /// <summary>Shows a tooltip next to the pointer, or hides it when text is null.</summary>
    public void ShowTooltip(string? text, Vector2 screenPosition)
    {
        if (_tooltip == null || _tooltipLabel == null) return;
        if (string.IsNullOrEmpty(text))
        {
            _tooltip.Visible = false;
            return;
        }
        _tooltipLabel.Text = text;
        _tooltip.ResetSize();
        var viewport = GetViewportRect().Size;
        var size = _tooltip.GetCombinedMinimumSize();
        var position = screenPosition + new Vector2(18f, 18f);
        if (position.X + size.X > viewport.X) position.X = screenPosition.X - size.X - 12f;
        if (position.Y + size.Y > viewport.Y) position.Y = screenPosition.Y - size.Y - 12f;
        _tooltip.Position = position;
        _tooltip.Visible = true;
    }

    public void PopupOpenSource() => PopupDialog(_sourceDialog, null);

    public void PopupOpenGraph() => PopupDialog(_graphDialog, DefaultGraphDir());

    public void FocusSearch()
    {
        if (!Outline.Visible) TogglePanel(ViewOutline);
        Outline.FocusFilter();
    }

    public static PanelContainer MakePanel(float alpha = 0.88f)
    {
        var panel = new PanelContainer();
        panel.AddThemeStyleboxOverride("panel", PanelStyle(alpha, 8, 10f, 1, 1, 1, 1));
        return panel;
    }

    // ---------------------------------------------------------------- construction

    private Control BuildMenuBar()
    {
        var bar = new PanelContainer();
        bar.AddThemeStyleboxOverride("panel", PanelStyle(0.96f, 0, 4f, borderBottom: 2));
        bar.MouseEntered += () => PointerEnteredUi?.Invoke();

        var row = new HBoxContainer();
        row.AddThemeConstantOverride("separation", 12);
        bar.AddChild(row);

        // Drawn in the window, not the macOS global menu (hidden in exclusive fullscreen).
        var menus = new MenuBar { PreferGlobalMenu = false };
        row.AddChild(menus);

        _fileMenu = new PopupMenu { Name = "File" };
        _fileMenu.AddItem("Open Source Folder…", FileOpenSource);
        SetShortcut(_fileMenu, FileOpenSource, Key.O);
        _fileMenu.AddItem("Open Graph…", FileOpenGraph);
        SetShortcut(_fileMenu, FileOpenGraph, Key.O, shift: true);
        _recentMenu = new PopupMenu { Name = "Recent" };
        _recentMenu.IdPressed += id => OnRecent((int)id);
        _fileMenu.AddSubmenuNodeItem("Open Recent", _recentMenu);
        _fileMenu.AddSeparator();
        _fileMenu.AddItem("Re-analyze", FileReanalyze);
        _fileMenu.AddItem("Take Snapshot (to compare later)", FileSnapshot);
        SetShortcut(_fileMenu, FileReanalyze, Key.R);
        _fileMenu.AddSeparator();
        _fileMenu.AddItem("Quit", FileQuit);
        _fileMenu.IdPressed += id => OnMenu((int)id);
        menus.AddChild(_fileMenu);
        SetRecent(Array.Empty<string>());

        _viewMenu = new PopupMenu { Name = "View" };
        _colorMenu = new PopupMenu { Name = "ColorBy" };
        foreach (var (mode, label) in ColorModes) _colorMenu.AddRadioCheckItem(label, (int)mode);
        _colorMenu.IdPressed += id => ColorModeChanged?.Invoke((ColorMode)(int)id);
        _viewMenu.AddSubmenuNodeItem("Color By", _colorMenu);
        _depthMenu = new PopupMenu { Name = "LinkDepth" };
        _depthMenu.AddRadioCheckItem("1 hop (direct links)", 1);
        _depthMenu.AddRadioCheckItem("2 hops", 2);
        _depthMenu.AddRadioCheckItem("3 hops", 3);
        _depthMenu.IdPressed += id =>
        {
            SetLinkDepth((int)id);
            LinkDepthChanged?.Invoke((int)id);
        };
        _viewMenu.AddSubmenuNodeItem("Link Depth", _depthMenu);
        _viewMenu.AddSeparator();
        _viewMenu.AddItem("Frame All", ViewFrameAll);
        SetShortcut(_viewMenu, ViewFrameAll, Key.F, command: false);
        _viewMenu.AddItem("Reset Layout", ViewResetLayout);
        _viewMenu.AddSeparator();
        _fadeMenu = new PopupMenu { Name = "QuietFade" };
        for (var i = 0; i < QuietFades.Length; i++) _fadeMenu.AddRadioCheckItem($"{QuietFades[i] * 100:0}%", i);
        _fadeMenu.IdPressed += id =>
        {
            SetQuietFade(QuietFades[(int)id]);
            QuietFadeChanged?.Invoke(QuietFades[(int)id]);
        };
        _viewMenu.AddSubmenuNodeItem("Quiet Fade", _fadeMenu);
        _viewMenu.AddItem("Show Everything (clear hide/quiet)", ViewShowAll);
        _labelsMenu = new PopupMenu { Name = "Labels" };
        _labelsMenu.AddRadioCheckItem("All", (int)LabelMode.All);
        _labelsMenu.AddRadioCheckItem("Near Mouse", (int)LabelMode.NearMouse);
        _labelsMenu.AddRadioCheckItem("On Click", (int)LabelMode.OnClick);
        _labelsMenu.IdPressed += id => LabelModeChanged?.Invoke((LabelMode)(int)id);
        _viewMenu.AddSubmenuNodeItem("Labels (L)", _labelsMenu);
        _brightnessMenu = new PopupMenu { Name = "Brightness" };
        for (var i = 0; i < Brightnesses.Length; i++) _brightnessMenu.AddRadioCheckItem($"{Brightnesses[i] * 100:0}%", i);
        _brightnessMenu.IdPressed += id =>
        {
            SetBrightness(Brightnesses[(int)id]);
            BrightnessChanged?.Invoke(Brightnesses[(int)id]);
        };
        _viewMenu.AddSubmenuNodeItem("Brightness", _brightnessMenu);
        _viewMenu.AddSeparator();
        _compareMenu = new PopupMenu { Name = "CompareWith" };
        _compareMenu.IdPressed += id => OnCompare((int)id);
        _viewMenu.AddSubmenuNodeItem("Compare With Snapshot", _compareMenu);
        _viewMenu.AddItem("Stop Comparing", ViewStopCompare);
        _viewMenu.AddSeparator();
        _viewMenu.AddCheckItem("Filter by Usage (used-by count)", ViewUsageFilter);
        _viewMenu.AddSeparator();
        _viewMenu.AddCheckItem("Outline", ViewOutline);
        _viewMenu.AddCheckItem("Methods", ViewMethods);
        _viewMenu.AddCheckItem("Inspector", ViewInspector);
        _viewMenu.AddCheckItem("Legend", ViewLegend);
        _viewMenu.IdPressed += id => OnMenu((int)id);
        menus.AddChild(_viewMenu);

        var navigate = new PopupMenu { Name = "Navigate" };
        navigate.AddItem("Find…", NavigateFind);
        SetShortcut(navigate, NavigateFind, Key.F);
        navigate.AddItem("Back", NavigateBack);
        SetShortcut(navigate, NavigateBack, Key.Escape, command: false);
        navigate.AddItem("Clear Selection", NavigateClear);
        navigate.IdPressed += id => OnMenu((int)id);
        menus.AddChild(navigate);

        _simulateMenu = new PopupMenu { Name = "Simulate" };
        _simulateMenu.AddItem("Flow Downstream from Selection", SimDownstream);
        SetShortcut(_simulateMenu, SimDownstream, Key.D, command: false);
        _simulateMenu.AddItem("Flow Upstream into Selection", SimUpstream);
        SetShortcut(_simulateMenu, SimUpstream, Key.U, command: false);
        _simulateMenu.AddSeparator();
        _simulateMenu.AddItem("Play Game Loop (frame, physics, draw)", SimGameLoop);
        SetShortcut(_simulateMenu, SimGameLoop, Key.G, command: false);
        _triggerMenu = new PopupMenu { Name = "Triggers" };
        _triggerMenu.IdPressed += id => OnTrigger((int)id);
        _simulateMenu.AddSubmenuNodeItem("Play Trigger", _triggerMenu);
        _simulateMenu.AddSeparator();
        _simulateMenu.AddItem("Pause", SimPause);
        SetShortcut(_simulateMenu, SimPause, Key.Space, command: false);
        _simulateMenu.AddItem("Stop", SimStop);
        _simulateMenu.AddSeparator();
        _speedMenu = new PopupMenu { Name = "Speed" };
        for (var i = 0; i < Speeds.Length; i++) _speedMenu.AddRadioCheckItem($"{Speeds[i]:0.##}×", i);
        _speedMenu.IdPressed += id =>
        {
            SetSpeed(Speeds[(int)id]);
            SpeedChanged?.Invoke(Speeds[(int)id]);
        };
        _simulateMenu.AddSubmenuNodeItem("Speed", _speedMenu);
        _simulateMenu.AddCheckItem("Include Data Reads/Writes", SimData);
        _untouchedMenu = new PopupMenu { Name = "Untouched" };
        _untouchedMenu.AddRadioCheckItem("Show (draw everything)", (int)UntouchedMode.Show);
        _untouchedMenu.AddRadioCheckItem("Ghost (fade what the flow hasn't reached)", (int)UntouchedMode.Ghost);
        _untouchedMenu.AddRadioCheckItem("Hide (only draw what the flow has reached)", (int)UntouchedMode.Hide);
        _untouchedMenu.IdPressed += id => UntouchedModeChanged?.Invoke((UntouchedMode)(int)id);
        _simulateMenu.AddSubmenuNodeItem("Untouched (H)", _untouchedMenu);
        _simulateMenu.IdPressed += id => OnMenu((int)id);
        menus.AddChild(_simulateMenu);

        var timeline = new PopupMenu { Name = "Timeline" };
        timeline.AddItem("Build Timeline: 10 commits", TimelineBuild10);
        timeline.AddItem("Build Timeline: 25 commits", TimelineBuild25);
        timeline.AddItem("Build Timeline: 50 commits", TimelineBuild50);
        timeline.AddSeparator();
        timeline.AddItem("Show Timeline", TimelineShow);
        timeline.AddItem("Hide Timeline", TimelineHide);
        timeline.IdPressed += id => OnMenu((int)id);
        menus.AddChild(timeline);

        var help = new PopupMenu { Name = "Help" };
        help.AddItem("Controls", HelpControls);
        help.AddItem("About Valence", HelpAbout);
        help.IdPressed += id => OnMenu((int)id);
        menus.AddChild(help);

        row.AddChild(new Control { SizeFlagsHorizontal = SizeFlags.ExpandFill, MouseFilter = MouseFilterEnum.Ignore });
        _repoLabel = new Label { Text = "No graph loaded", VerticalAlignment = VerticalAlignment.Center };
        _repoLabel.AddThemeColorOverride("font_color", Palette.MutedText);
        row.AddChild(_repoLabel);
        return bar;
    }

    private void BuildInspector(bool visible)
    {
        _inspector = new PanelContainer { SizeFlagsVertical = SizeFlags.ExpandFill, Visible = visible };
        _inspector.AddThemeStyleboxOverride("panel", PanelStyle(0.92f, 0));
        _inspector.MouseEntered += () => PointerEnteredUi?.Invoke();

        var column = new VBoxContainer();
        column.AddThemeConstantOverride("separation", 6);
        _inspector.AddChild(column);

        var title = new Label { Text = "Inspector" };
        title.AddThemeColorOverride("font_color", Palette.Accent);
        column.AddChild(title);

        _info = new RichTextLabel
        {
            BbcodeEnabled = true,
            ScrollActive = true,
            SelectionEnabled = true,
            SizeFlagsVertical = SizeFlags.ExpandFill,
        };
        _info.AddThemeFontSizeOverride("normal_font_size", 13);
        _info.AddThemeFontSizeOverride("bold_font_size", 13);
        _info.MetaClicked += meta => LinkClicked?.Invoke(meta.AsString());
        column.AddChild(_info);
    }

    private void BuildLegend(Control parent, bool visible)
    {
        _legend = MakePanel();
        _legend.AnchorTop = 1f;
        _legend.AnchorBottom = 1f;
        _legend.OffsetLeft = 12f;
        _legend.OffsetTop = -12f;
        _legend.OffsetBottom = -12f;
        _legend.GrowVertical = GrowDirection.Begin;
        _legend.CustomMinimumSize = new Vector2(400f, 0f);
        _legend.Visible = visible;
        _legend.MouseEntered += () => PointerEnteredUi?.Invoke();
        parent.AddChild(_legend);

        var column = new VBoxContainer();
        column.AddThemeConstantOverride("separation", 4);
        _legend.AddChild(column);

        column.AddChild(Rich(
            "[b]Bonds[/b] between systems; thickness = number of dependencies\n" +
            $"[color={Palette.Html(Palette.Ionic)}]━━[/color] one-way: pulses flow from dependent to dependency\n" +
            $"[color={Palette.Html(Palette.Covalent)}]━━[/color] covalent: both depend on each other (cycle)\n" +
            "Height = dependency layer. Click a system to open it; its rim ports lead to connected systems."));

        column.AddChild(Rich("[b]Nucleons[/b] (classes)"));
        _legendNucleons = Rich("");
        column.AddChild(_legendNucleons);

        column.AddChild(Rich("[b]Class links[/b] shown in an opened system"));
        var kinds = new HFlowContainer();
        kinds.AddThemeConstantOverride("h_separation", 6);
        foreach (var kind in Palette.EdgeKindOrder)
        {
            var toggle = new CheckBox { Text = kind, ButtonPressed = true };
            toggle.AddThemeColorOverride("font_color", Palette.EdgeKind(kind));
            toggle.AddThemeColorOverride("font_pressed_color", Palette.EdgeKind(kind));
            toggle.AddThemeColorOverride("font_hover_color", Palette.EdgeKind(kind).Lightened(0.3f));
            toggle.AddThemeFontSizeOverride("font_size", 12);
            toggle.Toggled += on => EdgeKindToggled?.Invoke(kind, on);
            kinds.AddChild(toggle);
        }
        column.AddChild(kinds);
    }

    private Control BuildStatusBar()
    {
        var bar = new PanelContainer();
        bar.AddThemeStyleboxOverride("panel", PanelStyle(0.96f, 0, 4f, borderTop: 2));
        bar.MouseEntered += () => PointerEnteredUi?.Invoke();

        var row = new HBoxContainer();
        row.AddThemeConstantOverride("separation", 16);
        bar.AddChild(row);

        _status = new Label { Text = "", ClipText = true, SizeFlagsHorizontal = SizeFlags.ExpandFill };
        _status.AddThemeFontSizeOverride("font_size", 12);
        row.AddChild(_status);

        _selectionLabel = new Label { Text = "" };
        _selectionLabel.AddThemeFontSizeOverride("font_size", 12);
        _selectionLabel.AddThemeColorOverride("font_color", Palette.Accent);
        row.AddChild(_selectionLabel);

        var hint = new Label { Text = "Drag orbit · Right-drag pan · Scroll zoom · Click open/select · Shift-drag move · Esc back" };
        hint.AddThemeFontSizeOverride("font_size", 12);
        hint.AddThemeColorOverride("font_color", Palette.MutedText);
        row.AddChild(hint);
        return bar;
    }

    private void BuildTooltip()
    {
        _tooltip = new PanelContainer { MouseFilter = MouseFilterEnum.Ignore, Visible = false, TopLevel = true, ZIndex = 10 };
        _tooltip.AddThemeStyleboxOverride("panel", PanelStyle(0.95f, 6, 6f, 1, 1, 1, 1));
        _tooltipLabel = new Label { MouseFilter = MouseFilterEnum.Ignore };
        _tooltipLabel.AddThemeFontSizeOverride("font_size", 13);
        _tooltip.AddChild(_tooltipLabel);
        AddChild(_tooltip);
    }

    private void BuildDialogs()
    {
        _graphDialog = new FileDialog
        {
            Title = "Open a Valence graph",
            FileMode = FileDialog.FileModeEnum.OpenFile,
            Access = FileDialog.AccessEnum.Filesystem,
            UseNativeDialog = true,
            Filters = new[] { "*.json ; Valence graph" },
        };
        _graphDialog.FileSelected += path => GraphFileChosen?.Invoke(path);
        AddChild(_graphDialog);

        _sourceDialog = new FileDialog
        {
            Title = "Open a source folder (it is only read, never changed)",
            FileMode = FileDialog.FileModeEnum.OpenDir,
            Access = FileDialog.AccessEnum.Filesystem,
            UseNativeDialog = true,
        };
        _sourceDialog.DirSelected += dir => SourceFolderChosen?.Invoke(dir);
        AddChild(_sourceDialog);

        // Controls: key and mouse reference, in titled groups.
        _helpDialog = new ModalDialog();
        _helpDialog.Build("Controls", 680f);
        AddHelpGroup(_helpDialog, "Camera",
            ("Drag", "orbit"),
            ("Right-drag · Shift+two-finger swipe", "pan"),
            ("Scroll · pinch", "zoom"),
            ("F", "frame everything"),
            ("Esc", "back out one level"));
        AddHelpGroup(_helpDialog, "Exploring",
            ("Click a system", "open it: classes spread out, ports lead to other systems"),
            ("Click a class · ⌘F", "select it and label everything connected (View ▸ Link Depth for more hops)"),
            ("Click a port", "jump to that system"),
            ("Shift-drag a system", "move it (saved per repo; View ▸ Reset Layout undoes)"),
            ("L", "labels: all / near mouse / on click"),
            ("⌘O · ⌘R", "open a source folder · re-analyze"));
        AddHelpGroup(_helpDialog, "Simulate (static: nothing is run)",
            ("D · U", "flow downstream from / upstream into the selected class"),
            ("G", "play the game loop (every _Process/_PhysicsProcess, repeating)"),
            ("Simulate ▸ Play Trigger", "startup, input, event handlers, requests…"),
            ("Space", "pause, resume or replay"),
            ("H", "untouched classes: show / ghost / hide"),
            ("Esc", "stop"));
        _helpDialog.AddText("Drag a side panel's border to resize it.", fontSize: 12)
            .AddThemeColorOverride("font_color", Palette.MutedText);
        AddChild(_helpDialog);

        // About: the logo above the description, at a fixed size.
        _aboutDialog = new ModalDialog();
        _aboutDialog.Build("About Valence", 480f);
        if (ResourceLoader.Exists(IconPath))
        {
            // The app icon: its own rounded light body and transparent corners sit well on the dark dialog.
            _aboutDialog.Body.AddChild(new TextureRect
            {
                Texture = GD.Load<Texture2D>(IconPath),
                ExpandMode = TextureRect.ExpandModeEnum.IgnoreSize,
                StretchMode = TextureRect.StretchModeEnum.KeepAspectCentered,
                CustomMinimumSize = new Vector2(240f, 240f),
                SizeFlagsHorizontal = SizeFlags.ShrinkCenter,
            });
        }
        _aboutDialog.AddText(
            "Valence draws a codebase as chemistry: projects are molecules, systems are atoms, " +
            "classes are nucleons and dependencies are bonds.",
            HorizontalAlignment.Center);
        _aboutDialog.AddText(
                "It only reads the folder you point it at. Nothing is built, run or written there.",
                HorizontalAlignment.Center, 13)
            .AddThemeColorOverride("font_color", Palette.MutedText);
        AddChild(_aboutDialog);
    }

    /// <summary>A titled group of "keys · action" rows in a two-column grid.</summary>
    private static void AddHelpGroup(ModalDialog dialog, string title, params (string Keys, string Action)[] rows)
    {
        var heading = new Label { Text = title };
        heading.AddThemeColorOverride("font_color", Palette.Accent);
        dialog.Body.AddChild(heading);

        const float keyWidth = 240f;
        const float gap = 16f;
        var grid = new GridContainer { Columns = 2 };
        grid.AddThemeConstantOverride("h_separation", (int)gap);
        grid.AddThemeConstantOverride("v_separation", 4);
        foreach (var (keys, action) in rows)
        {
            var key = new Label { Text = keys, CustomMinimumSize = new Vector2(keyWidth, 0f) };
            key.AddThemeColorOverride("font_color", Palette.Gold);
            key.AddThemeFontSizeOverride("font_size", 13);
            grid.AddChild(key);

            var text = new Label
            {
                Text = action,
                AutowrapMode = TextServer.AutowrapMode.WordSmart,
                CustomMinimumSize = new Vector2(dialog.ContentWidth - keyWidth - gap, 0f),
            };
            text.AddThemeFontSizeOverride("font_size", 13);
            grid.AddChild(text);
        }
        dialog.Body.AddChild(grid);
    }

    /// <summary>The logo as a macOS-style icon (tools/make_icon.py); logo.png stays the flat art for the splash.</summary>
    private const string IconPath = "res://images/icon.png";

    // ---------------------------------------------------------------- menu handling

    private void OnMenu(int id)
    {
        switch (id)
        {
            case FileOpenSource:
                PopupOpenSource();
                break;
            case FileOpenGraph:
                PopupOpenGraph();
                break;
            case FileSnapshot:
                SnapshotRequested?.Invoke();
                break;
            case ViewStopCompare:
                StopCompareRequested?.Invoke();
                break;
            case ViewUsageFilter:
                SetUsageFilterVisible(!(_filterBar?.Visible ?? false));
                break;
            case TimelineBuild10:
                TimelineBuildRequested?.Invoke(10);
                break;
            case TimelineBuild25:
                TimelineBuildRequested?.Invoke(25);
                break;
            case TimelineBuild50:
                TimelineBuildRequested?.Invoke(50);
                break;
            case TimelineShow:
                TimelineShowRequested?.Invoke();
                break;
            case TimelineHide:
                TimelineHideRequested?.Invoke();
                break;
            case FileReanalyze:
                ReanalyzeRequested?.Invoke();
                break;
            case FileQuit:
                QuitRequested?.Invoke();
                break;
            case ViewFrameAll:
                FrameAllRequested?.Invoke();
                break;
            case ViewResetLayout:
                ResetLayoutRequested?.Invoke();
                break;
            case ViewShowAll:
                ShowAllRequested?.Invoke();
                break;
            case ViewOutline:
            case ViewMethods:
            case ViewInspector:
            case ViewLegend:
                TogglePanel(id);
                break;
            case NavigateFind:
                FocusSearch();
                break;
            case NavigateBack:
                BackRequested?.Invoke();
                break;
            case NavigateClear:
                ClearSelectionRequested?.Invoke();
                break;
            case SimDownstream:
                FlowRequested?.Invoke(FlowDirection.Downstream);
                break;
            case SimUpstream:
                FlowRequested?.Invoke(FlowDirection.Upstream);
                break;
            case SimGameLoop:
                GameLoopRequested?.Invoke();
                break;
            case SimPause:
                PauseToggleRequested?.Invoke();
                break;
            case SimStop:
                StopSimulationRequested?.Invoke();
                break;
            case SimData:
            {
                var index = _simulateMenu!.GetItemIndex(SimData);
                var on = !_simulateMenu.IsItemChecked(index);
                _simulateMenu.SetItemChecked(index, on);
                IncludeDataChanged?.Invoke(on);
                break;
            }
            case HelpControls:
                _helpDialog?.Open();
                break;
            case HelpAbout:
                _aboutDialog?.Open();
                break;
        }
    }

    private void OnTrigger(int id)
    {
        var index = id - TriggerBase;
        if (index >= 0 && index < _triggers.Count) TriggerRequested?.Invoke(_triggers[index]);
    }

    private void OnRecent(int id)
    {
        var index = id - RecentBase;
        if (index >= 0 && index < _recentFolders.Count) RecentFolderChosen?.Invoke(_recentFolders[index]);
    }

    private void TogglePanel(int id)
    {
        Control? panel = id switch
        {
            ViewOutline => Outline,
            ViewMethods => Methods,
            ViewInspector => _inspector,
            ViewLegend => _legend,
            _ => null,
        };
        if (panel == null) return;
        panel.Visible = !panel.Visible;
        SetChecked(_viewMenu, id, panel.Visible);
        UpdateRightColumn();
        PanelsChanged?.Invoke(Outline.Visible, Methods.Visible, _inspector?.Visible ?? false, _legend?.Visible ?? false);
    }

    /// <summary>The right column takes no space when both of its panels are hidden; borders follow their panels.</summary>
    private void UpdateRightColumn()
    {
        if (_rightColumn != null) _rightColumn.Visible = Methods.Visible || (_inspector?.Visible ?? false);
        if (_rightResizer != null) _rightResizer.Visible = _rightColumn?.Visible ?? false;
        if (_outlineResizer != null) _outlineResizer.Visible = Outline.Visible;
    }

    private static void ResizePanel(Control? panel, float delta)
    {
        if (panel == null) return;
        var width = Mathf.Clamp(panel.CustomMinimumSize.X + delta, MinPanelWidth, MaxPanelWidth);
        panel.CustomMinimumSize = new Vector2(width, panel.CustomMinimumSize.Y);
    }

    private void RaiseWidthsChanged() =>
        PanelWidthsChanged?.Invoke(Outline.CustomMinimumSize.X, _rightColumn?.CustomMinimumSize.X ?? 400f);

    // ---------------------------------------------------------------- helpers

    private static void SetChecked(PopupMenu? menu, int id, bool value)
    {
        if (menu == null) return;
        var index = menu.GetItemIndex(id);
        if (index >= 0) menu.SetItemChecked(index, value);
    }

    private static void SetDisabled(PopupMenu? menu, int id, bool value)
    {
        if (menu == null) return;
        var index = menu.GetItemIndex(id);
        if (index >= 0) menu.SetItemDisabled(index, value);
    }

    /// <summary>Shows the shortcut next to the item. Main handles the key itself when no menu is open.</summary>
    private static void SetShortcut(PopupMenu menu, int id, Key key, bool shift = false, bool command = true)
    {
        var index = menu.GetItemIndex(id);
        if (index < 0) return;
        var keyEvent = new InputEventKey { Keycode = key, ShiftPressed = shift, CommandOrControlAutoremap = command };
        var shortcut = new Shortcut { Events = new Godot.Collections.Array { keyEvent } };
        menu.SetItemShortcut(index, shortcut, false); // display only; not a global shortcut
    }

    private static void PopupDialog(FileDialog? dialog, string? directory)
    {
        if (dialog == null) return;
        if (directory != null && Directory.Exists(directory)) dialog.CurrentDir = directory;
        dialog.PopupCentered(new Vector2I(900, 600));
    }

    private static string DefaultGraphDir() => Path.Combine(
        System.Environment.GetFolderPath(System.Environment.SpecialFolder.UserProfile), ".valence", "graphs");

    private static StyleBoxFlat PanelStyle(
        float alpha, int corner, float margin = 10f,
        int borderLeft = 0, int borderRight = 0, int borderTop = 0, int borderBottom = 0)
    {
        var style = new StyleBoxFlat
        {
            BgColor = new Color(0.06f, 0.08f, 0.12f, alpha),
            BorderColor = Palette.PanelBorder,
            BorderWidthLeft = borderLeft,
            BorderWidthRight = borderRight,
            BorderWidthTop = borderTop,
            BorderWidthBottom = borderBottom,
        };
        style.SetCornerRadiusAll(corner);
        style.SetContentMarginAll(margin);
        return style;
    }

    private static RichTextLabel Rich(string bbcode)
    {
        var label = new RichTextLabel
        {
            BbcodeEnabled = true,
            FitContent = true,
            ScrollActive = false,
            AutowrapMode = TextServer.AutowrapMode.WordSmart,
            Text = bbcode,
            MouseFilter = MouseFilterEnum.Ignore,
        };
        label.AddThemeFontSizeOverride("normal_font_size", 12);
        label.AddThemeFontSizeOverride("bold_font_size", 12);
        return label;
    }
}
