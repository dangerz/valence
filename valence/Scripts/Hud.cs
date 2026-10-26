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
    private const int NavigateFind = 40;
    private const int NavigateBack = 41;
    private const int NavigateClear = 42;
    private const int HelpControls = 60;
    private const int HelpAbout = 61;
    private const int RecentBase = 1000;

    private static readonly (ColorMode Mode, string Label)[] ColorModes =
    {
        (ColorMode.Visibility, "Protons / Neutrons (public vs internal)"),
        (ColorMode.Kind, "Kind (class, interface, struct…)"),
        (ColorMode.Churn, "Churn (recent git changes)"),
        (ColorMode.Coupling, "Coupling (how many depend on it)"),
    };

    private readonly List<string> _recentFolders = new();
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
    private AcceptDialog? _helpDialog;
    private AcceptDialog? _aboutDialog;

    public OutlinePanel Outline { get; } = new();
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

    /// <summary>A "node:" / "atom:" link was clicked in the inspector or picked in the outline.</summary>
    public event Action<string>? LinkClicked;

    /// <summary>Outline, inspector, legend visibility changed (for saving).</summary>
    public event Action<bool, bool, bool>? PanelsChanged;

    /// <summary>The pointer moved onto a UI panel (so 3D hover should end).</summary>
    public event Action? PointerEnteredUi;

    /// <summary>Builds the UI. Call once, before adding to the tree.</summary>
    public void Build(bool showOutline, bool showInspector, bool showLegend)
    {
        SetAnchorsPreset(LayoutPreset.FullRect);
        MouseFilter = MouseFilterEnum.Ignore;

        var root = new VBoxContainer { MouseFilter = MouseFilterEnum.Ignore };
        root.SetAnchorsPreset(LayoutPreset.FullRect);
        root.AddThemeConstantOverride("separation", 0);
        AddChild(root);

        root.AddChild(BuildMenuBar());

        var middle = new HBoxContainer { MouseFilter = MouseFilterEnum.Ignore, SizeFlagsVertical = SizeFlags.ExpandFill };
        middle.AddThemeConstantOverride("separation", 0);
        root.AddChild(middle);

        Outline.AddThemeStyleboxOverride("panel", PanelStyle(0.92f, 0));
        Outline.CustomMinimumSize = new Vector2(300f, 0f);
        Outline.Build();
        Outline.ItemChosen += meta => LinkClicked?.Invoke(meta);
        Outline.MouseEntered += () => PointerEnteredUi?.Invoke();
        Outline.Visible = showOutline;
        middle.AddChild(Outline);

        // The 3D view shows through this area; it passes all input to the scene.
        var viewport = new Control { MouseFilter = MouseFilterEnum.Ignore, SizeFlagsHorizontal = SizeFlags.ExpandFill };
        middle.AddChild(viewport);
        BuildLegend(viewport, showLegend);

        BuildInspector(showInspector);
        middle.AddChild(_inspector!);

        root.AddChild(BuildStatusBar());

        BuildTooltip();
        BuildDialogs();

        Progress.Build();
        AddChild(Progress);

        SetChecked(_viewMenu, ViewOutline, showOutline);
        SetChecked(_viewMenu, ViewInspector, showInspector);
        SetChecked(_viewMenu, ViewLegend, showLegend);
        SetColorMode(ColorMode.Visibility);
        SetLinkDepth(1);
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
        panel.AddThemeStyleboxOverride("panel", PanelStyle(alpha, 8));
        return panel;
    }

    // ---------------------------------------------------------------- construction

    private Control BuildMenuBar()
    {
        var bar = new PanelContainer();
        bar.AddThemeStyleboxOverride("panel", PanelStyle(0.96f, 0, 4f));
        bar.MouseEntered += () => PointerEnteredUi?.Invoke();

        var row = new HBoxContainer();
        row.AddThemeConstantOverride("separation", 12);
        bar.AddChild(row);

        // Drawn in the window, not the macOS global menu (hidden in exclusive fullscreen).
        var menus = new MenuBar { PreferGlobalMenu = false, Flat = true };
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
        _viewMenu.AddCheckItem("Outline", ViewOutline);
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
        _inspector = new PanelContainer { CustomMinimumSize = new Vector2(400f, 0f), Visible = visible };
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
        bar.AddThemeStyleboxOverride("panel", PanelStyle(0.96f, 0, 4f));
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
        _tooltip.AddThemeStyleboxOverride("panel", PanelStyle(0.95f, 6, 6f));
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

        _helpDialog = new AcceptDialog
        {
            Title = "Controls",
            DialogText =
                "Drag — orbit the camera\n" +
                "Right-drag or Shift+two-finger swipe — pan\n" +
                "Scroll or pinch — zoom\n" +
                "Click a system — open it: classes spread out with labels, ports lead to other systems\n" +
                "Click a class — select it and light up its links (View ▸ Link Depth for more hops)\n" +
                "Click a port — jump to that system\n" +
                "Shift-drag a system — move it (saved per repo; View ▸ Reset Layout undoes)\n" +
                "F — frame everything · Esc — back out one level · ⌘F — find\n" +
                "⌘O — open a source folder · ⌘R — re-analyze",
        };
        AddChild(_helpDialog);

        _aboutDialog = new AcceptDialog
        {
            Title = "About Valence",
            DialogText =
                "Valence draws a codebase as chemistry: projects are molecules, systems are atoms,\n" +
                "classes are nucleons and dependencies are bonds.\n\n" +
                "It only reads the folder you point it at. Nothing is built, run or written there.",
        };
        AddChild(_aboutDialog);
    }

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
            case ViewOutline:
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
            case HelpControls:
                _helpDialog?.PopupCentered();
                break;
            case HelpAbout:
                _aboutDialog?.PopupCentered();
                break;
        }
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
            ViewInspector => _inspector,
            ViewLegend => _legend,
            _ => null,
        };
        if (panel == null) return;
        panel.Visible = !panel.Visible;
        SetChecked(_viewMenu, id, panel.Visible);
        PanelsChanged?.Invoke(Outline.Visible, _inspector?.Visible ?? false, _legend?.Visible ?? false);
    }

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

    private static StyleBoxFlat PanelStyle(float alpha, int corner, float margin = 10f)
    {
        var style = new StyleBoxFlat { BgColor = new Color(0.06f, 0.08f, 0.12f, alpha) };
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
