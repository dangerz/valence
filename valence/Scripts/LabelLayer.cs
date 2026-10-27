using System.Collections.Generic;
using System.Linq;
using Godot;

namespace Valence.Viewer;

/// <summary>
/// Class-name labels. Normally two kinds:
///  - <b>opened</b>: every class in the opened system, visible only as the camera gets close;
///  - <b>pinned</b>: the selection (or search result) and everything connected to it, always visible
///    wherever those classes are. While a selection is active, only pinned labels show.
/// During a simulation every label hides, and a class's label appears only once the flow reaches it.
/// </summary>
public partial class LabelLayer : Node3D
{
    private const float PixelSize = 0.0005f;

    private readonly Dictionary<NodeData, Label3D> _labels = new();
    private readonly HashSet<NodeData> _opened = new();
    private readonly HashSet<NodeData> _pinned = new();
    private readonly HashSet<NodeData> _revealed = new();
    private NodeData? _anchor;
    private bool _exclusive;
    private bool _simulation;
    private LabelMode _mode = LabelMode.NearMouse;
    private float _openFade = 1f;
    private float _zoomRange = 14f;

    /// <param name="zoomRange">Camera distance under which an opened class's label appears.</param>
    public void SetOpened(IEnumerable<NodeData> nodes, float zoomRange)
    {
        _opened.Clear();
        _opened.UnionWith(nodes);
        _zoomRange = zoomRange;
        Sync();
    }

    public void ClearOpened()
    {
        _opened.Clear();
        Sync();
    }

    /// <summary>Opened-class labels fade in with the opening animation (0..1).</summary>
    public void SetOpenFade(float fade)
    {
        _openFade = fade;
        foreach (var (node, label) in _labels) Apply(node, label);
    }

    /// <param name="exclusive">True while something is selected: hide every unpinned label.</param>
    public void SetPinned(IEnumerable<NodeData> nodes, NodeData? anchor, bool exclusive)
    {
        _pinned.Clear();
        _pinned.UnionWith(nodes);
        _anchor = anchor;
        _exclusive = exclusive;
        Sync();
    }

    /// <summary>View ▸ Labels (ignored during simulation).</summary>
    public void SetMode(LabelMode mode)
    {
        _mode = mode;
        foreach (var (node, label) in _labels) Apply(node, label);
    }

    /// <summary>Entering or leaving simulation. Entering hides every label until <see cref="Reveal"/>.</summary>
    public void SetSimulation(bool on)
    {
        _simulation = on;
        _revealed.Clear();
        Sync();
    }

    /// <summary>The flow reached this class: show its label (simulation only).</summary>
    public void Reveal(NodeData node)
    {
        if (!_simulation || !_revealed.Add(node)) return;
        if (!_labels.TryGetValue(node, out var label)) label = Create(node);
        label.Position = LabelPosition(node);
        Apply(node, label);
    }

    public void UpdatePositions()
    {
        foreach (var (node, label) in _labels) label.Position = LabelPosition(node);
    }

    private static Vector3 LabelPosition(NodeData node) => node.WorldPosition + Vector3.Up * (node.Radius + 0.15f);

    private void Sync()
    {
        var wanted = new HashSet<NodeData>(_opened);
        wanted.UnionWith(_pinned);
        wanted.UnionWith(_revealed);

        foreach (var node in _labels.Keys.Where(n => !wanted.Contains(n)).ToList())
        {
            _labels[node].QueueFree();
            _labels.Remove(node);
            _nearMouse.Remove(node);
            _baseColor.Remove(node);
        }
        foreach (var node in wanted)
        {
            if (!_labels.ContainsKey(node)) Create(node);
        }

        UpdatePositions();
        foreach (var (node, label) in _labels) Apply(node, label);
    }

    private Label3D Create(NodeData node)
    {
        var label = new Label3D
        {
            Text = node.Info.Name,
            FontSize = 32,
            PixelSize = PixelSize,
            FixedSize = true,
            OutlineSize = 10,
            Billboard = BaseMaterial3D.BillboardModeEnum.Enabled,
            NoDepthTest = true,
            RenderPriority = 1,
            VisibilityRangeEndMargin = 3f,
            VisibilityRangeFadeMode = GeometryInstance3D.VisibilityRangeFadeModeEnum.Self,
        };
        AddChild(label);
        _labels[node] = label;
        return label;
    }

    private void Apply(NodeData node, Label3D label)
    {
        var color = node == _anchor ? Colors.White : Palette.NodeKind(node.Info.Kind).Lightened(0.35f);
        float alpha;
        if (_simulation)
        {
            // Only classes the flow has reached (and the selection itself) are labeled.
            label.VisibilityRangeEnd = 0f;
            alpha = _revealed.Contains(node) || node == _anchor ? 1f : 0f;
        }
        else if (_pinned.Contains(node))
        {
            label.VisibilityRangeEnd = 0f; // always visible
            alpha = _opened.Contains(node) ? _openFade : 1f;
        }
        else if (_mode == LabelMode.OnClick || (_exclusive && _mode != LabelMode.All))
        {
            alpha = 0f; // only clicked things are labeled / a selection hides the rest
        }
        else if (_mode == LabelMode.All)
        {
            label.VisibilityRangeEnd = 0f;
            alpha = _openFade;
        }
        else
        {
            label.VisibilityRangeEnd = _zoomRange;
            alpha = _openFade;
        }
        alpha *= node.Opacity; // hidden → no label; quiet → faded

        // Near Mouse: unpinned labels only show near the pointer; _Process keeps them updated.
        var nearMouse = !_simulation && !_pinned.Contains(node) && !_exclusive && _mode == LabelMode.NearMouse;
        _baseColor[node] = new Color(color.R, color.G, color.B, alpha);
        if (nearMouse) _nearMouse.Add(node);
        else _nearMouse.Remove(node);
        SetAlpha(label, _baseColor[node], nearMouse && IsInsideTree() ? LabelProximity.Factor(GetViewport(), label.GlobalPosition) : 1f);
    }

    private readonly Dictionary<NodeData, Color> _baseColor = new();
    private readonly HashSet<NodeData> _nearMouse = new();

    public override void _Process(double delta)
    {
        if (_nearMouse.Count == 0) return;
        var viewport = GetViewport();
        foreach (var node in _nearMouse)
        {
            if (!_labels.TryGetValue(node, out var label) || !_baseColor.TryGetValue(node, out var color)) continue;
            SetAlpha(label, color, LabelProximity.Factor(viewport, label.GlobalPosition));
        }
    }

    private static void SetAlpha(Label3D label, Color color, float factor)
    {
        var alpha = color.A * factor;
        label.Visible = alpha > 0.01f;
        label.Modulate = new Color(color.R, color.G, color.B, alpha);
        label.OutlineModulate = new Color(0f, 0f, 0f, alpha);
    }
}
