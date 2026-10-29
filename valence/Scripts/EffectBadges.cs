using System;
using System.Collections.Generic;
using System.Linq;
using Godot;

namespace Valence.Viewer;

/// <summary>
/// The Boundary lens's badges: under each class whose own code reaches outside the program, a small
/// icon per kind (network, files, processes…), up to three side by side. Only classes that make the
/// call themselves get one; classes that only reach it through calls are colored, not badged. While a
/// timeline frame is shown, the badges show that commit's calls (<see cref="SetFlags"/>).
/// </summary>
public partial class EffectBadges : Node3D
{
    private const int MaxPerClass = 3;
    private const int IconSize = 64;
    private const float PixelSize = 0.00024f;
    private const float Spacing = 76f;  // icon pixels between badge centers

    private static readonly Dictionary<EffectFlags, Texture2D> Textures = new();

    private readonly Dictionary<NodeData, List<Sprite3D>> _badges = new();
    private readonly Dictionary<NodeData, EffectFlags> _flags = new();
    private List<NodeData> _nodes = new();
    private bool _shown;

    public void Build(GraphModel model)
    {
        foreach (var node in _badges.Keys.ToList()) Remove(node);
        _nodes = model.Nodes.Values.ToList();
        SetFlags(node => node.DirectEffects);
    }

    /// <summary>
    /// Which kinds each class shows: normally its own effects, or a timeline frame's while scrubbing.
    /// Only classes whose kinds changed get new sprites.
    /// </summary>
    public void SetFlags(Func<NodeData, EffectFlags> flagsOf)
    {
        foreach (var node in _nodes)
        {
            var flags = flagsOf(node);
            if (_flags.GetValueOrDefault(node) == flags) continue;
            Remove(node);
            if (flags == EffectFlags.None) continue;
            _flags[node] = flags;
            _badges[node] = Create(flags);
        }
        Refresh();
    }

    private void Remove(NodeData node)
    {
        if (_badges.Remove(node, out var sprites))
        {
            foreach (var sprite in sprites) sprite.QueueFree();
        }
        _flags.Remove(node);
    }

    private List<Sprite3D> Create(EffectFlags flags)
    {
        var kinds = Boundary.In(flags).Take(MaxPerClass).ToList();
        var sprites = new List<Sprite3D>(kinds.Count);
        for (var i = 0; i < kinds.Count; i++)
        {
            var sprite = new Sprite3D
            {
                Texture = WhiteIcon(kinds[i]),
                PixelSize = PixelSize,
                FixedSize = true,
                Billboard = BaseMaterial3D.BillboardModeEnum.Enabled,
                NoDepthTest = true,
                Shaded = false,
                RenderPriority = 2,
                TextureFilter = BaseMaterial3D.TextureFilterEnum.Linear,
                Offset = new Vector2((i - (kinds.Count - 1) / 2f) * Spacing, 0f),
                Modulate = kinds[i].Color,
                Visible = false,
            };
            AddChild(sprite);
            sprites.Add(sprite);
        }
        return sprites;
    }

    /// <summary>Shown only while the Boundary lens is on.</summary>
    public void SetShown(bool shown)
    {
        if (_shown == shown) return;
        _shown = shown;
        Refresh();
    }

    /// <summary>After nodes move (systems opening, layout).</summary>
    public void UpdatePositions()
    {
        if (!_shown) return;
        foreach (var (node, sprites) in _badges)
        {
            var position = Below(node);
            foreach (var sprite in sprites) sprite.Position = position;
        }
    }

    /// <summary>After visibility changes: hidden classes lose their badges, quiet ones fade.</summary>
    public void Refresh()
    {
        foreach (var (node, sprites) in _badges)
        {
            var visible = _shown && !node.IsHidden;
            var position = Below(node);
            foreach (var sprite in sprites)
            {
                sprite.Visible = visible;
                if (!visible) continue;
                sprite.Position = position;
                var color = sprite.Modulate;
                sprite.Modulate = new Color(color.R, color.G, color.B, node.Opacity);
            }
        }
    }

    private static Vector3 Below(NodeData node) => node.WorldPosition + Vector3.Down * (node.Radius + 0.12f);

    /// <summary>The kind's icon in white (keeping its outline darker), so a modulate gives it the kind's color.</summary>
    public static Texture2D WhiteIcon(EffectKind kind)
    {
        if (Textures.TryGetValue(kind.Flag, out var cached)) return cached;
        var image = UiAssets.IconAt(kind.Icon, IconSize).GetImage();
        if (image != null)
        {
            image.Convert(Image.Format.Rgba8);
            for (var y = 0; y < image.GetHeight(); y++)
            {
                for (var x = 0; x < image.GetWidth(); x++)
                {
                    var pixel = image.GetPixel(x, y);
                    var level = Mathf.Clamp(pixel.Luminance / 0.8f, 0f, 1f);
                    image.SetPixel(x, y, new Color(level, level, level, pixel.A));
                }
            }
        }
        var texture = image != null ? ImageTexture.CreateFromImage(image) : UiAssets.IconAt(kind.Icon, IconSize);
        Textures[kind.Flag] = texture;
        return texture;
    }
}
