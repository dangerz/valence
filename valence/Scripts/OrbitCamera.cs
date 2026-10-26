using Godot;

namespace Valence.Viewer;

/// <summary>Orbit/pan/zoom camera with smoothed motion. Input is fed in by <see cref="Main"/>.</summary>
public partial class OrbitCamera : Camera3D
{
    private const float MinDistance = 1.5f;
    private const float MaxDistance = 3000f;

    private Vector3 _target;
    private Vector3 _currentTarget;
    private float _yaw = 0.6f;
    private float _pitch = 0.45f;
    private float _distance = 40f;
    private float _currentDistance = 40f;

    public override void _Ready()
    {
        Fov = 55f;
        Near = 0.05f;
        Far = 6000f;
        UpdateTransform();
    }

    public void Orbit(Vector2 delta)
    {
        _yaw -= delta.X * 0.006f;
        _pitch = Mathf.Clamp(_pitch + delta.Y * 0.006f, -1.45f, 1.45f);
    }

    public void Pan(Vector2 delta)
    {
        var basis = GlobalTransform.Basis;
        var scale = _currentDistance * 0.0016f;
        _target += (-basis.X * delta.X + basis.Y * delta.Y) * scale;
    }

    public void Zoom(float factor) => _distance = Mathf.Clamp(_distance * factor, MinDistance, MaxDistance);

    public void FlyTo(Vector3 target, float distance, bool immediate = false)
    {
        _target = target;
        _distance = Mathf.Clamp(distance, MinDistance, MaxDistance);
        if (!immediate) return;
        _currentTarget = _target;
        _currentDistance = _distance;
        UpdateTransform();
    }

    public override void _Process(double delta)
    {
        var k = 1f - Mathf.Exp(-(float)delta * 8f);
        _currentTarget = _currentTarget.Lerp(_target, k);
        _currentDistance = Mathf.Lerp(_currentDistance, _distance, k);
        UpdateTransform();
    }

    private void UpdateTransform()
    {
        if (!IsInsideTree()) return;
        var offset = new Vector3(
            Mathf.Cos(_pitch) * Mathf.Sin(_yaw),
            Mathf.Sin(_pitch),
            Mathf.Cos(_pitch) * Mathf.Cos(_yaw)) * _currentDistance;
        Position = _currentTarget + offset;
        LookAt(_currentTarget, Vector3.Up);
    }
}
