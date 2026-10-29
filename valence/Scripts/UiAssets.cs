using System;
using System.Collections.Generic;
using Godot;

namespace Valence.Viewer;

/// <summary>The nine-patch panels in images/ui/panels.png.</summary>
public enum Patch
{
    /// <summary>Subtle double border: panels, inputs, cards, menus.</summary>
    Panel,

    /// <summary>Blue border: hover, pills, the selected tab, dialogs.</summary>
    Accent,

    /// <summary>Cyan border: pressed / active.</summary>
    Active,

    /// <summary>Border only, transparent inside: focus rings and the 3D view's frame.</summary>
    Focus,
}

/// <summary>
/// The UI sprite sheets (built by tools/build_ui_sheets.py from images/ui-source/): icons on a
/// 128px grid, given mipmaps at load so they stay smooth at 16–24px, and nine-patch panels scaled
/// down so their borders come out at about 1.5 UI pixels.
/// </summary>
public static class UiAssets
{
    private const string IconsPath = "res://images/ui/icons.png";
    private const string PanelsPath = "res://images/ui/panels.png";
    private const int IconCell = 128;
    private const int IconsPerRow = 8;

    /// <summary>Panels are drawn at this fraction of their size on the sheet.</summary>
    private const float PatchScale = 0.35f;

    /// <summary>Corner and border, in sheet pixels: the part of a panel that never stretches.</summary>
    private const float PatchMarginOnSheet = 30f;

    /// <summary>Same order as ICON_NAMES in tools/build_ui_sheets.py.</summary>
    private static readonly string[] IconNames =
    {
        "logo", "menu", "search", "star", "file", "eye", "eye-off", "folder",
        "globe", "chevron-right", "chevron-down", "branch", "layers", "filter", "focus", "grid",
        "play", "more", "database", "cube", "link", "cycle", "users", "graph",
        "pointer", "orbit", "move", "zoom-in", "info", "mouse", "close", "copy",
        "eye-quiet", "pause", "stop", "more-vertical", "plus-square", "minus-square", "clock", "dot",
        "camera", "bolt", "arrow-down", "arrow-up", "trash", "pencil", "boundary", "file-read",
        "file-write", "terminal", "sliders", "chip", "code",
    };

    /// <summary>Where each panel sits on panels.png (printed by the build script).</summary>
    private static readonly Dictionary<Patch, Rect2I> PatchRects = new()
    {
        [Patch.Panel] = new Rect2I(4, 4, 481, 304),
        [Patch.Accent] = new Rect2I(503, 4, 491, 304),
        [Patch.Active] = new Rect2I(4, 316, 489, 304),
        [Patch.Focus] = new Rect2I(503, 316, 491, 299),
    };

    private static readonly Dictionary<string, Texture2D> Icons = new(StringComparer.Ordinal);
    private static readonly Dictionary<Patch, Texture2D> Patches = new();
    private static readonly Dictionary<(string, int), Texture2D> SizedIcons = new();
    private static Image? _iconImage;
    private static Texture2D? _iconAtlas;
    private static Image? _panelSheet;

    public static Texture2D Icon(string name)
    {
        if (Icons.TryGetValue(name, out var cached)) return cached;
        var index = Array.IndexOf(IconNames, name);
        var atlas = IconAtlas();
        Texture2D texture;
        if (index < 0 || atlas == null)
        {
            GD.PushWarning($"[valence] missing UI icon '{name}'");
            texture = Blank();
        }
        else
        {
            texture = new AtlasTexture
            {
                Atlas = atlas,
                Region = new Rect2(index % IconsPerRow * IconCell, index / IconsPerRow * IconCell, IconCell, IconCell),
                FilterClip = true,
            };
        }
        Icons[name] = texture;
        return texture;
    }

    /// <summary>
    /// An icon as its own small texture, for places Godot draws at the texture's native size (the
    /// Tree's expand arrows) instead of scaling it.
    /// </summary>
    public static Texture2D IconAt(string name, int size)
    {
        if (SizedIcons.TryGetValue((name, size), out var cached)) return cached;
        var index = Array.IndexOf(IconNames, name);
        IconAtlas();
        Texture2D texture;
        if (index < 0 || _iconImage == null)
        {
            texture = Blank();
        }
        else
        {
            var image = _iconImage.GetRegion(new Rect2I(index % IconsPerRow * IconCell, index / IconsPerRow * IconCell, IconCell, IconCell));
            while (image.GetWidth() >= size * 4) image.ShrinkX2();
            image.Resize(size, size, Image.Interpolation.Cubic);
            texture = ImageTexture.CreateFromImage(image);
        }
        SizedIcons[(name, size)] = texture;
        return texture;
    }

    /// <summary>A nine-patch stylebox with the same content margin on every side.</summary>
    public static StyleBoxTexture Box(Patch patch, float content = 10f) => Box(patch, content, content);

    public static StyleBoxTexture Box(Patch patch, float horizontal, float vertical)
    {
        var box = new StyleBoxTexture { Texture = PatchTexture(patch) };
        box.SetTextureMarginAll(PatchMarginOnSheet * PatchScale);
        box.SetContentMargin(Side.Left, horizontal);
        box.SetContentMargin(Side.Right, horizontal);
        box.SetContentMargin(Side.Top, vertical);
        box.SetContentMargin(Side.Bottom, vertical);
        return box;
    }

    private static Texture2D? IconAtlas()
    {
        if (_iconAtlas != null) return _iconAtlas;
        _iconImage = LoadImage(IconsPath);
        if (_iconImage == null) return null;
        var image = (Image)_iconImage.Duplicate();
        image.GenerateMipmaps();
        _iconAtlas = ImageTexture.CreateFromImage(image);
        return _iconAtlas;
    }

    private static Texture2D PatchTexture(Patch patch)
    {
        if (Patches.TryGetValue(patch, out var cached)) return cached;
        _panelSheet ??= LoadImage(PanelsPath);
        Texture2D texture;
        if (_panelSheet == null)
        {
            texture = Blank();
        }
        else
        {
            var rect = PatchRects[patch];
            var image = _panelSheet.GetRegion(rect);
            // Halve with a box filter first, then the rest of the way: smoother than one big jump.
            image.ShrinkX2();
            image.Resize(
                Math.Max(1, (int)Math.Round(rect.Size.X * PatchScale)),
                Math.Max(1, (int)Math.Round(rect.Size.Y * PatchScale)),
                Image.Interpolation.Cubic);
            texture = ImageTexture.CreateFromImage(image);
        }
        Patches[patch] = texture;
        return texture;
    }

    private static Image? LoadImage(string path)
    {
        if (!ResourceLoader.Exists(path))
        {
            GD.PushWarning($"[valence] missing UI sheet {path}");
            return null;
        }
        var image = GD.Load<Texture2D>(path).GetImage();
        if (image == null) return null;
        if (image.IsCompressed()) image.Decompress();
        image.Convert(Image.Format.Rgba8);
        return image;
    }

    private static Texture2D Blank() => ImageTexture.CreateFromImage(Image.CreateEmpty(1, 1, false, Image.Format.Rgba8));
}
