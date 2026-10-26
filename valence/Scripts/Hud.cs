using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Godot;

namespace Valence.Viewer;

/// <summary>Screen-space UI: toolbar, status line, info panel, legend, help.</summary>
public partial class Hud : Control
{
    private Label? _repoLabel;
    private Label? _status;
    private LineEdit? _search;
    private PanelContainer? _infoPanel;
    private RichTextLabel? _info;
    private RichTextLabel? _legendNucleons;
    private FileDialog? _openDialog;
    private FileDialog? _sourceDialog;

    public event Action<string>? GraphFileChosen;
    public event Action<string>? SourceFolderChosen;
    public event Action<string>? SearchSubmitted;
    public event Action<ColorMode>? ColorModeChanged;
    public event Action? FrameAllRequested;
    public event Action? ResetLayoutRequested;
    public event Action<string, bool>? EdgeKindToggled;
    public event Action<string>? LinkClicked;

    /// <summary>Builds the UI. Call once, before adding to the tree.</summary>
    public void Build()
    {
        SetAnchorsPreset(LayoutPreset.FullRect);
        MouseFilter = MouseFilterEnum.Ignore;

        BuildToolbar();
        BuildInfoPanel();
        BuildLegend();
        BuildHelp();
        BuildDialogs();
    }

    public void SetRepo(string text)
    {
        if (_repoLabel != null) _repoLabel.Text = text;
    }

    public void SetStatus(string text)
    {
        if (_status != null) _status.Text = text;
    }

    public void ShowInfo(string bbcode)
    {
        if (_info == null || _infoPanel == null) return;
        _info.Text = bbcode;
        _info.ScrollToLine(0);
        _infoPanel.Visible = true;
    }

    public void HideInfo()
    {
        if (_infoPanel != null) _infoPanel.Visible = false;
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

    // ---------------------------------------------------------------- construction

    private void BuildToolbar()
    {
        var panel = MakePanel();
        panel.AnchorRight = 1f;
        panel.OffsetLeft = 12f;
        panel.OffsetTop = 12f;
        panel.OffsetRight = -12f;
        AddChild(panel);

        var rows = new VBoxContainer();
        rows.AddThemeConstantOverride("separation", 4);
        panel.AddChild(rows);

        var bar = new HBoxContainer();
        bar.AddThemeConstantOverride("separation", 8);
        rows.AddChild(bar);

        var title = new Label { Text = "VALENCE", VerticalAlignment = VerticalAlignment.Center };
        title.AddThemeFontSizeOverride("font_size", 18);
        title.AddThemeColorOverride("font_color", Palette.Accent);
        bar.AddChild(title);

        bar.AddChild(MakeButton("Open source folder…", () => Popup(_sourceDialog, null)));
        bar.AddChild(MakeButton("Open graph…", () => Popup(_openDialog, DefaultGraphDir())));
        bar.AddChild(new VSeparator());

        _repoLabel = new Label
        {
            Text = "No graph loaded",
            SizeFlagsHorizontal = SizeFlags.ExpandFill,
            VerticalAlignment = VerticalAlignment.Center,
            ClipText = true,
        };
        bar.AddChild(_repoLabel);

        _search = new LineEdit { PlaceholderText = "Find class or system… (Enter = next)", CustomMinimumSize = new Vector2(280f, 0f) };
        _search.TextSubmitted += text => SearchSubmitted?.Invoke(text);
        bar.AddChild(_search);

        var colors = new OptionButton();
        colors.AddItem("Color: protons / neutrons", (int)ColorMode.Visibility);
        colors.AddItem("Color: kind", (int)ColorMode.Kind);
        colors.AddItem("Color: churn", (int)ColorMode.Churn);
        colors.AddItem("Color: coupling", (int)ColorMode.Coupling);
        colors.ItemSelected += index => ColorModeChanged?.Invoke((ColorMode)colors.GetItemId((int)index));
        bar.AddChild(colors);

        bar.AddChild(MakeButton("Frame (F)", () => FrameAllRequested?.Invoke()));
        bar.AddChild(MakeButton("Reset layout", () => ResetLayoutRequested?.Invoke()));

        _status = new Label { Text = "", ClipText = true };
        _status.AddThemeFontSizeOverride("font_size", 12);
        _status.AddThemeColorOverride("font_color", Palette.MutedText);
        rows.AddChild(_status);
    }

    private void BuildInfoPanel()
    {
        _infoPanel = MakePanel();
        _infoPanel.AnchorLeft = 1f;
        _infoPanel.AnchorRight = 1f;
        _infoPanel.AnchorBottom = 1f;
        _infoPanel.OffsetLeft = -404f;
        _infoPanel.OffsetRight = -12f;
        _infoPanel.OffsetTop = 90f;
        _infoPanel.OffsetBottom = -44f;
        _infoPanel.Visible = false;
        AddChild(_infoPanel);

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
        _infoPanel.AddChild(_info);
    }

    private void BuildLegend()
    {
        var panel = MakePanel();
        panel.AnchorTop = 1f;
        panel.AnchorBottom = 1f;
        panel.OffsetLeft = 12f;
        panel.OffsetTop = -12f;
        panel.OffsetBottom = -12f;
        panel.GrowVertical = GrowDirection.Begin;
        panel.CustomMinimumSize = new Vector2(420f, 0f);
        AddChild(panel);

        var column = new VBoxContainer();
        column.AddThemeConstantOverride("separation", 4);
        panel.AddChild(column);

        var bonds = Rich(
            $"[b]Bonds[/b] (between systems, thickness = number of dependencies)\n" +
            $"[color={Palette.Html(Palette.Ionic)}]━━[/color] one-way: pulses flow from dependent to dependency\n" +
            $"[color={Palette.Html(Palette.Covalent)}]━━[/color] covalent: both depend on each other (cycle)\n" +
            "Height = dependency layer: systems sit above what they depend on");
        column.AddChild(bonds);

        column.AddChild(Rich("[b]Nucleons[/b] (classes)"));
        _legendNucleons = Rich("");
        column.AddChild(_legendNucleons);

        column.AddChild(Rich("[b]Class links[/b] (shown for the selection)"));
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

    private void BuildHelp()
    {
        var help = new Label
        {
            Text = "Drag: orbit · Right-drag: pan · Wheel/pinch: zoom · Click: focus/select · " +
                   "Shift-drag a system: move it (saved) · Double-click empty: frame all · Esc: back",
            HorizontalAlignment = HorizontalAlignment.Right,
            AnchorLeft = 1f,
            AnchorRight = 1f,
            AnchorTop = 1f,
            AnchorBottom = 1f,
            OffsetRight = -14f,
            OffsetTop = -30f,
            OffsetBottom = -10f,
            GrowHorizontal = GrowDirection.Begin,
        };
        help.AddThemeFontSizeOverride("font_size", 12);
        help.AddThemeColorOverride("font_color", Palette.MutedText);
        AddChild(help);
    }

    private void BuildDialogs()
    {
        _openDialog = new FileDialog
        {
            Title = "Open a Valence graph",
            FileMode = FileDialog.FileModeEnum.OpenFile,
            Access = FileDialog.AccessEnum.Filesystem,
            UseNativeDialog = true,
            Filters = new[] { "*.json ; Valence graph" },
        };
        _openDialog.FileSelected += path => GraphFileChosen?.Invoke(path);
        AddChild(_openDialog);

        _sourceDialog = new FileDialog
        {
            Title = "Open a source folder (it is only read, never changed)",
            FileMode = FileDialog.FileModeEnum.OpenDir,
            Access = FileDialog.AccessEnum.Filesystem,
            UseNativeDialog = true,
        };
        _sourceDialog.DirSelected += dir => SourceFolderChosen?.Invoke(dir);
        AddChild(_sourceDialog);
    }

    // ---------------------------------------------------------------- helpers

    private static void Popup(FileDialog? dialog, string? directory)
    {
        if (dialog == null) return;
        if (directory != null && Directory.Exists(directory)) dialog.CurrentDir = directory;
        dialog.PopupCentered(new Vector2I(900, 600));
    }

    private static string DefaultGraphDir() => Path.Combine(
        System.Environment.GetFolderPath(System.Environment.SpecialFolder.UserProfile), ".valence", "graphs");

    private static PanelContainer MakePanel()
    {
        var style = new StyleBoxFlat { BgColor = new Color(0.06f, 0.08f, 0.12f, 0.88f) };
        style.SetCornerRadiusAll(8);
        style.SetContentMarginAll(10f);
        var panel = new PanelContainer();
        panel.AddThemeStyleboxOverride("panel", style);
        return panel;
    }

    private static Button MakeButton(string text, Action onPressed)
    {
        var button = new Button { Text = text };
        button.Pressed += onPressed;
        return button;
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
