using System.Linq;
using Godot;

namespace Valence.Viewer;

/// <summary>
/// The X/Y/Z axes in the 3D view's corner, turning with the camera so it's always clear which way
/// is up (height = dependency layer). Purely visual; clicks pass through.
/// </summary>
public partial class AxisGizmo : Control
{
    private const float Length = 34f;

    private static readonly (Vector3 Axis, string Name, Color Color)[] Axes =
    {
        (Vector3.Right, "X", new Color(1f, 0.35f, 0.37f)),
        (Vector3.Up, "Y", new Color(0.32f, 0.85f, 0.4f)),
        (Vector3.Back, "Z", new Color(0.3f, 0.67f, 0.97f)),
    };

    /// <summary>The camera whose orientation is shown; nothing is drawn without one.</summary>
    public Camera3D? Camera { get; set; }

    public AxisGizmo()
    {
        CustomMinimumSize = new Vector2(96f, 96f);
        MouseFilter = MouseFilterEnum.Ignore;
    }

    public override void _Process(double delta)
    {
        if (Camera != null && IsVisibleInTree()) QueueRedraw();
    }

    public override void _Draw()
    {
        if (Camera == null) return;
        var center = Size / 2f;
        var view = Camera.GlobalTransform.Basis.Inverse();
        var font = GetThemeDefaultFont();

        // Back to front, so the axis pointing at the viewer is drawn last.
        foreach (var (axis, name, color) in Axes.OrderBy(a => (view * a.Axis).Z))
        {
            var v = view * axis;
            var tip = center + new Vector2(v.X, -v.Y) * Length;
            var faded = v.Z < -0.2f ? new Color(color, 0.55f) : color;
            DrawLine(center, tip, faded, 2.5f, true);
            DrawCircle(tip, 2.5f, faded);
            var labelAt = center + new Vector2(v.X, -v.Y) * (Length + 11f) + new Vector2(-4f, 5f);
            DrawString(font, labelAt, name, HorizontalAlignment.Left, -1, 13, faded);
        }

        var cube = UiAssets.Icon("cube");
        DrawTextureRect(cube, new Rect2(center - new Vector2(9f, 9f), new Vector2(18f, 18f)), false);
    }
}
