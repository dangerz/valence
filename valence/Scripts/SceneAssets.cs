using System.Collections.Generic;
using Godot;

namespace Valence.Viewer;

/// <summary>Meshes and materials shared by every atom, bond and nucleon.</summary>
public sealed class SceneAssets
{
    public SphereMesh Nucleon { get; } = new() { Radius = 1f, Height = 2f, RadialSegments = 20, Rings = 10 };
    public SphereMesh Cloud { get; } = new() { Radius = 1f, Height = 2f, RadialSegments = 32, Rings = 16 };
    public SphereMesh Electron { get; } = new() { Radius = 0.07f, Height = 0.14f, RadialSegments = 8, Rings = 4 };

    /// <summary>Unit-radius ring in the XZ plane; scaled per orbit.</summary>
    public TorusMesh Orbit { get; } = new() { InnerRadius = 0.99f, OuterRadius = 1.01f, Rings = 64, RingSegments = 6 };

    /// <summary>Unit cylinder along Y; scaled per bond.</summary>
    public CylinderMesh Bond { get; } = new()
    {
        TopRadius = 1f, BottomRadius = 1f, Height = 1f, RadialSegments = 10, Rings = 1, CapTop = false, CapBottom = false,
    };

    public ShaderMaterial NucleonMaterial { get; }
    public ShaderMaterial CloudMaterial { get; }
    public ShaderMaterial BondMaterial { get; }

    public StandardMaterial3D ElectronMaterial { get; } = new()
    {
        AlbedoColor = new Color(0.75f, 0.9f, 1f),
        EmissionEnabled = true,
        Emission = new Color(0.55f, 0.8f, 1f),
        EmissionEnergyMultiplier = 3f,
    };

    public StandardMaterial3D LineMaterial { get; } = new()
    {
        ShadingMode = BaseMaterial3D.ShadingModeEnum.Unshaded,
        VertexColorUseAsAlbedo = true,
        Transparency = BaseMaterial3D.TransparencyEnum.Alpha,
        NoDepthTest = true,
    };

    private readonly Dictionary<Color, StandardMaterial3D> _orbitMaterials = new();

    public SceneAssets()
    {
        NucleonMaterial = new ShaderMaterial { Shader = GD.Load<Shader>("res://Shaders/nucleon.gdshader") };
        CloudMaterial = new ShaderMaterial { Shader = GD.Load<Shader>("res://Shaders/atom_cloud.gdshader") };
        BondMaterial = new ShaderMaterial { Shader = GD.Load<Shader>("res://Shaders/bond_flow.gdshader") };
    }

    public StandardMaterial3D OrbitMaterial(Color tint)
    {
        if (!_orbitMaterials.TryGetValue(tint, out var material))
        {
            material = new StandardMaterial3D
            {
                ShadingMode = BaseMaterial3D.ShadingModeEnum.Unshaded,
                Transparency = BaseMaterial3D.TransparencyEnum.Alpha,
                AlbedoColor = new Color(tint.R, tint.G, tint.B, 0.28f),
            };
            _orbitMaterials[tint] = material;
        }
        return material;
    }
}
