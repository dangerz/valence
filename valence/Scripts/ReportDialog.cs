using System;
using System.IO;
using System.Linq;
using Godot;

namespace Valence.Viewer;

/// <summary>
/// Shows an Insights <see cref="Report"/>: click a class to select it, "Show Only These" filters the 3D
/// view to the report's classes, and the report can be copied or saved as Markdown.
/// </summary>
public partial class ReportDialog : ModalDialog
{
    private RichTextLabel? _text;
    private Button? _spotlight;
    private FileDialog? _save;
    private Report? _report;

    /// <summary>A class link was clicked ("node:&lt;id&gt;"); the dialog has closed.</summary>
    public event Action<string>? LinkClicked;

    /// <summary>"Show Only These" was pressed; the dialog has closed.</summary>
    public event Action<Report>? SpotlightRequested;

    public event Action<string>? Status;

    public void BuildReport()
    {
        Build("Insights", 1000f);

        _text = new RichTextLabel
        {
            BbcodeEnabled = true,
            ScrollActive = true,
            SelectionEnabled = true,
            CustomMinimumSize = new Vector2(ContentWidth, 560f),
        };
        _text.AddThemeFontSizeOverride("normal_font_size", 14);
        _text.AddThemeFontSizeOverride("bold_font_size", 14);
        _text.MetaClicked += meta =>
        {
            Close();
            LinkClicked?.Invoke(meta.AsString());
        };
        Body.AddChild(_text);

        var buttons = new HBoxContainer();
        buttons.AddThemeConstantOverride("separation", 8);
        _spotlight = UiKit.TextButton("eye", "Show Only These", "Hide every class that isn't in this report (Filters ▸ Clear Spotlight brings them back)", () =>
        {
            if (_report?.Spotlight is not { Count: > 0 }) return;
            Close();
            SpotlightRequested?.Invoke(_report);
        });
        buttons.AddChild(_spotlight);
        buttons.AddChild(UiKit.TextButton("copy", "Copy Markdown", "Copy the report as Markdown", () =>
        {
            if (_report == null) return;
            DisplayServer.ClipboardSet(_report.ToMarkdown());
            Status?.Invoke($"Copied \"{_report.Title}\" as Markdown");
        }));
        buttons.AddChild(UiKit.TextButton("arrow-down", "Save as Markdown…", "Save the report as a .md file", SaveMarkdown));
        Body.AddChild(buttons);

        _save = new FileDialog
        {
            Title = "Save the report",
            FileMode = FileDialog.FileModeEnum.SaveFile,
            Access = FileDialog.AccessEnum.Filesystem,
            UseNativeDialog = true,
            Filters = new[] { "*.md ; Markdown" },
        };
        _save.FileSelected += path =>
        {
            if (_report == null) return;
            try
            {
                File.WriteAllText(path, _report.ToMarkdown());
                Status?.Invoke($"Saved {path}");
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                Status?.Invoke($"Could not save {path}: {e.Message}");
            }
        };
        AddChild(_save);
    }

    public void ShowReport(Report report)
    {
        _report = report;
        SetTitle(report.Title);
        if (_text != null)
        {
            _text.Text = report.ToBbcode();
            _text.ScrollToLine(0);
        }
        if (_spotlight != null)
        {
            var count = report.Spotlight?.Count ?? 0;
            _spotlight.Visible = count > 0;
            _spotlight.Text = $"Show Only These ({count:N0})";
        }
        Open();
    }

    private void SaveMarkdown()
    {
        if (_report == null || _save == null) return;
        var folder = Path.Combine(System.Environment.GetFolderPath(System.Environment.SpecialFolder.UserProfile), ".valence", "exports");
        Directory.CreateDirectory(folder);
        _save.CurrentDir = folder;
        _save.CurrentFile = Slug(_report.Title) + ".md";
        _save.PopupCentered(new Vector2I(900, 600));
    }

    private static string Slug(string title)
    {
        var slug = new string(title.ToLowerInvariant().Select(c => char.IsLetterOrDigit(c) ? c : '-').ToArray());
        while (slug.Contains("--", StringComparison.Ordinal)) slug = slug.Replace("--", "-");
        slug = slug.Trim('-');
        return slug.Length == 0 ? "report" : slug[..Math.Min(80, slug.Length)];
    }
}
