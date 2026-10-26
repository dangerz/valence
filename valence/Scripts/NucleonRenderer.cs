using System;
using System.Collections.Generic;
using Godot;

namespace Valence.Viewer;

/// <summary>Every class in the graph as one MultiMesh instance, so large repos stay cheap to draw.</summary>
public partial class NucleonRenderer : MultiMeshInstance3D
{
    private readonly List<NodeData> _nodes = new();
    private MultiMesh? _multimesh;

    public void Build(GraphModel model, SceneAssets assets)
    {
        _nodes.Clear();
        foreach (var atom in model.AtomList)
        {
            foreach (var node in atom.Nodes)
            {
                node.Index = _nodes.Count;
                _nodes.Add(node);
            }
        }

        _multimesh = new MultiMesh
        {
            TransformFormat = MultiMesh.TransformFormatEnum.Transform3D,
            UseColors = true,
            Mesh = assets.Nucleon,
        };
        _multimesh.InstanceCount = _nodes.Count;
        Multimesh = _multimesh;
        MaterialOverride = assets.NucleonMaterial;
        CastShadow = ShadowCastingSetting.Off;

        foreach (var node in _nodes) Place(node);
    }

    public void UpdateAtom(AtomData atom)
    {
        foreach (var node in atom.Nodes) Place(node);
    }

    public void UpdateAll()
    {
        foreach (var node in _nodes) Place(node);
    }

    /// <summary>rgb = color, alpha = glow.</summary>
    public void ApplyColors(Func<NodeData, Color> colorOf)
    {
        if (_multimesh == null) return;
        foreach (var node in _nodes) _multimesh.SetInstanceColor(node.Index, colorOf(node));
    }

    private void Place(NodeData node)
    {
        if (_multimesh == null) return;
        var r = node.IsHidden ? 0f : node.Radius; // hidden: collapse the instance
        var basis = new Basis(new Vector3(r, 0f, 0f), new Vector3(0f, r, 0f), new Vector3(0f, 0f, r));
        _multimesh.SetInstanceTransform(node.Index, new Transform3D(basis, node.WorldPosition));
    }
}
