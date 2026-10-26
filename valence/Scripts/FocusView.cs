using System.Collections.Generic;
using System.Linq;
using Godot;

namespace Valence.Viewer;

/// <summary>Where a bond to another system leaves the opened atom.</summary>
public sealed class PortData
{
    public PortData(AtomData other, BondData bond, string text)
    {
        Other = other;
        Bond = bond;
        Text = text;
    }

    public AtomData Other { get; }
    public BondData Bond { get; }
    public string Text { get; }
    public Vector3 Position { get; set; }
    public MeshInstance3D? Mesh { get; set; }
    public Label3D? Label { get; set; }
}

/// <summary>
/// Ports on the rim of the opened atom, one per bonded system ("→ Shop.Data ×12"). Links to other
/// systems are drawn to these ports instead of across the scene. Port labels follow the same zoom
/// rule as class labels, except for systems connected to the current selection.
/// </summary>
public partial class FocusView : Node3D
{
    private const float PortPixelSize = 0.0006f;

    private readonly List<PortData> _ports = new();
    private readonly Dictionary<AtomData, PortData> _portByAtom = new();
    private readonly HashSet<AtomData> _pinned = new();
    private AtomData? _atom;
    private float _radius;
    private float _fade;
    private float _zoomRange = 14f;
    private bool _exclusive;

    public IReadOnlyList<PortData> Ports => _ports;

    public Vector3? PortPosition(AtomData other) => _portByAtom.TryGetValue(other, out var port) ? port.Position : null;

    public void Open(AtomData atom, GraphModel model, SceneAssets assets, float radius, float zoomRange)
    {
        Close();
        _atom = atom;
        _radius = radius;
        _zoomRange = zoomRange;

        foreach (var bond in model.Bonds.Where(b => b.A == atom || b.B == atom))
        {
            var other = bond.Other(atom);
            var outgoing = bond.WeightFrom(atom);
            var incoming = bond.WeightFrom(other);
            var text = outgoing > 0 && incoming > 0 ? $"⇄ {other.ShortName}  {outgoing} out · {incoming} in"
                : outgoing > 0 ? $"→ {other.ShortName}  ×{outgoing}"
                : $"← {other.ShortName}  ×{incoming}";
            var color = bond.Covalent ? Palette.Covalent : Palette.Ionic;
            var port = new PortData(other, bond, text)
            {
                Mesh = new MeshInstance3D
                {
                    Mesh = assets.Port,
                    MaterialOverride = assets.PortMaterial(color),
                    Scale = Vector3.One * 0.22f,
                    CastShadow = GeometryInstance3D.ShadowCastingSetting.Off,
                },
                Label = new Label3D
                {
                    Text = text,
                    FontSize = 32,
                    PixelSize = PortPixelSize,
                    FixedSize = true,
                    OutlineSize = 10,
                    Billboard = BaseMaterial3D.BillboardModeEnum.Enabled,
                    NoDepthTest = true,
                    Modulate = color.Lightened(0.3f),
                    VisibilityRangeEndMargin = 3f,
                    VisibilityRangeFadeMode = GeometryInstance3D.VisibilityRangeFadeModeEnum.Self,
                },
            };
            AddChild(port.Mesh);
            AddChild(port.Label);
            _ports.Add(port);
            _portByAtom[other] = port;
        }

        UpdatePorts();
        UpdateLayout(0f);
    }

    public void Close()
    {
        foreach (var child in GetChildren()) child.QueueFree();
        _ports.Clear();
        _portByAtom.Clear();
        _atom = null;
    }

    /// <summary>Ports sit on the opened rim, pointing at the system they lead to.</summary>
    public void UpdatePorts()
    {
        if (_atom == null) return;
        foreach (var port in _ports)
        {
            var direction = port.Other.Position - _atom.Position;
            direction = direction.LengthSquared() < 1e-6f ? Vector3.Up : direction.Normalized();
            port.Position = _atom.Position + direction * (_radius + 0.4f);
            if (port.Mesh != null) port.Mesh.Position = port.Position;
            if (port.Label != null) port.Label.Position = port.Position + Vector3.Up * 0.35f;
        }
    }

    /// <param name="t">Opening progress, 0..1. Ports fade in over the second half.</param>
    public void UpdateLayout(float t)
    {
        _fade = Mathf.Clamp((t - 0.5f) * 2f, 0f, 1f);
        Visible = t > 0.01f;
        ApplyLabels();
    }

    /// <summary>Systems connected to the selection keep their port labels; with a selection, others hide.</summary>
    public void SetPinnedAtoms(IEnumerable<AtomData> atoms, bool exclusive)
    {
        _pinned.Clear();
        _pinned.UnionWith(atoms);
        _exclusive = exclusive;
        ApplyLabels();
    }

    /// <summary>Re-applies hidden/quiet to ports after visibility changes.</summary>
    public void RefreshVisibility() => ApplyLabels();

    private void ApplyLabels()
    {
        foreach (var port in _ports)
        {
            if (port.Mesh != null) port.Mesh.Visible = _fade > 0.01f && !port.Other.IsHidden;
            if (port.Label == null) continue;
            var pinned = _pinned.Contains(port.Other);
            var alpha = (pinned || !_exclusive ? _fade : 0f) * port.Other.Opacity;
            port.Label.VisibilityRangeEnd = pinned ? 0f : _zoomRange;
            port.Label.Visible = alpha > 0.01f;
            var color = port.Label.Modulate;
            port.Label.Modulate = new Color(color.R, color.G, color.B, alpha);
            port.Label.OutlineModulate = new Color(0f, 0f, 0f, alpha);
        }
    }
}
