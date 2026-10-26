using System;
using System.Collections.Generic;
using Godot;

namespace Valence.Viewer;

/// <summary>
/// One tube per pair of bonded systems. Thickness = number of dependencies.
/// Ionic (cyan) = one-way dependency; covalent (amber) = the two systems depend on each other.
/// </summary>
public partial class BondRenderer : Node3D
{
    private readonly List<BondData> _bonds = new();

    public void Build(GraphModel model, SceneAssets assets)
    {
        _bonds.Clear();
        foreach (var bond in model.Bonds)
        {
            var mesh = new MeshInstance3D
            {
                Mesh = assets.Bond,
                MaterialOverride = assets.BondMaterial,
                CastShadow = GeometryInstance3D.ShadowCastingSetting.Off,
            };
            AddChild(mesh);
            bond.Mesh = mesh;
            _bonds.Add(bond);
            Place(bond);
            Style(bond, null);
        }
    }

    public void UpdateAtom(AtomData atom)
    {
        foreach (var bond in _bonds)
        {
            if (bond.A == atom || bond.B == atom) Place(bond);
        }
    }

    public void UpdateAll()
    {
        foreach (var bond in _bonds) Place(bond);
    }

    public void SetFocus(AtomData? focus)
    {
        foreach (var bond in _bonds) Style(bond, focus);
    }

    private static void Place(BondData bond)
    {
        if (bond.Mesh == null) return;
        var from = bond.A.Position;
        var to = bond.B.Position;
        var delta = to - from;
        var distance = delta.Length();
        if (distance < 0.001f)
        {
            bond.Mesh.Visible = false;
            return;
        }
        bond.Mesh.Visible = true;

        var direction = delta / distance;
        var start = from + direction * bond.A.DisplayRadius * 0.75f;
        var end = to - direction * bond.B.DisplayRadius * 0.75f;
        if ((end - start).Dot(direction) <= 0.05f)
        {
            // Bubbles overlap; run centre to centre instead.
            start = from;
            end = to;
        }
        var length = (end - start).Length();

        var reference = Mathf.Abs(direction.Dot(Vector3.Up)) > 0.95f ? Vector3.Right : Vector3.Up;
        var x = direction.Cross(reference).Normalized();
        var z = x.Cross(direction);
        var thickness = Mathf.Min(0.28f, 0.035f + 0.03f * MathF.Log2(1f + bond.Weight));
        var basis = new Basis(x * thickness, direction * length, z * thickness);
        bond.Mesh.Transform = new Transform3D(basis, (start + end) * 0.5f);
        bond.Mesh.SetInstanceShaderParameter("bond_length", length);
    }

    private static void Style(BondData bond, AtomData? focus)
    {
        if (bond.Mesh == null) return;
        var touchesFocus = focus == null || bond.A == focus || bond.B == focus;
        var color = bond.Covalent ? Palette.Covalent : Palette.Ionic;
        color.A = touchesFocus ? 1f : 0.15f;
        bond.Mesh.SetInstanceShaderParameter("bond_color", color);
        bond.Mesh.SetInstanceShaderParameter("highlight", focus != null && touchesFocus ? 1f : 0f);
        bond.Mesh.SetInstanceShaderParameter("two_way", bond.Covalent ? 1f : 0f);
        bond.Mesh.SetInstanceShaderParameter("flow_sign", bond.WeightAB > 0 ? 1f : -1f);
    }
}
