using Godot;

namespace Valence.Viewer;

/// <summary>
/// One theme for the whole UI, built from the nine-patch panels (<see cref="UiAssets"/>): the subtle
/// panel for resting controls and menus, blue on hover, cyan when pressed, the clear frame for focus.
/// Set on the HUD root; menus, popups and dialogs inherit it.
/// </summary>
public static class ValenceTheme
{
    public static readonly Color Panel = new(0.04f, 0.09f, 0.14f, 0.97f);
    public static readonly Color PanelRaised = new(0.07f, 0.13f, 0.2f, 1f);
    public static readonly Color Hover = new(0.11f, 0.2f, 0.31f, 1f);
    public static readonly Color Pressed = new(0.13f, 0.27f, 0.42f, 1f);
    public static readonly Color Text = UiKit.Text;
    private static readonly Color FieldBackground = new(0.02f, 0.05f, 0.08f, 1f);

    public static Theme Build()
    {
        var theme = new Theme();

        // Menus (menu bar dropdowns, right-click menus, submenus).
        theme.SetStylebox("panel", "PopupMenu", UiAssets.Box(Patch.Panel, 6f));
        theme.SetStylebox("hover", "PopupMenu", Box(Hover, Palette.Accent, 0, 4, 4f));
        theme.SetStylebox("separator", "PopupMenu", Line(Palette.PanelBorder));
        theme.SetColor("font_color", "PopupMenu", Text);
        theme.SetColor("font_hover_color", "PopupMenu", Colors.White);
        theme.SetColor("font_disabled_color", "PopupMenu", Palette.MutedText);
        theme.SetColor("font_accelerator_color", "PopupMenu", Palette.MutedText);
        theme.SetColor("font_separator_color", "PopupMenu", Palette.MutedText);
        theme.SetConstant("v_separation", "PopupMenu", 8);
        theme.SetConstant("h_separation", "PopupMenu", 10);
        theme.SetConstant("icon_max_width", "PopupMenu", 18);

        // Menu bar titles.
        theme.SetStylebox("normal", "MenuBar", Box(Colors.Transparent, Colors.Transparent, 0, 6, 8f));
        theme.SetStylebox("hover", "MenuBar", Box(Hover, Colors.Transparent, 0, 6, 8f));
        theme.SetStylebox("pressed", "MenuBar", Box(Pressed, Colors.Transparent, 0, 6, 8f));
        theme.SetColor("font_color", "MenuBar", Text);
        theme.SetColor("font_hover_color", "MenuBar", Colors.White);
        theme.SetColor("font_pressed_color", "MenuBar", Colors.White);
        theme.SetFontSize("font_size", "MenuBar", 15);

        // Buttons, dropdowns and menu buttons: subtle at rest, blue on hover, cyan when pressed.
        foreach (var type in new[] { "Button", "OptionButton", "MenuButton" })
        {
            theme.SetStylebox("normal", type, UiAssets.Box(Patch.Panel, 10f, 6f));
            theme.SetStylebox("hover", type, UiAssets.Box(Patch.Accent, 10f, 6f));
            theme.SetStylebox("pressed", type, UiAssets.Box(Patch.Active, 10f, 6f));
            theme.SetStylebox("hover_pressed", type, UiAssets.Box(Patch.Active, 10f, 6f));
            var disabled = UiAssets.Box(Patch.Panel, 10f, 6f);
            disabled.ModulateColor = new Color(1f, 1f, 1f, 0.45f);
            theme.SetStylebox("disabled", type, disabled);
            theme.SetStylebox("focus", type, new StyleBoxEmpty());
            theme.SetColor("font_color", type, Text);
            theme.SetColor("font_hover_color", type, Colors.White);
            theme.SetColor("font_pressed_color", type, Colors.White);
            theme.SetColor("font_hover_pressed_color", type, Colors.White);
            theme.SetColor("font_disabled_color", type, Palette.MutedText);
            theme.SetColor("icon_disabled_color", type, new Color(1f, 1f, 1f, 0.35f));
            theme.SetConstant("icon_max_width", type, 20);
            theme.SetConstant("h_separation", type, 8);
        }

        // Text fields.
        var field = UiAssets.Box(Patch.Panel, 10f, 6f);
        theme.SetStylebox("normal", "LineEdit", field);
        theme.SetStylebox("read_only", "LineEdit", field);
        theme.SetStylebox("focus", "LineEdit", UiAssets.Box(Patch.Focus, 10f, 6f));
        theme.SetColor("font_color", "LineEdit", Text);
        theme.SetColor("font_placeholder_color", "LineEdit", Palette.MutedText);
        theme.SetColor("caret_color", "LineEdit", Palette.Accent);
        theme.SetColor("clear_button_color", "LineEdit", Palette.MutedText);
        theme.SetColor("clear_button_color_pressed", "LineEdit", Palette.Accent);

        // Outline tree: no frame of its own (it sits in a panel); the selected row gets a cyan bar.
        theme.SetStylebox("panel", "Tree", new StyleBoxEmpty());
        theme.SetStylebox("focus", "Tree", new StyleBoxEmpty());
        theme.SetStylebox("selected", "Tree", SelectedRow(0.16f));
        theme.SetStylebox("selected_focus", "Tree", SelectedRow(0.22f));
        theme.SetStylebox("hovered", "Tree", Box(new Color(Palette.Accent, 0.07f), Colors.Transparent, 0, 4, 0f));
        theme.SetStylebox("cursor", "Tree", new StyleBoxEmpty());
        theme.SetStylebox("cursor_unfocused", "Tree", new StyleBoxEmpty());
        theme.SetColor("font_color", "Tree", Text);
        theme.SetColor("font_selected_color", "Tree", Colors.White);
        theme.SetColor("guide_color", "Tree", new Color(Palette.PanelBorder, 0.5f));
        theme.SetConstant("v_separation", "Tree", 8);
        theme.SetConstant("h_separation", "Tree", 8);
        theme.SetConstant("item_margin", "Tree", 18);
        theme.SetConstant("button_margin", "Tree", 4);
        theme.SetIcon("arrow", "Tree", UiAssets.IconAt("chevron-down", 14));
        theme.SetIcon("arrow_collapsed", "Tree", UiAssets.IconAt("chevron-right", 14));
        theme.SetConstant("icon_max_width", "Tree", 18);

        // Tooltips.
        theme.SetStylebox("panel", "TooltipPanel", UiAssets.Box(Patch.Panel, 10f, 6f));
        theme.SetColor("font_color", "TooltipLabel", Text);

        // Progress bar, sliders, scrollbars.
        theme.SetStylebox("background", "ProgressBar", Box(FieldBackground, Palette.PanelBorder, 1, 6, 0f));
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
        theme.SetStylebox("separator", "VSeparator", Line(new Color(Palette.PanelBorder, 0.8f), vertical: true));
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

    /// <summary>The selected Outline row: a soft cyan wash with a bright bar on the left.</summary>
    private static StyleBoxFlat SelectedRow(float wash)
    {
        var box = new StyleBoxFlat { BgColor = new Color(Palette.Accent, wash), BorderColor = UiKit.TitleColor, BorderWidthLeft = 3 };
        box.SetCornerRadiusAll(2);
        return box;
    }

    private static StyleBoxLine Line(Color color, bool vertical = false) => new() { Color = color, Thickness = 1, Vertical = vertical };
}
