using System.Collections.Generic;
using Godot;

namespace Valence.Viewer;

/// <summary>
/// Class-to-class dependency lines for the current selection. Lines arc upward slightly so
/// overlapping ones stay readable, and fade from source to target to show direction.
/// </summary>
public partial class DetailLines : MeshInstance3D
{
    private const int Segments = 10;
    private readonly ImmediateMesh _lines = new();

    public void Init(SceneAssets assets)
    {
        Mesh = _lines;
        MaterialOverride = assets.LineMaterial;
        CastShadow = ShadowCastingSetting.Off;
    }

    public void SetLines(IReadOnlyList<(Vector3 From, Vector3 To, Color Color)> lines)
    {
        _lines.ClearSurfaces();
        if (lines.Count == 0) return;

        _lines.SurfaceBegin(Godot.Mesh.PrimitiveType.Lines);
        foreach (var (from, to, color) in lines)
        {
            var lift = Vector3.Up * Mathf.Min(6f, from.DistanceTo(to) * 0.18f);
            var previous = from;
            for (var i = 1; i <= Segments; i++)
            {
                var t = (float)i / Segments;
                var point = from.Lerp(to, t) + lift * (4f * t * (1f - t));
                _lines.SurfaceSetColor(Fade(color, (float)(i - 1) / Segments));
                _lines.SurfaceAddVertex(previous);
                _lines.SurfaceSetColor(Fade(color, t));
                _lines.SurfaceAddVertex(point);
                previous = point;
            }
        }
        _lines.SurfaceEnd();
    }

    private static Color Fade(Color color, float t) => new(color.R, color.G, color.B, color.A * (1f - 0.7f * t));
}
