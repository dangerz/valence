using System;
using System.IO;
using System.Linq;
using Godot;

namespace Valence.Viewer;

/// <summary>
/// The Flow chart drawer under the 3D view: the simulation as a flow chart (<see cref="FlowChartView"/>)
/// with Fit, Follow, Group by class, and Export (SVG, PNG, Mermaid, or Mermaid to the clipboard).
/// Files go to ~/.valence/exports by default, through a save dialog.
/// </summary>
public partial class FlowDrawer : PanelContainer
{
    private const int ExportSvg = 0;
    private const int ExportPng = 1;
    private const int ExportMermaid = 2;
    private const int CopyMermaid = 3;
    private const int MaxPngSide = 8192;

    private Label? _subtitle;
    private Button? _follow;
    private CheckBox? _group;
    private FileDialog? _saveDialog;
    private int _pendingFormat;

    public FlowChartView View { get; } = new();

    /// <summary>The ✕ was pressed.</summary>
    public event Action? CloseRequested;

    /// <summary>Group by class toggled: Main rebuilds the chart.</summary>
    public event Action<bool>? GroupingChanged;

    /// <summary>A message for the status bar (saved, copied, failed).</summary>
    public event Action<string>? Status;

    public void Build()
    {
        AddThemeStyleboxOverride("panel", UiAssets.Box(Patch.Panel, 12f, 10f));
        var column = new VBoxContainer();
        column.AddThemeConstantOverride("separation", 8);
        AddChild(column);

        var header = UiKit.Header("graph", UiKit.Title("Flow chart"));
        _subtitle = new Label
        {
            TextOverrunBehavior = TextServer.OverrunBehavior.TrimEllipsis,
            SizeFlagsHorizontal = SizeFlags.ExpandFill,
            VerticalAlignment = VerticalAlignment.Center,
        };
        _subtitle.AddThemeFontSizeOverride("font_size", 13);
        _subtitle.AddThemeColorOverride("font_color", Palette.MutedText);
        header.AddChild(_subtitle);

        _group = new CheckBox
        {
            Text = "Group by class",
            FocusMode = FocusModeEnum.None,
            TooltipText = "One box per class instead of per method (automatic for big flows)",
        };
        _group.AddThemeFontSizeOverride("font_size", 13);
        _group.Toggled += on => GroupingChanged?.Invoke(on);
        header.AddChild(_group);

        _follow = UiKit.TextButton("pointer", "Follow", "Keep the newest step in view while it plays");
        _follow.ToggleMode = true;
        _follow.ButtonPressed = true;
        _follow.CustomMinimumSize = new Vector2(0f, 30f);
        _follow.Toggled += on => View.Follow = on;
        header.AddChild(_follow);

        header.AddChild(UiKit.IconButton("focus", "Fit the whole chart", () => View.Fit(), 30f));

        var export = new MenuButton
        {
            Text = "Export  ▾",
            Icon = UiAssets.Icon("copy"),
            Flat = false,
            TooltipText = "Save the chart as SVG, PNG or Mermaid",
            CustomMinimumSize = new Vector2(0f, 30f),
        };
        var menu = export.GetPopup();
        menu.AddItem("Export SVG…", ExportSvg);
        menu.AddItem("Export PNG…", ExportPng);
        menu.AddItem("Export Mermaid…", ExportMermaid);
        menu.AddSeparator();
        menu.AddItem("Copy as Mermaid", CopyMermaid);
        menu.IdPressed += id => OnExport((int)id);
        header.AddChild(export);

        header.AddChild(UiKit.IconButton("close", "Close the flow chart (Simulate ▸ Flow Chart brings it back)",
            () => CloseRequested?.Invoke(), 28f, flat: true));
        column.AddChild(header);

        View.CustomMinimumSize = new Vector2(0f, 240f);
        View.SizeFlagsVertical = SizeFlags.ExpandFill;
        column.AddChild(View);

        _saveDialog = new FileDialog
        {
            FileMode = FileDialog.FileModeEnum.SaveFile,
            Access = FileDialog.AccessEnum.Filesystem,
            UseNativeDialog = true,
        };
        _saveDialog.FileSelected += Save;
        AddChild(_saveDialog);
    }

    public void ShowChart(FlowChart? chart)
    {
        View.SetChart(chart);
        _follow?.SetPressedNoSignal(true);
        _group?.SetPressedNoSignal(chart?.Grouped ?? false);
        if (_subtitle == null) return;
        _subtitle.Text = chart == null
            ? ""
            : $"{chart.Title} · {chart.Boxes.Count} {(chart.Grouped ? "classes" : "methods")}, {chart.Arrows.Count} links" +
              (chart.Truncated ? " (the flow was cut short)" : "");
    }

    public void SetPlayhead(float? time, int tick) => View.SetPlayhead(time, tick);

    public override void _Process(double delta)
    {
        // Panning or zooming turns Follow off; keep the button honest.
        if (_follow != null && _follow.ButtonPressed != View.Follow) _follow.SetPressedNoSignal(View.Follow);
    }

    // ---------------------------------------------------------------- export

    private void OnExport(int id)
    {
        if (View.Chart is not { } chart)
        {
            Status?.Invoke("Start a simulation first; its flow chart is what gets exported.");
            return;
        }
        if (id == CopyMermaid)
        {
            DisplayServer.ClipboardSet(chart.ToMermaid());
            Status?.Invoke("Copied the flow chart as Mermaid (paste it into a ```mermaid block in markdown)");
            return;
        }
        if (_saveDialog == null) return;

        var (extension, filter) = id switch
        {
            ExportSvg => (".svg", "*.svg ; SVG image"),
            ExportPng => (".png", "*.png ; PNG image"),
            _ => (".mmd", "*.mmd ; Mermaid diagram"),
        };
        _pendingFormat = id;
        var folder = Path.Combine(System.Environment.GetFolderPath(System.Environment.SpecialFolder.UserProfile), ".valence", "exports");
        Directory.CreateDirectory(folder);
        _saveDialog.Title = "Export the flow chart";
        _saveDialog.Filters = new[] { filter };
        _saveDialog.CurrentDir = folder;
        _saveDialog.CurrentFile = Slug(chart.Title) + extension;
        _saveDialog.PopupCentered(new Vector2I(900, 600));
    }

    private void Save(string path)
    {
        if (View.Chart is not { } chart) return;
        try
        {
            switch (_pendingFormat)
            {
                case ExportSvg:
                    File.WriteAllText(path, chart.ToSvg());
                    Status?.Invoke($"Saved {path}");
                    break;
                case ExportPng:
                    SavePng(chart, path);
                    break;
                default:
                    File.WriteAllText(path, chart.ToMermaid());
                    Status?.Invoke($"Saved {path}");
                    break;
            }
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            Status?.Invoke($"Could not save {path}: {e.Message}");
        }
    }

    /// <summary>Renders the whole chart (not just what's on screen) off-screen at up to 2× and saves it.</summary>
    private async void SavePng(FlowChart chart, string path)
    {
        var scale = Math.Min(2f, MaxPngSide / Math.Max(chart.Size.X, chart.Size.Y));
        var size = new Vector2I((int)Math.Ceiling(chart.Size.X * scale), (int)Math.Ceiling(chart.Size.Y * scale));
        var viewport = new SubViewport
        {
            Size = size,
            TransparentBg = false,
            Disable3D = true,
            RenderTargetUpdateMode = SubViewport.UpdateMode.Always,
        };
        var view = new FlowChartView { Size = size };
        viewport.AddChild(view);
        AddChild(viewport);
        view.RenderWhole(chart, scale);

        // Let it lay out and draw before reading the pixels back.
        await ToSignal(RenderingServer.Singleton, RenderingServer.SignalName.FramePostDraw);
        await ToSignal(RenderingServer.Singleton, RenderingServer.SignalName.FramePostDraw);
        var image = viewport.GetTexture().GetImage();
        var error = image.SavePng(path);
        viewport.QueueFree();
        Status?.Invoke(error == Error.Ok ? $"Saved {path}" : $"Could not save {path}: {error}");
    }

    /// <summary>"Flow from Player._Process" → "flow-from-player-process".</summary>
    private static string Slug(string title)
    {
        var slug = new string(title.ToLowerInvariant().Select(c => char.IsLetterOrDigit(c) ? c : '-').ToArray());
        while (slug.Contains("--", StringComparison.Ordinal)) slug = slug.Replace("--", "-");
        slug = slug.Trim('-');
        return slug.Length == 0 ? "flow-chart" : slug[..Math.Min(80, slug.Length)];
    }
}
