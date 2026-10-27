using Godot;

namespace Valence.Viewer;

/// <summary>
/// An in-app dialog: dims the screen and shows a bordered, fixed-width panel with a title, content
/// and an OK button. Drawn inside the viewer (not an OS window), so it looks the same in fullscreen.
/// OK, Esc or clicking outside closes it.
/// </summary>
public partial class ModalDialog : Control
{
    private Label? _title;

    /// <summary>Add the dialog's content here.</summary>
    public VBoxContainer Body { get; } = new();

    /// <summary>Width for wrapping text inside the dialog.</summary>
    public float ContentWidth { get; private set; }

    public void Build(string title, float width)
    {
        ContentWidth = width - 48f;
        SetAnchorsPreset(LayoutPreset.FullRect);
        MouseFilter = MouseFilterEnum.Stop;
        Visible = false;

        var dim = new ColorRect { Color = new Color(0f, 0f, 0f, 0.6f), MouseFilter = MouseFilterEnum.Stop };
        dim.SetAnchorsPreset(LayoutPreset.FullRect);
        dim.GuiInput += e =>
        {
            if (e is InputEventMouseButton { Pressed: true, ButtonIndex: MouseButton.Left }) Close();
        };
        AddChild(dim);

        var center = new CenterContainer { MouseFilter = MouseFilterEnum.Ignore };
        center.SetAnchorsPreset(LayoutPreset.FullRect);
        AddChild(center);

        var panel = new PanelContainer { CustomMinimumSize = new Vector2(width, 0f) };
        panel.AddThemeStyleboxOverride("panel", ValenceTheme.Box(ValenceTheme.Panel, Palette.Accent, 2, 10, 22f));
        center.AddChild(panel);

        var column = new VBoxContainer();
        column.AddThemeConstantOverride("separation", 14);
        panel.AddChild(column);

        _title = new Label { Text = title };
        _title.AddThemeFontSizeOverride("font_size", 20);
        _title.AddThemeColorOverride("font_color", Palette.Accent);
        column.AddChild(_title);
        column.AddChild(new HSeparator());

        Body.AddThemeConstantOverride("separation", 12);
        column.AddChild(Body);

        var buttons = new HBoxContainer { Alignment = BoxContainer.AlignmentMode.End };
        column.AddChild(buttons);
        var ok = new Button { Text = "OK", CustomMinimumSize = new Vector2(110f, 34f) };
        ok.Pressed += Close;
        buttons.AddChild(ok);
    }

    /// <summary>A wrapped paragraph sized to the dialog (wrapping labels need a fixed width).</summary>
    public Label AddText(string text, HorizontalAlignment alignment = HorizontalAlignment.Left, int fontSize = 14)
    {
        var label = new Label
        {
            Text = text,
            AutowrapMode = TextServer.AutowrapMode.WordSmart,
            HorizontalAlignment = alignment,
            CustomMinimumSize = new Vector2(ContentWidth, 0f),
        };
        label.AddThemeFontSizeOverride("font_size", fontSize);
        Body.AddChild(label);
        return label;
    }

    public void Open() => Visible = true;

    public void Close() => Visible = false;

    public override void _Input(InputEvent @event)
    {
        // While open, keys belong to the dialog: shortcuts behind it don't fire.
        if (!Visible || @event is not InputEventKey key) return;
        if (key.Pressed && key.Keycode is Key.Escape or Key.Enter or Key.KpEnter) Close();
        GetViewport().SetInputAsHandled();
    }
}
