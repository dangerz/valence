using System;
using Godot;

namespace Valence.Viewer;

/// <summary>Small building blocks shared by the panels, so they all look the same.</summary>
public static class UiKit
{
    /// <summary>Behind and between the panels (the gutters).</summary>
    public static readonly Color AppBackground = new(0.031f, 0.071f, 0.11f);

    /// <summary>Panel titles ("OUTLINE", "INSPECTOR").</summary>
    public static readonly Color TitleColor = new(0.36f, 0.82f, 0.96f);

    public static readonly Color Text = new(0.88f, 0.91f, 0.95f);

    private static FontVariation? _titleFont;
    private static FontVariation? _boldFont;

    public static TextureRect IconRect(string icon, float size, Color? modulate = null) => new()
    {
        Texture = UiAssets.Icon(icon),
        ExpandMode = TextureRect.ExpandModeEnum.IgnoreSize,
        StretchMode = TextureRect.StretchModeEnum.KeepAspectCentered,
        CustomMinimumSize = new Vector2(size, size),
        SizeFlagsVertical = Control.SizeFlags.ShrinkCenter,
        Modulate = modulate ?? Colors.White,
        MouseFilter = Control.MouseFilterEnum.Ignore,
    };

    /// <summary>A square button showing just an icon.</summary>
    public static Button IconButton(string icon, string tooltip, Action? pressed = null, float size = 32f, bool flat = false)
    {
        var button = new Button
        {
            Icon = UiAssets.Icon(icon),
            TooltipText = tooltip,
            ExpandIcon = true,
            IconAlignment = HorizontalAlignment.Center,
            CustomMinimumSize = new Vector2(size, size),
            FocusMode = Control.FocusModeEnum.None,
            Flat = flat,
        };
        if (pressed != null) button.Pressed += pressed;
        return button;
    }

    /// <summary>A button with an icon and text (toolbar, tabs).</summary>
    public static Button TextButton(string icon, string text, string tooltip, Action? pressed = null)
    {
        var button = new Button
        {
            Icon = UiAssets.Icon(icon),
            Text = text,
            TooltipText = tooltip,
            FocusMode = Control.FocusModeEnum.None,
            CustomMinimumSize = new Vector2(0f, 34f),
        };
        if (pressed != null) button.Pressed += pressed;
        return button;
    }

    /// <summary>An icon-only menu button ("⋯").</summary>
    public static MenuButton IconMenu(string icon, string tooltip, float size = 28f)
    {
        var menu = new MenuButton
        {
            Icon = UiAssets.Icon(icon),
            TooltipText = tooltip,
            ExpandIcon = true,
            IconAlignment = HorizontalAlignment.Center,
            CustomMinimumSize = new Vector2(size, size),
            Flat = true,
        };
        return menu;
    }

    /// <summary>"OUTLINE": upper case, letter-spaced, cyan.</summary>
    public static Label Title(string text)
    {
        var label = new Label { Text = text.ToUpperInvariant(), VerticalAlignment = VerticalAlignment.Center };
        label.AddThemeFontOverride("font", TitleFont());
        label.AddThemeFontSizeOverride("font_size", 15);
        label.AddThemeColorOverride("font_color", TitleColor);
        return label;
    }

    /// <summary>A panel header: icon, title, then whatever the caller adds on the right.</summary>
    public static HBoxContainer Header(string icon, Label title)
    {
        var row = new HBoxContainer();
        row.AddThemeConstantOverride("separation", 8);
        row.AddChild(IconRect(icon, 20f));
        row.AddChild(title);
        return row;
    }

    public static Control Spacer() => new() { SizeFlagsHorizontal = Control.SizeFlags.ExpandFill, MouseFilter = Control.MouseFilterEnum.Ignore };

    /// <summary>A rounded count ("124").</summary>
    public static PanelContainer Badge(out Label label, string text = "")
    {
        var badge = new PanelContainer { SizeFlagsVertical = Control.SizeFlags.ShrinkCenter };
        var style = new StyleBoxFlat { BgColor = new Color(Palette.Accent, 0.18f), BorderColor = new Color(Palette.Accent, 0.45f) };
        style.SetBorderWidthAll(1);
        style.SetCornerRadiusAll(8);
        style.SetContentMargin(Side.Left, 8f);
        style.SetContentMargin(Side.Right, 8f);
        style.SetContentMargin(Side.Top, 1f);
        style.SetContentMargin(Side.Bottom, 1f);
        badge.AddThemeStyleboxOverride("panel", style);
        label = new Label { Text = text, HorizontalAlignment = HorizontalAlignment.Center };
        label.AddThemeFontSizeOverride("font_size", 12);
        label.AddThemeColorOverride("font_color", Colors.White);
        badge.AddChild(label);
        return badge;
    }

    /// <summary>A search box: magnifier on the left, a borderless field filling the rest.</summary>
    public static PanelContainer SearchField(string placeholder, out LineEdit edit)
    {
        var box = new PanelContainer();
        box.AddThemeStyleboxOverride("panel", UiAssets.Box(Patch.Panel, 10f, 4f));
        var row = new HBoxContainer();
        row.AddThemeConstantOverride("separation", 6);
        box.AddChild(row);
        row.AddChild(IconRect("search", 18f, new Color(1f, 1f, 1f, 0.8f)));
        edit = new LineEdit
        {
            PlaceholderText = placeholder,
            ClearButtonEnabled = true,
            Flat = true,
            SizeFlagsHorizontal = Control.SizeFlags.ExpandFill,
        };
        edit.AddThemeStyleboxOverride("focus", new StyleBoxEmpty());
        row.AddChild(edit);
        return box;
    }

    /// <summary>A title-bar pill: icon and text ("main", "66861cc6").</summary>
    public static PanelContainer Pill(string icon, out Label label)
    {
        var pill = new PanelContainer { SizeFlagsVertical = Control.SizeFlags.ShrinkCenter };
        pill.AddThemeStyleboxOverride("panel", UiAssets.Box(Patch.Panel, 12f, 5f));
        var row = new HBoxContainer();
        row.AddThemeConstantOverride("separation", 8);
        pill.AddChild(row);
        row.AddChild(IconRect(icon, 18f));
        label = new Label { VerticalAlignment = VerticalAlignment.Center };
        label.AddThemeFontSizeOverride("font_size", 14);
        row.AddChild(label);
        return pill;
    }

    /// <summary>An inspector stat: icon, big number, caption.</summary>
    public static PanelContainer Card(string icon, string caption, out Label value)
    {
        var card = new PanelContainer { SizeFlagsHorizontal = Control.SizeFlags.ExpandFill };
        card.AddThemeStyleboxOverride("panel", UiAssets.Box(Patch.Panel, 12f, 8f));
        var row = new HBoxContainer();
        row.AddThemeConstantOverride("separation", 12);
        card.AddChild(row);
        row.AddChild(IconRect(icon, 24f));
        var column = new VBoxContainer();
        column.AddThemeConstantOverride("separation", 0);
        row.AddChild(column);
        value = new Label { Text = "0" };
        value.AddThemeFontOverride("font", BoldFont());
        value.AddThemeFontSizeOverride("font_size", 18);
        value.AddThemeColorOverride("font_color", Colors.White);
        column.AddChild(value);
        var label = new Label { Text = caption };
        label.AddThemeFontSizeOverride("font_size", 12);
        label.AddThemeColorOverride("font_color", Palette.MutedText);
        column.AddChild(label);
        return card;
    }

    /// <summary>An app-colored frame around a panel: the gaps between panels.</summary>
    public static PanelContainer Gutter(Control child, float left, float top, float right, float bottom)
    {
        var gutter = new PanelContainer { MouseFilter = Control.MouseFilterEnum.Ignore };
        var style = new StyleBoxFlat { BgColor = AppBackground };
        style.SetContentMargin(Side.Left, left);
        style.SetContentMargin(Side.Top, top);
        style.SetContentMargin(Side.Right, right);
        style.SetContentMargin(Side.Bottom, bottom);
        gutter.AddThemeStyleboxOverride("panel", style);
        gutter.AddChild(child);
        return gutter;
    }

    /// <summary>A flat strip in the app color (title bar, status bar), with a hairline on one edge.</summary>
    public static StyleBoxFlat Strip(float horizontal, float vertical, bool lineTop = false, bool lineBottom = false)
    {
        var style = new StyleBoxFlat { BgColor = AppBackground, BorderColor = new Color(Palette.PanelBorder, 0.6f) };
        if (lineTop) style.BorderWidthTop = 1;
        if (lineBottom) style.BorderWidthBottom = 1;
        style.SetContentMargin(Side.Left, horizontal);
        style.SetContentMargin(Side.Right, horizontal);
        style.SetContentMargin(Side.Top, vertical);
        style.SetContentMargin(Side.Bottom, vertical);
        return style;
    }

    public static FontVariation TitleFont() => _titleFont ??= new FontVariation
    {
        BaseFont = ThemeDB.FallbackFont,
        VariationEmbolden = 0.6f,
        SpacingGlyph = 1,
    };

    public static FontVariation BoldFont() => _boldFont ??= new FontVariation
    {
        BaseFont = ThemeDB.FallbackFont,
        VariationEmbolden = 0.8f,
    };

    /// <summary>The wordmark next to the logo: wide letter spacing.</summary>
    public static FontVariation WordmarkFont(int spacing) => new()
    {
        BaseFont = ThemeDB.FallbackFont,
        VariationEmbolden = 0.3f,
        SpacingGlyph = spacing,
    };
}
