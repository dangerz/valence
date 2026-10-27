using System;
using System.Collections.Generic;
using Godot;

namespace Valence.Viewer;

/// <summary>
/// Plays a <see cref="FlowPlan"/>: glowing pulses travel along arcs between classes, and links that
/// have fired stay drawn as a trail. Raises <see cref="NodeReached"/> as pulses land, which Main
/// turns into heat on the nucleons.
/// </summary>
public partial class SimulationView : Node3D
{
    private const int MaxPulses = 3000;
    private const int MaxTraceLines = 3000;

    private readonly Dictionary<(NodeData From, NodeData To, string Kind), int> _traced = new();
    private MultiMesh? _pulses;
    private DetailLines? _trace;
    private FlowGraph? _graph;
    private FlowPlan? _plan;
    private float _time;
    private int _started;
    private int _arrived;
    private bool _traceDirty;
    private float _traceCooldown;

    public bool Active => _plan != null;

    /// <summary>Seconds into the current run of the plan (the flow chart follows it).</summary>
    public float Time => _time;
    public bool Playing { get; private set; }
    public float Speed { get; set; } = 1f;

    /// <summary>A pulse landed on a class; the float is how much heat to add.</summary>
    public event Action<NodeData, float>? NodeReached;

    /// <summary>The plan played to the end.</summary>
    public event Action? Finished;

    public void Init(SceneAssets assets)
    {
        _pulses = new MultiMesh
        {
            TransformFormat = MultiMesh.TransformFormatEnum.Transform3D,
            UseColors = true,
            Mesh = assets.Pulse,
        };
        _pulses.InstanceCount = MaxPulses;
        _pulses.VisibleInstanceCount = 0;
        AddChild(new MultiMeshInstance3D
        {
            Multimesh = _pulses,
            MaterialOverride = assets.NucleonMaterial, // emissive via color alpha
            CastShadow = GeometryInstance3D.ShadowCastingSetting.Off,
        });

        _trace = new DetailLines();
        _trace.Init(assets);
        AddChild(_trace);
    }

    /// <param name="keepTrace">Keep the trail from earlier ticks (game loop).</param>
    public void Play(FlowGraph graph, FlowPlan plan, bool keepTrace)
    {
        _graph = graph;
        _plan = plan;
        _time = 0f;
        _started = 0;
        _arrived = 0;
        if (!keepTrace) _traced.Clear();
        _traceDirty = true;
        Playing = true;
    }

    public void Pause() => Playing = false;

    public void Resume() => Playing = _plan != null;

    public void Stop()
    {
        _plan = null;
        _graph = null;
        Playing = false;
        _traced.Clear();
        if (_pulses != null) _pulses.VisibleInstanceCount = 0;
        _trace?.SetLines(Array.Empty<(Vector3, Vector3, Color)>());
    }

    /// <summary>Call when classes move (atom opened, dragged) so the trail follows.</summary>
    public void RefreshTrace() => _traceDirty = true;

    public override void _Process(double delta)
    {
        if (_plan == null || _graph == null || _pulses == null) return;
        if (Playing) _time += (float)delta * Speed;

        var events = _plan.Events;
        while (_arrived < events.Count && events[_arrived].Arrive <= _time)
        {
            var landed = events[_arrived++];
            var from = _graph.OwnerOf(landed.From);
            var to = _graph.OwnerOf(landed.To);
            if (to != null) NodeReached?.Invoke(to, landed.Likely ? 0.2f : 0.35f);
            if (from != null && to != null && from != to)
            {
                var key = (from, to, landed.Kind);
                _traced[key] = _traced.GetValueOrDefault(key) + 1;
                _traceDirty = true;
            }
        }
        while (_started < events.Count && events[_started].Start <= _time) _started++;

        // In flight: started but not yet arrived.
        var count = 0;
        for (var i = _arrived; i < _started && count < MaxPulses; i++)
        {
            var pulse = events[i];
            var from = _graph.OwnerOf(pulse.From);
            var to = _graph.OwnerOf(pulse.To);
            // A pulse may head for a class "hide untouched" is still hiding (it appears on arrival),
            // but never for one the user hid.
            if (from == null || to == null || from.IsHidden || to.UserOpacity <= 0f) continue;
            var opacity = Mathf.Min(from.Opacity, to.UserOpacity);
            var s = Mathf.Clamp((_time - pulse.Start) / (pulse.Arrive - pulse.Start), 0f, 1f);
            var position = PulsePosition(from.WorldPosition, to.WorldPosition, s, from == to);
            var size = (pulse.Likely ? 0.08f : 0.12f) * (opacity < 1f ? 0.6f : 1f);
            var basis = new Basis(new Vector3(size, 0f, 0f), new Vector3(0f, size, 0f), new Vector3(0f, 0f, size));
            _pulses.SetInstanceTransform(count, new Transform3D(basis, position));
            var color = Palette.FlowKind(pulse.Kind);
            _pulses.SetInstanceColor(count, new Color(color.R, color.G, color.B, (pulse.Likely ? 0.6f : 1f) * opacity));
            count++;
        }
        _pulses.VisibleInstanceCount = count;

        _traceCooldown -= (float)delta;
        if (_traceDirty && _traceCooldown <= 0f)
        {
            RebuildTrace();
            _traceDirty = false;
            _traceCooldown = 0.1f;
        }

        if (Playing && _arrived >= events.Count && _time >= _plan.Duration + 0.2f)
        {
            Playing = false;
            Finished?.Invoke();
        }
    }

    /// <summary>Same arc as <see cref="DetailLines"/>, so pulses ride on the trail; self-calls loop.</summary>
    private static Vector3 PulsePosition(Vector3 from, Vector3 to, float s, bool same)
    {
        if (same)
        {
            var angle = s * Mathf.Tau;
            return from + new Vector3(Mathf.Cos(angle) * 0.45f, 0.3f + Mathf.Sin(angle) * 0.15f, Mathf.Sin(angle) * 0.45f);
        }
        var lift = Vector3.Up * Mathf.Min(6f, from.DistanceTo(to) * 0.18f);
        return from.Lerp(to, s) + lift * (4f * s * (1f - s));
    }

    private void RebuildTrace()
    {
        if (_trace == null) return;
        var lines = new List<(Vector3 From, Vector3 To, Color Color)>();
        foreach (var ((from, to, kind), hits) in _traced)
        {
            if (lines.Count >= MaxTraceLines) break;
            if (from.IsHidden || to.IsHidden) continue;
            var color = Palette.FlowKind(kind);
            color.A = Mathf.Clamp(0.25f + 0.08f * hits, 0.25f, 0.85f) * Mathf.Min(from.Opacity, to.Opacity);
            lines.Add((from.WorldPosition, to.WorldPosition, color));
        }
        _trace.SetLines(lines);
    }
}
