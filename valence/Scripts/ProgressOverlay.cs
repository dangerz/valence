using System;
using Godot;
using Valence.Core;

namespace Valence.Viewer;

/// <summary>Modal "Analyzing…" dialog: phase, progress bar, current file, log tail and Cancel.</summary>
public partial class ProgressOverlay : Control
{
    private const int MaxLogParagraphs = 400;

    private Label? _title;
    private Label? _phase;
    private Label? _detail;
    private ProgressBar? _bar;
    private RichTextLabel? _log;
    private Button? _button;
    private bool _finished;

    public event Action? CancelRequested;

    public void Build()
    {
        SetAnchorsPreset(LayoutPreset.FullRect);
        MouseFilter = MouseFilterEnum.Stop;
        Visible = false;

        var dim = new ColorRect { Color = new Color(0f, 0f, 0f, 0.55f), MouseFilter = MouseFilterEnum.Stop };
        dim.SetAnchorsPreset(LayoutPreset.FullRect);
        AddChild(dim);

        var center = new CenterContainer { MouseFilter = MouseFilterEnum.Ignore };
        center.SetAnchorsPreset(LayoutPreset.FullRect);
        AddChild(center);

        var panel = Hud.MakePanel(Patch.Accent, 18f);
        panel.CustomMinimumSize = new Vector2(620f, 0f);
        center.AddChild(panel);

        var column = new VBoxContainer();
        column.AddThemeConstantOverride("separation", 8);
        panel.AddChild(column);

        _title = new Label();
        _title.AddThemeFontSizeOverride("font_size", 18);
        column.AddChild(_title);

        _phase = new Label();
        column.AddChild(_phase);

        _bar = new ProgressBar
        {
            MinValue = 0,
            MaxValue = 100,
            Step = 0.1,
            ShowPercentage = true,
            CustomMinimumSize = new Vector2(0f, 22f),
        };
        column.AddChild(_bar);

        _detail = new Label { ClipText = true };
        _detail.AddThemeFontSizeOverride("font_size", 12);
        _detail.AddThemeColorOverride("font_color", Palette.MutedText);
        column.AddChild(_detail);

        _log = new RichTextLabel
        {
            BbcodeEnabled = false,
            ScrollFollowing = true,
            SelectionEnabled = true,
            CustomMinimumSize = new Vector2(0f, 180f),
        };
        _log.AddThemeFontSizeOverride("normal_font_size", 11);
        _log.AddThemeColorOverride("default_color", Palette.MutedText);
        column.AddChild(_log);

        var buttons = new HBoxContainer { Alignment = BoxContainer.AlignmentMode.End };
        column.AddChild(buttons);
        _button = new Button { Text = "Cancel", CustomMinimumSize = new Vector2(100f, 0f) };
        _button.Pressed += OnButton;
        buttons.AddChild(_button);
    }

    public void Begin(string title)
    {
        _finished = false;
        if (_title != null) _title.Text = title;
        if (_phase != null) _phase.Text = "Starting…";
        if (_detail != null) _detail.Text = "";
        if (_bar != null) _bar.Value = 0;
        _log?.Clear();
        if (_button != null)
        {
            _button.Text = "Cancel";
            _button.Disabled = false;
        }
        Visible = true;
    }

    public void SetProgress(AnalysisProgress progress)
    {
        if (_finished) return;
        if (_phase != null) _phase.Text = progress.Phase;
        if (_bar != null) _bar.Value = progress.Fraction * 100.0;
        if (_detail != null) _detail.Text = progress.Detail ?? "";
    }

    public void AppendLog(string line)
    {
        if (_log == null) return;
        _log.AddText(line + "\n");
        while (_log.GetParagraphCount() > MaxLogParagraphs) _log.RemoveParagraph(0);
    }

    public void Finish() => Visible = false;

    public void Fail(string message)
    {
        _finished = true;
        if (_phase != null) _phase.Text = "Analysis failed";
        if (_detail != null) _detail.Text = message;
        AppendLog("ERROR: " + message);
        if (_button != null)
        {
            _button.Text = "Close";
            _button.Disabled = false;
        }
    }

    private void OnButton()
    {
        if (_finished)
        {
            Visible = false;
            return;
        }
        CancelRequested?.Invoke();
        if (_button != null)
        {
            _button.Text = "Cancelling…";
            _button.Disabled = true;
        }
    }
}
