using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Godot;

namespace Valence.Viewer;

/// <summary>What a left-drag in the 3D view does (the tool buttons in its corner).</summary>
public enum ViewTool
{
    /// <summary>Click selects, drag orbits (the default).</summary>
    Select,

    /// <summary>Drag orbits; clicks don't select.</summary>
    Orbit,

    /// <summary>Drag pans.</summary>
    Pan,

    /// <summary>Drag up/down zooms.</summary>
    Zoom,
}

/// <summary>
/// Screen-space UI: the title bar (logo, menus, repo pills), the Outline (left), the toolbar and the
/// framed 3D view with its legend, axis gizmo and view tools (center), entry points / methods and
/// the Inspector (right), the status bar, tooltips, dialogs and the analysis progress dialog.
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
    private const int ViewOnlyDifferences = 29;
    private const int ViewFullscreen = 30;
    private const int FileSnapshot = 4;
    private const int FileSnapshots = 5;
    private const int TimelineBuild10 = 110;
    private const int TimelineBuild25 = 111;
    private const int TimelineBuild50 = 112;
    private const int TimelineShow = 113;
    private const int TimelineHide = 114;
    private const int CompareBase = 2000;
    private const int CompareManage = CompareBase - 2;
    private const int InspectorSummary = 300;
    private const int InspectorCopyPath = 301;
    private const int InspectorReveal = 302;
    private const int ToolZoomIn = 310;
    private const int ToolZoomOut = 311;

    // The toolbar's lenses: each colors classes by a different question (same as View ▸ Color By).
    private static readonly (ColorMode Mode, string Icon, string Text, string Tooltip)[] Lenses =
    {
        (ColorMode.Visibility, "graph", "Structure", "Public vs internal classes (protons and neutrons)"),
        (ColorMode.Churn, "branch", "Changes", "Classes changed recently glow hot (git churn)"),
        (ColorMode.Coupling, "layers", "Dependencies", "Classes many others depend on glow hot"),
    };

    private static readonly (ViewTool Tool, string Icon, string Tooltip)[] Tools =
    {
        (ViewTool.Select, "pointer", "Select: click to select, drag to orbit"),
        (ViewTool.Orbit, "orbit", "Orbit: drag to turn the view (clicks don't select)"),
        (ViewTool.Pan, "move", "Pan: drag to move the view"),
        (ViewTool.Zoom, "zoom-in", "Zoom: drag up or down to zoom"),
    };

    private static readonly (string Key, string Icon, string Caption)[] SummaryCards =
    {
        ("projects", "cube", "Projects"),
        ("systems", "layers", "Systems"),
        ("classes", "file", "Classes"),
        ("bonds", "link", "Bonds"),
        ("cycles", "cycle", "Cycles"),
        ("members", "users", "Members"),
        ("links", "graph", "member links"),
    };

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
    private PanelContainer? _repoPill;
    private PanelContainer? _branchPill;
    private PanelContainer? _commitPill;
    private Label? _repoName;
    private Label? _branchName;
    private Label? _commitName;
    private string _repoRoot = "";
    private PanelContainer? _outlineGutter;
    private PanelContainer? _rightGutter;
    private PanelContainer? _filterGutter;
    private PanelContainer? _timelineGutter;
    private PopupMenu? _filtersMenu;
    private readonly Dictionary<ColorMode, Button> _tabs = new();
    private VBoxContainer? _summary;
    private Label? _summaryName;
    private Label? _summaryPath;
    private HBoxContainer? _summaryBranchRow;
    private Label? _summaryBranch;
    private readonly Dictionary<string, Label> _cards = new(StringComparer.Ordinal);
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

    /// <summary>X/Y/Z in the 3D view's corner; Main gives it the camera.</summary>
    public AxisGizmo Gizmo { get; } = new();

    public event Action<ViewTool>? ToolChanged;

    /// <summary>Zoom by this factor (&lt;1 = in), from the view's ▾ menu.</summary>
    public event Action<float>? ZoomStepRequested;

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

    /// <summary>File ▸ Snapshots… or Compare With Snapshot ▸ Manage Snapshots….</summary>
    public event Action? ManageSnapshotsRequested;

    /// <summary>View ▸ Full Screen (F11, ⌃⌘F).</summary>
    public event Action? FullscreenToggled;

    /// <summary>View ▸ Only Show Differences toggled (only enabled while comparing).</summary>
    public event Action<bool>? OnlyDifferencesChanged;

    /// <summary>The snapshot list with Compare/Rename/Delete; Main fills it and handles its events.</summary>
    public SnapshotsDialog Snapshots { get; } = new();
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
        // Menus, popups and dialogs below inherit the theme; icons shrink smoothly through mipmaps.
        Theme = ValenceTheme.Build();
        TextureFilter = TextureFilterEnum.LinearWithMipmaps;

        var root = new VBoxContainer { MouseFilter = MouseFilterEnum.Ignore };
        root.SetAnchorsPreset(LayoutPreset.FullRect);
        root.AddThemeConstantOverride("separation", 0);
        AddChild(root);

        root.AddChild(BuildMenuBar());

        var middle = new HBoxContainer { MouseFilter = MouseFilterEnum.Ignore, SizeFlagsVertical = SizeFlags.ExpandFill };
        middle.AddThemeConstantOverride("separation", 0);
        root.AddChild(middle);

        // Left: the Outline, inside a gutter of app background.
        Outline.AddThemeStyleboxOverride("panel", UiAssets.Box(Patch.Panel, 12f));
        Outline.CustomMinimumSize = new Vector2(Mathf.Clamp(outlineWidth, MinPanelWidth, MaxPanelWidth), 0f);
        Outline.Build();
        Outline.ItemChosen += meta => LinkClicked?.Invoke(meta);
        Outline.MouseEntered += () => PointerEnteredUi?.Invoke();
        Outline.Visible = showOutline;
        _outlineGutter = UiKit.Gutter(Outline, 8f, 8f, 0f, 8f);
        middle.AddChild(_outlineGutter);

        // The gap right of the Outline: drag to resize.
        _outlineResizer = new PanelResizer();
        _outlineResizer.Dragged += dx => ResizePanel(Outline, dx);
        _outlineResizer.DragFinished += RaiseWidthsChanged;
        _outlineResizer.MouseEntered += () => PointerEnteredUi?.Invoke();
        middle.AddChild(_outlineResizer);

        // Center: the toolbar, the framed 3D view (it passes all input to the scene), then the bars.
        var center = new VBoxContainer
        {
            MouseFilter = MouseFilterEnum.Ignore,
            SizeFlagsHorizontal = SizeFlags.ExpandFill,
            SizeFlagsVertical = SizeFlags.ExpandFill,
        };
        center.AddThemeConstantOverride("separation", 0);
        middle.AddChild(center);
        center.AddChild(BuildToolbar());
        center.AddChild(BuildViewport(showLegend));
        _filterGutter = UiKit.Gutter(BuildFilterBar(), 0f, 8f, 0f, 0f);
        _filterGutter.Visible = false;
        center.AddChild(_filterGutter);
        _timelineGutter = UiKit.Gutter(BuildTimelineBar(), 0f, 8f, 0f, 0f);
        _timelineGutter.Visible = false;
        center.AddChild(_timelineGutter);
        center.AddChild(new ColorRect
        {
            Color = UiKit.AppBackground,
            CustomMinimumSize = new Vector2(0f, 8f),
            MouseFilter = MouseFilterEnum.Ignore,
        });

        // The gap left of the right column: drag to resize (dragging left widens it).
        _rightResizer = new PanelResizer();
        _rightResizer.Dragged += dx => ResizePanel(_rightColumn, -dx);
        _rightResizer.DragFinished += RaiseWidthsChanged;
        _rightResizer.MouseEntered += () => PointerEnteredUi?.Invoke();
        middle.AddChild(_rightResizer);

        // Right: entry points / methods (with ▶ simulate buttons) above the Inspector.
        _rightColumn = new VBoxContainer
        {
            CustomMinimumSize = new Vector2(Mathf.Clamp(rightWidth, MinPanelWidth, MaxPanelWidth), 0f),
            MouseFilter = MouseFilterEnum.Ignore,
        };
        _rightColumn.AddThemeConstantOverride("separation", 8);

        Methods.AddThemeStyleboxOverride("panel", UiAssets.Box(Patch.Panel, 12f));
        Methods.SizeFlagsVertical = SizeFlags.ExpandFill;
        Methods.SizeFlagsStretchRatio = 0.8f;
        Methods.Build();
        Methods.MouseEntered += () => PointerEnteredUi?.Invoke();
        Methods.Visible = showMethods;
        _rightColumn.AddChild(Methods);

        BuildInspector(showInspector);
        _rightColumn.AddChild(_inspector!);
        _rightGutter = UiKit.Gutter(_rightColumn, 0f, 8f, 8f, 8f);
        middle.AddChild(_rightGutter);
        UpdateRightColumn();

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

    /// <summary>The title bar's repo, branch and commit pills.</summary>
    public void SetRepoInfo(string name, string root, string? branch, string? commit)
    {
        _repoRoot = root;
        if (_repoName != null) _repoName.Text = name;
        if (_repoPill != null) _repoPill.TooltipText = root;
        if (_branchName != null) _branchName.Text = branch ?? "";
        if (_branchPill != null) _branchPill.Visible = !string.IsNullOrEmpty(branch);
        var hasCommit = !string.IsNullOrEmpty(commit);
        if (_commitName != null) _commitName.Text = hasCommit ? commit![..Math.Min(8, commit.Length)] : "";
        if (_commitPill != null)
        {
            _commitPill.Visible = hasCommit;
            _commitPill.TooltipText = commit ?? "";
        }
    }

    public void SetStatus(string text)
    {
        if (_status != null) _status.Text = text;
    }

    public void SetSelection(string text)
    {
        if (_selectionLabel != null) _selectionLabel.Text = text;
    }

    /// <summary>Selection, simulation or comparison details in the Inspector.</summary>
    public void ShowInfo(string bbcode)
    {
        if (_info == null) return;
        if (_summary != null) _summary.Visible = false;
        _info.Visible = true;
        _info.Text = bbcode;
        _info.ScrollToLine(0);
    }

    /// <summary>The Inspector with nothing selected: the repo and its stat cards.</summary>
    public void ShowRepoSummary(GraphModel model)
    {
        if (_summary == null || _info == null) return;
        var repo = model.Graph.Repo;
        if (_summaryName != null) _summaryName.Text = repo.Name;
        if (_summaryPath != null)
        {
            _summaryPath.Text = repo.Root;
            _summaryPath.TooltipText = repo.Root;
        }
        var commit = string.IsNullOrEmpty(repo.Commit) ? "" : "  @ " + repo.Commit[..Math.Min(10, repo.Commit.Length)];
        if (_summaryBranch != null) _summaryBranch.Text = (repo.Branch ?? "") + commit;
        if (_summaryBranchRow != null) _summaryBranchRow.Visible = commit.Length > 0 || !string.IsNullOrEmpty(repo.Branch);

        SetCard("projects", model.Molecules.Count);
        SetCard("systems", model.AtomList.Count);
        SetCard("classes", model.Nodes.Count);
        SetCard("bonds", model.Bonds.Count);
        SetCard("cycles", model.Bonds.Count(b => b.Covalent));
        SetCard("members", model.Nodes.Values.Sum(n => n.Info.Members.Count));
        SetCard("links", model.Graph.MemberEdges.Count);

        _info.Visible = false;
        _summary.Visible = true;
    }

    private void SetCard(string key, int value)
    {
        if (_cards.TryGetValue(key, out var label)) label.Text = value.ToString("N0");
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
        _compareMenu.AddSeparator();
        _compareMenu.AddItem("Manage Snapshots…", CompareManage);
    }

    /// <summary>Stop Comparing and Only Show Differences only make sense while comparing.</summary>
    public void SetFullscreen(bool on) => SetChecked(_viewMenu, ViewFullscreen, on);

    public void SetCompareActive(bool active)
    {
        SetDisabled(_viewMenu, ViewStopCompare, !active);
        SetDisabled(_viewMenu, ViewOnlyDifferences, !active);
        SetDisabled(_filtersMenu, ViewOnlyDifferences, !active);
    }

    public void ShowTimeline(int frames)
    {
        if (_timelineGutter == null || _timelineSlider == null) return;
        _suppressTimelineSlider = true;
        _timelineSlider.MaxValue = Math.Max(0, frames - 1);
        _suppressTimelineSlider = false;
        _timelineGutter.Visible = true;
    }

    public void HideTimeline()
    {
        if (_timelineGutter != null) _timelineGutter.Visible = false;
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
        if (_timelinePlay != null) _timelinePlay.Icon = UiAssets.Icon(playing ? "pause" : "play");
    }

    private void OnCompare(int id)
    {
        if (id == CompareManage)
        {
            ManageSnapshotsRequested?.Invoke();
            return;
        }
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
        if (_filterGutter == null) return;
        _filterGutter.Visible = visible;
        SetChecked(_viewMenu, ViewUsageFilter, visible);
        SetChecked(_filtersMenu, ViewUsageFilter, visible);
        RaiseUsageFilter();
    }

    private Control BuildFilterBar()
    {
        _filterBar = new PanelContainer();
        _filterBar.AddThemeStyleboxOverride("panel", UiAssets.Box(Patch.Panel, 14f, 6f));
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

        row.AddChild(UiKit.IconButton("close", "Turn the filter off", () => SetUsageFilterVisible(false), 28f, flat: true));
        return _filterBar;
    }

    private void RaiseUsageFilter()
    {
        if (_filterRange == null) return;
        if (_filterValue != null) _filterValue.Text = $"{_filterRange.Low} – {_filterRange.High}";
        UsageFilterChanged?.Invoke(_filterGutter?.Visible ?? false, _filterRange.Low, _filterRange.High, _filterMode?.Selected == 1);
    }

    private Control BuildTimelineBar()
    {
        _timelineBar = new PanelContainer();
        _timelineBar.AddThemeStyleboxOverride("panel", UiAssets.Box(Patch.Panel, 14f, 6f));
        _timelineBar.MouseEntered += () => PointerEnteredUi?.Invoke();

        var row = new HBoxContainer();
        row.AddThemeConstantOverride("separation", 10);
        _timelineBar.AddChild(row);

        var title = new Label { Text = "Timeline", VerticalAlignment = VerticalAlignment.Center };
        title.AddThemeColorOverride("font_color", Palette.Accent);
        row.AddChild(title);

        _timelinePlay = UiKit.IconButton("play", "Play through history", () => TimelinePlayToggled?.Invoke(), 30f, flat: true);
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

        row.AddChild(UiKit.IconButton("close", "Hide the timeline (back to the current code)", () => TimelineHideRequested?.Invoke(), 28f, flat: true));
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
        foreach (var (each, tab) in _tabs) tab.SetPressedNoSignal(each == mode);
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

    /// <summary>A free-standing panel (legend, progress dialog) in one of the nine-patch styles.</summary>
    public static PanelContainer MakePanel(Patch patch = Patch.Panel, float content = 12f)
    {
        var panel = new PanelContainer();
        panel.AddThemeStyleboxOverride("panel", UiAssets.Box(patch, content));
        return panel;
    }

    // ---------------------------------------------------------------- construction

    private Control BuildMenuBar()
    {
        var bar = new PanelContainer();
        bar.AddThemeStyleboxOverride("panel", UiKit.Strip(14f, 6f, lineBottom: true));
        bar.MouseEntered += () => PointerEnteredUi?.Invoke();

        var row = new HBoxContainer();
        row.AddThemeConstantOverride("separation", 12);
        bar.AddChild(row);

        row.AddChild(UiKit.IconRect("logo", 28f));
        var wordmark = new Label { Text = "VALENCE", VerticalAlignment = VerticalAlignment.Center };
        wordmark.AddThemeFontOverride("font", UiKit.WordmarkFont(6));
        wordmark.AddThemeFontSizeOverride("font_size", 19);
        wordmark.AddThemeColorOverride("font_color", Colors.White);
        row.AddChild(wordmark);
        row.AddChild(new VSeparator { CustomMinimumSize = new Vector2(14f, 0f) });

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
        _fileMenu.AddItem("Snapshots…", FileSnapshots);
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
        _viewMenu.AddCheckItem("Only Show Differences", ViewOnlyDifferences);
        _viewMenu.AddSeparator();
        _viewMenu.AddCheckItem("Filter by Usage (used-by count)", ViewUsageFilter);
        _viewMenu.AddSeparator();
        _viewMenu.AddCheckItem("Outline", ViewOutline);
        _viewMenu.AddCheckItem("Methods", ViewMethods);
        _viewMenu.AddCheckItem("Inspector", ViewInspector);
        _viewMenu.AddCheckItem("Legend", ViewLegend);
        _viewMenu.AddSeparator();
        _viewMenu.AddCheckItem("Full Screen", ViewFullscreen);
        SetShortcut(_viewMenu, ViewFullscreen, Key.F11, command: false);
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

        // The pills sit right; on a narrow window they're clipped instead of widening the layout.
        var clip = new ScrollContainer
        {
            HorizontalScrollMode = ScrollContainer.ScrollMode.ShowNever,
            VerticalScrollMode = ScrollContainer.ScrollMode.Disabled,
            SizeFlagsHorizontal = SizeFlags.ExpandFill,
        };
        row.AddChild(clip);
        var pills = new HBoxContainer { Alignment = BoxContainer.AlignmentMode.End, SizeFlagsHorizontal = SizeFlags.ExpandFill };
        pills.AddThemeConstantOverride("separation", 10);
        clip.AddChild(pills);
        _repoPill = UiKit.Pill("database", out var repoName);
        _repoName = repoName;
        _repoName.Text = "No graph loaded";
        pills.AddChild(_repoPill);
        _branchPill = UiKit.Pill("branch", out var branchName);
        _branchName = branchName;
        _branchPill.Visible = false;
        pills.AddChild(_branchPill);
        _commitPill = UiKit.Pill("clock", out var commitName);
        _commitName = commitName;
        _commitPill.Visible = false;
        pills.AddChild(_commitPill);
        return bar;
    }

    /// <summary>Lenses (Structure / Changes / Dependencies), Filters and Fit view, above the 3D view.</summary>
    private Control BuildToolbar()
    {
        var bar = new PanelContainer();
        bar.AddThemeStyleboxOverride("panel", UiKit.Strip(0f, 8f));
        bar.MouseEntered += () => PointerEnteredUi?.Invoke();

        // Clipped rather than widening the window's layout when the center is narrow.
        var clip = new ScrollContainer
        {
            HorizontalScrollMode = ScrollContainer.ScrollMode.ShowNever,
            VerticalScrollMode = ScrollContainer.ScrollMode.Disabled,
        };
        bar.AddChild(clip);
        var row = new HBoxContainer { SizeFlagsHorizontal = SizeFlags.ExpandFill };
        row.AddThemeConstantOverride("separation", 6);
        clip.AddChild(row);

        foreach (var (mode, icon, text, tooltip) in Lenses)
        {
            var tab = UiKit.TextButton(icon, text, tooltip);
            tab.ToggleMode = true;
            tab.Pressed += () => ColorModeChanged?.Invoke(mode);
            _tabs[mode] = tab;
            row.AddChild(tab);
        }

        row.AddChild(new VSeparator { CustomMinimumSize = new Vector2(16f, 0f) });

        var filters = new MenuButton
        {
            Text = "Filters  ▾",
            Icon = UiAssets.Icon("filter"),
            Flat = false,
            TooltipText = "Usage filter, only differences, show everything",
            CustomMinimumSize = new Vector2(0f, 34f),
        };
        _filtersMenu = filters.GetPopup();
        _filtersMenu.AddCheckItem("Filter by Usage (used-by count)", ViewUsageFilter);
        _filtersMenu.AddCheckItem("Only Show Differences", ViewOnlyDifferences);
        _filtersMenu.AddSeparator();
        _filtersMenu.AddItem("Show Everything (clear hide/quiet)", ViewShowAll);
        _filtersMenu.IdPressed += id => OnMenu((int)id);
        SetDisabled(_filtersMenu, ViewOnlyDifferences, true);
        row.AddChild(filters);

        row.AddChild(UiKit.TextButton("focus", "Fit view", "Frame everything (F)", () => FrameAllRequested?.Invoke()));
        return bar;
    }

    /// <summary>
    /// The 3D view's frame (border only: the scene shows through and gets all input), with the legend,
    /// the axis gizmo and the view tools on top.
    /// </summary>
    private Control BuildViewport(bool showLegend)
    {
        var frame = new Control
        {
            MouseFilter = MouseFilterEnum.Ignore,
            SizeFlagsHorizontal = SizeFlags.ExpandFill,
            SizeFlagsVertical = SizeFlags.ExpandFill,
        };
        var border = new Panel { MouseFilter = MouseFilterEnum.Ignore };
        border.SetAnchorsPreset(LayoutPreset.FullRect);
        border.AddThemeStyleboxOverride("panel", UiAssets.Box(Patch.Focus, 0f));
        frame.AddChild(border);

        BuildLegend(frame, showLegend);

        Gizmo.AnchorTop = 1f;
        Gizmo.AnchorBottom = 1f;
        Gizmo.OffsetLeft = 10f;
        Gizmo.OffsetRight = 106f;
        Gizmo.OffsetTop = -106f;
        Gizmo.OffsetBottom = -10f;
        frame.AddChild(Gizmo);

        var tools = new HBoxContainer { MouseFilter = MouseFilterEnum.Ignore };
        tools.AddThemeConstantOverride("separation", 6);
        tools.AnchorLeft = 1f;
        tools.AnchorRight = 1f;
        tools.AnchorTop = 1f;
        tools.AnchorBottom = 1f;
        tools.OffsetLeft = -14f;
        tools.OffsetRight = -14f;
        tools.OffsetTop = -14f;
        tools.OffsetBottom = -14f;
        tools.GrowHorizontal = GrowDirection.Begin;
        tools.GrowVertical = GrowDirection.Begin;
        frame.AddChild(tools);

        var group = new ButtonGroup();
        foreach (var (tool, icon, tooltip) in Tools)
        {
            var button = UiKit.IconButton(icon, tooltip, () => ToolChanged?.Invoke(tool), 36f);
            button.ToggleMode = true;
            button.ButtonGroup = group;
            button.ButtonPressed = tool == ViewTool.Select;
            button.MouseEntered += () => PointerEnteredUi?.Invoke();
            tools.AddChild(button);
        }

        var more = UiKit.IconMenu("chevron-down", "More view actions", 36f);
        more.Flat = false;
        var popup = more.GetPopup();
        popup.AddItem("Zoom In", ToolZoomIn);
        popup.AddItem("Zoom Out", ToolZoomOut);
        popup.AddSeparator();
        popup.AddItem("Fit View", ViewFrameAll);
        popup.AddItem("Reset Layout", ViewResetLayout);
        popup.IdPressed += id =>
        {
            switch ((int)id)
            {
                case ToolZoomIn:
                    ZoomStepRequested?.Invoke(0.8f);
                    break;
                case ToolZoomOut:
                    ZoomStepRequested?.Invoke(1.25f);
                    break;
                default:
                    OnMenu((int)id);
                    break;
            }
        };
        tools.AddChild(more);
        return frame;
    }

    private void BuildInspector(bool visible)
    {
        _inspector = new PanelContainer { SizeFlagsVertical = SizeFlags.ExpandFill, Visible = visible };
        _inspector.AddThemeStyleboxOverride("panel", UiAssets.Box(Patch.Panel, 14f, 12f));
        _inspector.MouseEntered += () => PointerEnteredUi?.Invoke();

        var column = new VBoxContainer();
        column.AddThemeConstantOverride("separation", 10);
        _inspector.AddChild(column);

        var header = UiKit.Header("focus", UiKit.Title("Inspector"));
        header.AddChild(UiKit.Spacer());
        var menu = UiKit.IconMenu("more", "More");
        var popup = menu.GetPopup();
        popup.AddItem("Back to Summary", InspectorSummary);
        popup.AddSeparator();
        popup.AddItem("Copy Repo Path", InspectorCopyPath);
        popup.AddItem("Open Repo Folder", InspectorReveal);
        popup.IdPressed += id => OnInspectorMenu((int)id);
        header.AddChild(menu);
        column.AddChild(header);

        // The body scrolls, so the stat cards never push the window's layout taller.
        var scroll = new ScrollContainer
        {
            SizeFlagsVertical = SizeFlags.ExpandFill,
            HorizontalScrollMode = ScrollContainer.ScrollMode.Disabled,
        };
        column.AddChild(scroll);
        var body = new VBoxContainer { SizeFlagsHorizontal = SizeFlags.ExpandFill, SizeFlagsVertical = SizeFlags.ExpandFill };
        body.AddThemeConstantOverride("separation", 10);
        scroll.AddChild(body);
        body.AddChild(BuildSummary());

        _info = new RichTextLabel
        {
            BbcodeEnabled = true,
            ScrollActive = true,
            SelectionEnabled = true,
            SizeFlagsVertical = SizeFlags.ExpandFill,
            Visible = false,
        };
        _info.AddThemeFontSizeOverride("normal_font_size", 13);
        _info.AddThemeFontSizeOverride("bold_font_size", 13);
        _info.MetaClicked += meta => LinkClicked?.Invoke(meta.AsString());
        body.AddChild(_info);
    }

    /// <summary>Repo name, path, branch and the stat cards (shown when nothing is selected).</summary>
    private Control BuildSummary()
    {
        _summary = new VBoxContainer { Visible = false, SizeFlagsVertical = SizeFlags.ExpandFill };
        _summary.AddThemeConstantOverride("separation", 8);

        var nameRow = new HBoxContainer();
        nameRow.AddThemeConstantOverride("separation", 10);
        nameRow.AddChild(UiKit.IconRect("database", 28f));
        _summaryName = new Label
        {
            TextOverrunBehavior = TextServer.OverrunBehavior.TrimEllipsis,
            SizeFlagsHorizontal = SizeFlags.ExpandFill,
            VerticalAlignment = VerticalAlignment.Center,
        };
        _summaryName.AddThemeFontOverride("font", UiKit.BoldFont());
        _summaryName.AddThemeFontSizeOverride("font_size", 22);
        _summaryName.AddThemeColorOverride("font_color", Colors.White);
        nameRow.AddChild(_summaryName);
        _summary.AddChild(nameRow);

        _summaryPath = new Label { TextOverrunBehavior = TextServer.OverrunBehavior.TrimEllipsis };
        _summaryPath.AddThemeFontSizeOverride("font_size", 13);
        _summaryPath.AddThemeColorOverride("font_color", Palette.MutedText);
        _summary.AddChild(_summaryPath);

        _summaryBranchRow = new HBoxContainer();
        _summaryBranchRow.AddThemeConstantOverride("separation", 6);
        _summaryBranchRow.AddChild(UiKit.IconRect("branch", 16f, new Color(1f, 1f, 1f, 0.75f)));
        _summaryBranch = new Label();
        _summaryBranch.AddThemeFontSizeOverride("font_size", 13);
        _summaryBranch.AddThemeColorOverride("font_color", Palette.MutedText);
        _summaryBranchRow.AddChild(_summaryBranch);
        _summary.AddChild(_summaryBranchRow);

        var grid = new GridContainer { Columns = 2 };
        grid.AddThemeConstantOverride("h_separation", 8);
        grid.AddThemeConstantOverride("v_separation", 8);
        _summary.AddChild(grid);
        foreach (var (key, icon, caption) in SummaryCards)
        {
            var card = UiKit.Card(icon, caption, out var value);
            _cards[key] = value;
            // The last card spans both columns.
            if (key == SummaryCards[^1].Key) _summary.AddChild(card);
            else grid.AddChild(card);
        }

        var hint = new Label
        {
            Text = "Click a system to open it, or pick anything from the Outline.",
            AutowrapMode = TextServer.AutowrapMode.WordSmart,
        };
        hint.AddThemeFontSizeOverride("font_size", 12);
        hint.AddThemeColorOverride("font_color", Palette.MutedText);
        _summary.AddChild(hint);
        return _summary;
    }

    private void OnInspectorMenu(int id)
    {
        switch (id)
        {
            case InspectorSummary:
                ClearSelectionRequested?.Invoke();
                break;
            case InspectorCopyPath when _repoRoot.Length > 0:
                DisplayServer.ClipboardSet(_repoRoot);
                SetStatus($"Copied {_repoRoot}");
                break;
            case InspectorReveal when Directory.Exists(_repoRoot):
                OS.ShellOpen(_repoRoot);
                break;
        }
    }

    private void BuildLegend(Control parent, bool visible)
    {
        _legend = MakePanel();
        _legend.OffsetLeft = 14f;
        _legend.OffsetTop = 14f;
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
        bar.AddThemeStyleboxOverride("panel", UiKit.Strip(14f, 7f, lineTop: true));
        bar.MouseEntered += () => PointerEnteredUi?.Invoke();

        var row = new HBoxContainer();
        row.AddThemeConstantOverride("separation", 10);
        bar.AddChild(row);

        var dim = new Color(1f, 1f, 1f, 0.8f);
        row.AddChild(UiKit.IconRect("info", 18f, dim));
        _status = new Label
        {
            Text = "",
            TextOverrunBehavior = TextServer.OverrunBehavior.TrimEllipsis,
            SizeFlagsHorizontal = SizeFlags.ExpandFill,
            SizeFlagsStretchRatio = 1.6f,
            VerticalAlignment = VerticalAlignment.Center,
        };
        _status.AddThemeFontSizeOverride("font_size", 13);
        row.AddChild(_status);

        _selectionLabel = new Label { Text = "", VerticalAlignment = VerticalAlignment.Center };
        _selectionLabel.AddThemeFontSizeOverride("font_size", 13);
        _selectionLabel.AddThemeColorOverride("font_color", Palette.Accent);
        row.AddChild(_selectionLabel);

        row.AddChild(new VSeparator { CustomMinimumSize = new Vector2(12f, 0f) });
        row.AddChild(UiKit.IconRect("mouse", 18f, dim));
        var hint = new Label
        {
            Text = "Drag orbit · Right-drag pan · Scroll zoom · Click open/select · Shift-drag move · Esc back",
            TextOverrunBehavior = TextServer.OverrunBehavior.TrimEllipsis,
            SizeFlagsHorizontal = SizeFlags.ExpandFill,
            VerticalAlignment = VerticalAlignment.Center,
        };
        hint.AddThemeFontSizeOverride("font_size", 13);
        hint.AddThemeColorOverride("font_color", Palette.MutedText);
        row.AddChild(hint);
        return bar;
    }

    private void BuildTooltip()
    {
        _tooltip = new PanelContainer { MouseFilter = MouseFilterEnum.Ignore, Visible = false, TopLevel = true, ZIndex = 10 };
        _tooltip.AddThemeStyleboxOverride("panel", UiAssets.Box(Patch.Panel, 10f, 6f));
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

        Snapshots.BuildList();
        AddChild(Snapshots);
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
            case FileSnapshots:
                ManageSnapshotsRequested?.Invoke();
                break;
            case ViewFullscreen:
                FullscreenToggled?.Invoke();
                break;
            case ViewOnlyDifferences:
            {
                // In both View and the toolbar's Filters menu.
                var on = !IsChecked(_viewMenu, ViewOnlyDifferences);
                SetChecked(_viewMenu, ViewOnlyDifferences, on);
                SetChecked(_filtersMenu, ViewOnlyDifferences, on);
                OnlyDifferencesChanged?.Invoke(on);
                break;
            }
            case ViewUsageFilter:
                SetUsageFilterVisible(!(_filterGutter?.Visible ?? false));
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

    /// <summary>A side column (and its gutter and resize gap) takes no space when its panels are hidden.</summary>
    private void UpdateRightColumn()
    {
        var right = Methods.Visible || (_inspector?.Visible ?? false);
        if (_rightColumn != null) _rightColumn.Visible = right;
        if (_rightGutter != null) _rightGutter.Visible = right;
        if (_rightResizer != null) _rightResizer.Visible = right;
        if (_outlineGutter != null) _outlineGutter.Visible = Outline.Visible;
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

    private static bool IsChecked(PopupMenu? menu, int id)
    {
        if (menu == null) return false;
        var index = menu.GetItemIndex(id);
        return index >= 0 && menu.IsItemChecked(index);
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
