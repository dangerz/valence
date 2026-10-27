using Godot;

namespace Valence.Viewer;

/// <summary>
/// Outside simulation, unpinned labels only show near the mouse pointer: fully visible within
/// ~55% of <see cref="Radius"/> pixels, fading to nothing at the radius.
/// </summary>
public static class LabelProximity
{
    public const float Radius = 220f;

    /// <summary>0..1 visibility for a label at a world position, given the camera and mouse.</summary>
    public static float Factor(Viewport viewport, Vector3 world)
    {
        var camera = viewport.GetCamera3D();
        if (camera == null || camera.IsPositionBehind(world)) return 0f;
        var screen = camera.UnprojectPosition(world);
        var distance = screen.DistanceTo(viewport.GetMousePosition());
        return 1f - Mathf.SmoothStep(Radius * 0.55f, Radius, distance);
    }
}
