using System.Collections.Generic;
using System.Linq;
using Godot;

namespace Valence.Viewer;

/// <summary>
/// Class-name labels. Two kinds:
///  - <b>opened</b>: every class in the opened system, visible only as the camera gets close;
///  - <b>pinned</b>: the selection (or search result) and everything connected to it, always visible
///    wherever those classes are. While a selection is active, only pinned labels show.
/// </summary>
public partial class LabelLayer : Node3D
{
    private const float PixelSize = 0.0005f;

    private readonly Dictionary<NodeData, Label3D> _labels = new();
    private readonly HashSet<NodeData> _opened = new();
    private readonly HashSet<NodeData> _pinned = new();
    private NodeData? _anchor;
    private bool _exclusive;
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

    public void UpdatePositions()
    {
        foreach (var (node, label) in _labels) label.Position = node.WorldPosition + Vector3.Up * (node.Radius + 0.15f);
    }

    private void Sync()
    {
        var wanted = new HashSet<NodeData>(_opened);
        wanted.UnionWith(_pinned);

        foreach (var node in _labels.Keys.Where(n => !wanted.Contains(n)).ToList())
        {
            _labels[node].QueueFree();
            _labels.Remove(node);
        }
        foreach (var node in wanted)
        {
            if (_labels.ContainsKey(node)) continue;
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
        }

        UpdatePositions();
        foreach (var (node, label) in _labels) Apply(node, label);
    }

    private void Apply(NodeData node, Label3D label)
    {
        var color = node == _anchor ? Colors.White : Palette.NodeKind(node.Info.Kind).Lightened(0.35f);
        float alpha;
        if (_pinned.Contains(node))
        {
            label.VisibilityRangeEnd = 0f; // always visible
            alpha = _opened.Contains(node) ? _openFade : 1f;
        }
        else if (_exclusive)
        {
            alpha = 0f;
        }
        else
        {
            label.VisibilityRangeEnd = _zoomRange;
            alpha = _openFade;
        }
        label.Visible = alpha > 0.01f;
        label.Modulate = new Color(color.R, color.G, color.B, alpha);
        label.OutlineModulate = new Color(0f, 0f, 0f, alpha);
    }
}
