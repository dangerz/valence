using System;
using System.Collections.Generic;
using Godot;

namespace Valence.Viewer;

/// <summary>
/// The shell of one system: a faint cloud, electron orbits (one electron per bonded system, up to 8)
/// and a name label. The nucleons themselves are drawn by <see cref="NucleonRenderer"/>.
/// </summary>
public partial class AtomView : Node3D
{
    private const int MaxElectrons = 8;

    private readonly List<(Node3D Spinner, float Speed)> _spinners = new();
    private readonly List<Node3D> _orbits = new();
    private MeshInstance3D? _cloud;
    private Label3D? _label;
    private Color _labelColor;
    private Color _tint;
    private float _labelRange = 70f;
    private float _opacity = 1f;
    private float _expansion;
    private bool _focused;
    private bool _dimmed;
    private bool _connected;
    private bool _labelNearMouse = true;
    private Color _labelBase = Colors.White;

    public AtomData? Data { get; private set; }

    public void Setup(AtomData data, SceneAssets assets)
    {
        Data = data;
        data.View = this;
        Position = data.Position;
        var tint = Palette.MoleculeHue(data.Molecule.Id);
        _tint = tint;

        _cloud = new MeshInstance3D
        {
            Mesh = assets.Cloud,
            MaterialOverride = assets.CloudMaterial,
            Scale = Vector3.One * data.Radius * 0.95f,
            CastShadow = GeometryInstance3D.ShadowCastingSetting.Off,
        };
        AddChild(_cloud);
        _cloud.SetInstanceShaderParameter("cloud_color", tint);

        var ringCount = Math.Clamp(1 + data.Valence / 3, 1, 3);
        var electrons = Math.Min(data.Valence, MaxElectrons);
        var orbitMaterial = assets.OrbitMaterial(tint);
        var seed = Palette.Hash01(data.Id);
        for (var ring = 0; ring < ringCount; ring++)
        {
            var radius = data.CoreRadius + 0.35f + 0.25f * ring;
            var tilt = 25f + 40f * Fract(seed * (ring + 1) * 7.13f);
            var pivot = new Node3D
            {
                Rotation = new Vector3(Mathf.DegToRad(tilt), Mathf.DegToRad(60f * ring + 360f * seed), 0f),
            };
            AddChild(pivot);
            _orbits.Add(pivot);
            pivot.AddChild(new MeshInstance3D
            {
                Mesh = assets.Orbit,
                MaterialOverride = orbitMaterial,
                Scale = Vector3.One * radius,
                CastShadow = GeometryInstance3D.ShadowCastingSetting.Off,
            });

            var spinner = new Node3D();
            pivot.AddChild(spinner);
            var onThisRing = 0;
            for (var e = ring; e < electrons; e += ringCount) onThisRing++;
            for (var e = 0; e < onThisRing; e++)
            {
                var angle = Mathf.Tau * e / onThisRing;
                spinner.AddChild(new MeshInstance3D
                {
                    Mesh = assets.Electron,
                    MaterialOverride = assets.ElectronMaterial,
                    Position = new Vector3(Mathf.Cos(angle) * radius, 0f, Mathf.Sin(angle) * radius),
                    CastShadow = GeometryInstance3D.ShadowCastingSetting.Off,
                });
            }
            _spinners.Add((spinner, (0.6f + 0.5f * ring) * (ring % 2 == 0 ? 1f : -1f)));
        }

        _labelColor = tint.Lightened(0.45f);
        _labelBase = _labelColor;
        _label = new Label3D
        {
            Text = data.ShortName,
            FontSize = 48,
            PixelSize = 0.008f,
            OutlineSize = 10,
            Billboard = BaseMaterial3D.BillboardModeEnum.Enabled,
            Modulate = _labelColor,
            Position = new Vector3(0f, data.Radius + 0.35f, 0f),
            VisibilityRangeEnd = 70f,
            VisibilityRangeEndMargin = 15f,
            VisibilityRangeFadeMode = GeometryInstance3D.VisibilityRangeFadeModeEnum.Self,
        };
        AddChild(_label);
    }

    /// <param name="focused">This system is the current focus.</param>
    /// <param name="dimmed">Another system is focused.</param>
    /// <param name="connected">Bonded to the focused system: glows yellow.</param>
    public void SetState(bool focused, bool dimmed, bool connected = false)
    {
        _focused = focused;
        _dimmed = dimmed;
        _connected = connected;
        _cloud?.SetInstanceShaderParameter("highlight", focused ? 1f : connected ? 0.6f : 0f);
        var cloud = connected ? Palette.Connection : _tint;
        _cloud?.SetInstanceShaderParameter("cloud_color", new Color(cloud.R, cloud.G, cloud.B, _opacity));
        if (_label != null)
        {
            _labelBase = focused ? Colors.White
                : connected ? Palette.Connection
                : dimmed ? _labelColor.Darkened(0.55f)
                : _labelColor;
            ApplyLabelAlpha(_labelNearMouse && IsInsideTree() ? LabelProximity.Factor(GetViewport(), _label.GlobalPosition) : 1f);
        }
    }

    private void ApplyLabelAlpha(float factor)
    {
        if (_label == null) return;
        var alpha = _opacity * factor;
        _label.Modulate = new Color(_labelBase.R, _labelBase.G, _labelBase.B, alpha);
        _label.OutlineModulate = new Color(0f, 0f, 0f, alpha);
    }

    /// <summary>1 shown, the quiet fade when quiet, 0 hidden (the whole atom disappears).</summary>
    public void SetOpacity(float opacity)
    {
        _opacity = opacity;
        Visible = opacity > 0f;
        foreach (var orbit in _orbits) orbit.Visible = _expansion < 0.05f && opacity >= 1f;
        SetState(_focused, _dimmed, _connected); // also re-applies the cloud color with the new opacity
    }

    /// <summary>Camera distance under which the system's name shows (smaller for big graphs).</summary>
    public void SetLabelRange(float range)
    {
        _labelRange = range;
        if (_label != null) _label.VisibilityRangeEnd = range;
    }

    /// <param name="pinned">Always show the name (focused, connected to the selection, simulated).</param>
    /// <param name="exclusive">A selection is active: hide names that aren't pinned.</param>
    /// <param name="mode">View ▸ Labels; Main passes OnClick during simulation so only pinned names show.</param>
    public void SetLabelPinned(bool pinned, bool exclusive, LabelMode mode = LabelMode.NearMouse)
    {
        if (_label == null) return;
        var all = mode == LabelMode.All;
        _label.VisibilityRangeEnd = pinned || all ? 0f : _labelRange;
        _label.Visible = pinned || all || (!exclusive && mode == LabelMode.NearMouse);
        // Near Mouse: unpinned names only show near the pointer.
        _labelNearMouse = !pinned && !exclusive && mode == LabelMode.NearMouse;
        ApplyLabelAlpha(_labelNearMouse && IsInsideTree() ? LabelProximity.Factor(GetViewport(), _label.GlobalPosition) : 1f);
    }

    /// <summary>0 = packed nucleus with orbits; 1 = opened to <paramref name="openRadius"/>.</summary>
    public void SetExpansion(float t, float openRadius)
    {
        if (Data == null) return;
        var radius = Mathf.Lerp(Data.Radius, Mathf.Max(openRadius, Data.Radius), t);
        if (_cloud != null) _cloud.Scale = Vector3.One * radius * 0.95f;
        _expansion = t;
        foreach (var orbit in _orbits) orbit.Visible = t < 0.05f && _opacity >= 1f;
        if (_label != null) _label.Position = new Vector3(0f, radius + 0.35f, 0f);
    }

    public override void _Process(double delta)
    {
        foreach (var (spinner, speed) in _spinners) spinner.RotateY(speed * (float)delta);
        if (_labelNearMouse && _label is { Visible: true }) ApplyLabelAlpha(LabelProximity.Factor(GetViewport(), _label.GlobalPosition));
    }

    private static float Fract(float value) => value - MathF.Floor(value);
}
