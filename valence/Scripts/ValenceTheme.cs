using Godot;

namespace Valence.Viewer;

/// <summary>
/// One theme for the whole UI: dark panels with a blue border, rounded corners, clear hover and
/// pressed states. Set on the HUD root; menus, popups and dialogs inherit it.
/// </summary>
public static class ValenceTheme
{
    public static readonly Color Panel = new(0.07f, 0.09f, 0.14f, 0.97f);
    public static readonly Color PanelRaised = new(0.10f, 0.13f, 0.19f, 1f);
    public static readonly Color Hover = new(0.15f, 0.21f, 0.31f, 1f);
    public static readonly Color Pressed = new(0.20f, 0.29f, 0.42f, 1f);
    public static readonly Color Text = new(0.88f, 0.91f, 0.95f, 1f);

    public static Theme Build()
    {
        var theme = new Theme();

        // Menus (menu bar dropdowns, right-click menus, submenus).
        theme.SetStylebox("panel", "PopupMenu", Box(Panel, Palette.PanelBorder, 1, 6, 6f));
        theme.SetStylebox("hover", "PopupMenu", Box(Hover, Palette.Accent, 0, 4, 4f));
        theme.SetStylebox("separator", "PopupMenu", Line(Palette.PanelBorder));
        theme.SetColor("font_color", "PopupMenu", Text);
        theme.SetColor("font_hover_color", "PopupMenu", Colors.White);
        theme.SetColor("font_disabled_color", "PopupMenu", Palette.MutedText);
        theme.SetColor("font_accelerator_color", "PopupMenu", Palette.MutedText);
        theme.SetColor("font_separator_color", "PopupMenu", Palette.MutedText);
        theme.SetConstant("v_separation", "PopupMenu", 8);
        theme.SetConstant("h_separation", "PopupMenu", 10);

        // Menu bar titles.
        theme.SetStylebox("normal", "MenuBar", Box(Colors.Transparent, Colors.Transparent, 0, 4, 6f));
        theme.SetStylebox("hover", "MenuBar", Box(Hover, Palette.PanelBorder, 1, 4, 6f));
        theme.SetStylebox("pressed", "MenuBar", Box(Pressed, Palette.Accent, 1, 4, 6f));
        theme.SetColor("font_color", "MenuBar", Text);
        theme.SetColor("font_hover_color", "MenuBar", Colors.White);
        theme.SetColor("font_pressed_color", "MenuBar", Colors.White);

        // Buttons and dropdowns.
        foreach (var type in new[] { "Button", "OptionButton" })
        {
            theme.SetStylebox("normal", type, Box(PanelRaised, Palette.PanelBorder, 1, 6, 6f));
            theme.SetStylebox("hover", type, Box(Hover, Palette.Accent, 1, 6, 6f));
            theme.SetStylebox("pressed", type, Box(Pressed, Palette.Accent, 1, 6, 6f));
            theme.SetStylebox("disabled", type, Box(Panel, Palette.PanelBorder.Darkened(0.4f), 1, 6, 6f));
            theme.SetStylebox("focus", type, Box(Colors.Transparent, Palette.Accent, 1, 6, 6f));
            theme.SetColor("font_color", type, Text);
            theme.SetColor("font_hover_color", type, Colors.White);
            theme.SetColor("font_pressed_color", type, Colors.White);
            theme.SetColor("font_disabled_color", type, Palette.MutedText);
        }

        // Text fields.
        theme.SetStylebox("normal", "LineEdit", Box(new Color(0.04f, 0.06f, 0.09f, 1f), Palette.PanelBorder, 1, 6, 6f));
        theme.SetStylebox("focus", "LineEdit", Box(Colors.Transparent, Palette.Accent, 1, 6, 6f));
        theme.SetColor("font_color", "LineEdit", Text);
        theme.SetColor("font_placeholder_color", "LineEdit", Palette.MutedText);
        theme.SetColor("caret_color", "LineEdit", Palette.Accent);

        // Outline tree.
        theme.SetStylebox("panel", "Tree", Box(new Color(0.04f, 0.06f, 0.09f, 1f), Palette.PanelBorder, 1, 6, 4f));
        theme.SetStylebox("focus", "Tree", Box(Colors.Transparent, Colors.Transparent, 0, 6, 0f));
        theme.SetStylebox("selected", "Tree", Box(Pressed, Colors.Transparent, 0, 4, 0f));
        theme.SetStylebox("selected_focus", "Tree", Box(Pressed, Palette.Accent, 1, 4, 0f));
        theme.SetStylebox("hovered", "Tree", Box(Hover, Colors.Transparent, 0, 4, 0f));
        theme.SetColor("font_color", "Tree", Text);
        theme.SetColor("font_selected_color", "Tree", Colors.White);
        theme.SetColor("guide_color", "Tree", Palette.PanelBorder);
        theme.SetConstant("v_separation", "Tree", 4);

        // Tooltips.
        theme.SetStylebox("panel", "TooltipPanel", Box(Panel, Palette.Accent, 1, 6, 8f));
        theme.SetColor("font_color", "TooltipLabel", Text);

        // Progress bar, sliders, scrollbars.
        theme.SetStylebox("background", "ProgressBar", Box(new Color(0.04f, 0.06f, 0.09f, 1f), Palette.PanelBorder, 1, 6, 0f));
        theme.SetStylebox("fill", "ProgressBar", Box(Palette.Accent.Darkened(0.2f), Colors.Transparent, 0, 6, 0f));
        theme.SetColor("font_color", "ProgressBar", Colors.White);
        theme.SetStylebox("slider", "HSlider", Box(Palette.PanelBorder, Colors.Transparent, 0, 3, 2f));
        theme.SetStylebox("grabber_area", "HSlider", Box(Palette.Accent, Colors.Transparent, 0, 3, 2f));
        theme.SetStylebox("grabber_area_highlight", "HSlider", Box(Palette.Accent.Lightened(0.2f), Colors.Transparent, 0, 3, 2f));
        foreach (var type in new[] { "VScrollBar", "HScrollBar" })
        {
            theme.SetStylebox("scroll", type, Box(new Color(0f, 0f, 0f, 0.2f), Colors.Transparent, 0, 4, 2f));
            theme.SetStylebox("grabber", type, Box(Palette.PanelBorder, Colors.Transparent, 0, 4, 4f));
            theme.SetStylebox("grabber_highlight", type, Box(Palette.Accent.Darkened(0.2f), Colors.Transparent, 0, 4, 4f));
            theme.SetStylebox("grabber_pressed", type, Box(Palette.Accent, Colors.Transparent, 0, 4, 4f));
        }

        // Check boxes, labels, separators.
        theme.SetColor("font_color", "CheckBox", Text);
        theme.SetColor("font_color", "Label", Text);
        theme.SetStylebox("separator", "HSeparator", Line(Palette.PanelBorder));
        return theme;
    }

    /// <summary>A rounded panel box with an optional border.</summary>
    public static StyleBoxFlat Box(Color background, Color border, int borderWidth, int radius, float margin)
    {
        var box = new StyleBoxFlat { BgColor = background, BorderColor = border };
        box.SetBorderWidthAll(borderWidth);
        box.SetCornerRadiusAll(radius);
        box.SetContentMarginAll(margin);
        return box;
    }

    private static StyleBoxLine Line(Color color) => new() { Color = color, Thickness = 1 };
}
