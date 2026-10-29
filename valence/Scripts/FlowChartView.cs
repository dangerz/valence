using System;
using System.Collections.Generic;
using Godot;
using Valence.Schema;

namespace Valence.Viewer;

/// <summary>
/// Draws a <see cref="FlowChart"/>: scroll or pinch to zoom, drag to pan, click a box to select its
/// class, hover for its full name. While a simulation plays, boxes light up as the flow reaches
/// them (gold for a moment, then lit), fired arrows brighten and pulses in flight glow; the view
/// follows the flow until you pan or zoom yourself.
/// </summary>
public partial class FlowChartView : Control
{
    private const float MinZoom = 0.15f;
    private const float MaxZoom = 2.5f;
    private const float FlashSeconds = 1.2f;
    private const int CurveSegments = 18;

    private static readonly Color Background = new(0.02f, 0.05f, 0.08f);
    private static readonly Color BoxFill = new(0.04f, 0.09f, 0.14f);
    private static readonly Color BoxIdle = new(0.23f, 0.35f, 0.52f, 0.7f);
    private static readonly Color Muted = Palette.MutedText;

    private FlowChart? _chart;
    private Vector2 _offset;
    private float _zoom = 1f;
    private bool _dragging;
    private bool _moved;
    private float? _time;     // null: not playing (everything drawn as reached)
    private bool _allReached; // a later tick of a loop: the whole chart is the trail
    private bool _needsFit;   // a new chart arrived while the drawer had no size yet
    private readonly Dictionary<FlowChart.Box, string> _shortLabels = new();

    /// <summary>A box was clicked: its class (null for points without one).</summary>
    public event Action<FlowChart.Box>? BoxChosen;

    /// <summary>Keep the newest reached box in view while playing. Off once the user pans or zooms.</summary>
    public bool Follow { get; set; } = true;

    public FlowChart? Chart => _chart;

    public FlowChartView()
    {
        ClipContents = true;
        MouseFilter = MouseFilterEnum.Stop;
        FocusMode = FocusModeEnum.None;
        TooltipText = " "; // non-empty so Godot asks _GetTooltip for the box under the pointer
    }

    public void SetChart(FlowChart? chart)
    {
        _chart = chart;
        _shortLabels.Clear();
        _time = null;
        _allReached = false;
        Follow = true;
        _needsFit = chart != null;
        if (chart != null) Callable.From(Fit).CallDeferred();
        QueueRedraw();
    }

    /// <param name="time">Seconds into the current run, or null when nothing is playing.</param>
    /// <param name="tick">Which run of a looping simulation (1 = the first).</param>
    public void SetPlayhead(float? time, int tick)
    {
        if (_chart == null) return;
        _time = time;
        _allReached = tick > 1;
        if (time is { } t && Follow) FollowFlow(t);
        QueueRedraw();
    }

    /// <summary>For export: the whole chart at a fixed zoom from the top-left, nothing playing.</summary>
    public void RenderWhole(FlowChart chart, float zoom)
    {
        _chart = chart;
        _shortLabels.Clear();
        _time = null;
        _allReached = false;
        Follow = false;
        _zoom = zoom;
        _offset = Vector2.Zero;
        QueueRedraw();
    }

    /// <summary>Zoom to show the whole chart (never larger than 1:1), centered.</summary>
    public void Fit()
    {
        if (_chart == null || Size.X <= 0f || Size.Y <= 0f) return;
        _needsFit = false;
        var size = _chart.Size;
        _zoom = Mathf.Clamp(Mathf.Min(Size.X / size.X, Size.Y / size.Y), MinZoom, 1f);
        _offset = (Size - size * _zoom) / 2f;
        QueueRedraw();
    }

    public override void _Notification(int what)
    {
        if (what == NotificationResized && _needsFit) Fit();
    }

    // ---------------------------------------------------------------- drawing

    public override void _Draw()
    {
        DrawRect(new Rect2(Vector2.Zero, Size), Background);
        if (_chart == null)
        {
            var font = GetThemeDefaultFont();
            DrawString(font, new Vector2(16f, 28f), "Start a simulation to see its flow chart here.",
                HorizontalAlignment.Left, -1, 14, Muted);
            return;
        }

        DrawSetTransform(_offset, 0f, new Vector2(_zoom, _zoom));
        foreach (var arrow in _chart.Arrows) DrawArrow(arrow);
        foreach (var box in _chart.Boxes) DrawBox(box);
        DrawSetTransform(Vector2.Zero, 0f, Vector2.One);
    }

    private void DrawArrow(FlowChart.Arrow arrow)
    {
        var color = Palette.FlowKind(arrow.Kind);
        var fired = _time == null || _allReached || arrow.FirstStart <= _time;
        var progress = 0f;
        var flying = _time is { } t && InFlight(arrow, t, out progress);
        var alpha = flying ? 1f : fired ? 0.75f : 0.18f;
        if (arrow.Likely) alpha *= 0.6f;
        var width = (flying ? 3.5f : 2f) / Mathf.Max(0.6f, _zoom) * Mathf.Min(1f, _zoom * 1.4f);

        var (a, c1, c2, b) = FlowChart.Curve(arrow.From.Rect, arrow.To.Rect);
        var points = new Vector2[CurveSegments + 1];
        for (var i = 0; i <= CurveSegments; i++) points[i] = Bezier(a, c1, c2, b, i / (float)CurveSegments);
        var dashed = arrow.Likely || arrow.Kind is MemberEdgeKinds.Reads or MemberEdgeKinds.Writes;
        if (dashed)
        {
            for (var i = 0; i < CurveSegments; i += 2) DrawLine(points[i], points[i + 1], new Color(color, alpha), width, true);
        }
        else
        {
            DrawPolyline(points, new Color(color, alpha), width, true);
        }

        // Arrowhead along the curve's last segment.
        var tip = points[^1];
        var direction = (tip - points[^2]).Normalized();
        var side = new Vector2(-direction.Y, direction.X);
        DrawColoredPolygon(new[] { tip, tip - direction * 9f + side * 4.5f, tip - direction * 9f - side * 4.5f }, new Color(color, alpha));

        if (flying) DrawCircle(Bezier(a, c1, c2, b, progress), 4.5f, new Color(Palette.Gold, 0.95f));
    }

    private void DrawBox(FlowChart.Box box)
    {
        var reached = _time == null || _allReached || box.ReachedAt <= _time;
        var flashing = _time is { } t && box.ReachedAt <= t && t - box.ReachedAt < FlashSeconds;
        var rect = box.Rect;

        var style = new StyleBoxFlat
        {
            BgColor = reached ? BoxFill : new Color(BoxFill, 0.55f),
            BorderColor = flashing ? Palette.Gold : box.IsStart ? new Color(Palette.Gold, 0.75f) : reached ? Palette.Accent : BoxIdle,
        };
        style.SetBorderWidthAll(flashing ? 3 : box.IsStart ? 2 : 1);
        style.SetCornerRadiusAll(7);
        if (flashing) style.ShadowColor = new Color(Palette.Gold, 0.35f);
        if (flashing) style.ShadowSize = 8;
        DrawStyleBox(style, rect);

        var hue = box.Owner != null ? Palette.MoleculeHue(box.Owner.Atom.Molecule.Id) : Palette.Accent;
        DrawRect(new Rect2(rect.Position + new Vector2(2f, 7f), new Vector2(3f, rect.Size.Y - 14f)), new Color(hue, reached ? 1f : 0.4f));

        // What it does outside the program (the Boundary lens): an icon per kind, bottom right.
        var marks = 0;
        var right = rect.End.X - 6f;
        foreach (var kind in Boundary.In(box.Effects))
        {
            right -= EffectIcon;
            DrawTextureRect(EffectBadges.WhiteIcon(kind), new Rect2(right, rect.End.Y - EffectIcon - 5f, EffectIcon, EffectIcon), false,
                new Color(kind.Color, reached ? 1f : 0.45f));
            right -= 2f;
            marks++;
        }

        // Text only once it's big enough to read.
        if (_zoom < 0.35f) return;
        var font = GetThemeDefaultFont();
        var textColor = reached ? new Color(0.9f, 0.93f, 0.97f) : new Color(0.9f, 0.93f, 0.97f, 0.45f);
        DrawString(font, rect.Position + new Vector2(12f, 20f), ShortLabel(box, font), HorizontalAlignment.Left, -1, 13, textColor);
        DrawString(font, rect.Position + new Vector2(12f, 37f), Clip(box.Sublabel, font, 11, rect.Size.X - 20f - marks * (EffectIcon + 2f)),
            HorizontalAlignment.Left, -1, 11, new Color(Muted, reached ? 1f : 0.5f));
    }

    private const float EffectIcon = 14f;

    private static bool InFlight(FlowChart.Arrow arrow, float time, out float progress)
    {
        foreach (var (start, arrive) in arrow.Pulses)
        {
            if (time < start || time >= arrive) continue;
            progress = (time - start) / (arrive - start);
            return true;
        }
        progress = 0f;
        return false;
    }

    private static Vector2 Bezier(Vector2 a, Vector2 b, Vector2 c, Vector2 d, float t)
    {
        var u = 1f - t;
        return u * u * u * a + 3f * u * u * t * b + 3f * u * t * t * c + t * t * t * d;
    }

    private string ShortLabel(FlowChart.Box box, Font font)
    {
        if (_shortLabels.TryGetValue(box, out var text)) return text;
        text = Clip((box.IsStart ? "▶ " : "") + box.Label, font, 13, box.Rect.Size.X - 20f);
        _shortLabels[box] = text;
        return text;
    }

    /// <summary>Shortens text with "…" to fit a width at a font size.</summary>
    private static string Clip(string text, Font font, int size, float width)
    {
        if (font.GetStringSize(text, HorizontalAlignment.Left, -1, size).X <= width) return text;
        var cut = text.Length;
        while (cut > 1 && font.GetStringSize(text[..cut] + "…", HorizontalAlignment.Left, -1, size).X > width) cut--;
        return text[..cut] + "…";
    }

    // ---------------------------------------------------------------- following

    /// <summary>Pans (smoothly) so the most recently reached box stays in view.</summary>
    private void FollowFlow(float time)
    {
        if (_chart == null) return;
        FlowChart.Box? latest = null;
        foreach (var box in _chart.Boxes)
        {
            if (box.ReachedAt <= time && (latest == null || box.ReachedAt > latest.ReachedAt)) latest = box;
        }
        if (latest == null) return;

        var center = _offset + latest.Rect.GetCenter() * _zoom;
        var view = new Rect2(Vector2.Zero, Size).Grow(-60f);
        if (view.HasPoint(center)) return;
        var target = Size / 2f - latest.Rect.GetCenter() * _zoom;
        _offset = _offset.Lerp(target, 0.12f);
    }

    // ---------------------------------------------------------------- input

    public override void _GuiInput(InputEvent @event)
    {
        switch (@event)
        {
            case InputEventMouseButton { ButtonIndex: MouseButton.WheelUp or MouseButton.WheelDown, Pressed: true } wheel:
                ZoomAt(wheel.Position, wheel.ButtonIndex == MouseButton.WheelUp ? 1.12f : 1f / 1.12f);
                AcceptEvent();
                break;
            case InputEventMouseButton { ButtonIndex: MouseButton.Left or MouseButton.Middle or MouseButton.Right } button:
                if (button.Pressed)
                {
                    _dragging = true;
                    _moved = false;
                }
                else
                {
                    if (_dragging && !_moved && button.ButtonIndex == MouseButton.Left && BoxAt(button.Position) is { } box)
                    {
                        BoxChosen?.Invoke(box);
                    }
                    _dragging = false;
                }
                AcceptEvent();
                break;
            case InputEventMouseMotion motion when _dragging:
                if (motion.Relative.Length() > 0.5f)
                {
                    _moved = true;
                    Follow = false;
                    _offset += motion.Relative;
                    QueueRedraw();
                }
                AcceptEvent();
                break;
            case InputEventPanGesture pan:
                Follow = false;
                _offset -= pan.Delta * 12f;
                QueueRedraw();
                AcceptEvent();
                break;
            case InputEventMagnifyGesture magnify when magnify.Factor > 0f:
                ZoomAt(magnify.Position, magnify.Factor);
                AcceptEvent();
                break;
        }
    }

    private void ZoomAt(Vector2 at, float factor)
    {
        var zoom = Mathf.Clamp(_zoom * factor, MinZoom, MaxZoom);
        if (Mathf.IsEqualApprox(zoom, _zoom)) return;
        Follow = false;
        // Keep the point under the pointer where it is.
        var chartPoint = (at - _offset) / _zoom;
        _zoom = zoom;
        _offset = at - chartPoint * _zoom;
        QueueRedraw();
    }

    private FlowChart.Box? BoxAt(Vector2 position)
    {
        if (_chart == null) return null;
        var point = (position - _offset) / _zoom;
        foreach (var box in _chart.Boxes)
        {
            if (box.Rect.HasPoint(point)) return box;
        }
        return null;
    }

    public override string _GetTooltip(Vector2 atPosition)
    {
        if (BoxAt(atPosition) is not { } box) return "";
        var reached = box.ReachedAt < float.MaxValue ? $"\nreached at {box.ReachedAt:0.0}s (1×)" : "";
        var outside = box.Effects != EffectFlags.None ? $"\nreaches outside: {Boundary.Describe(box.Effects)}" : "";
        return $"{box.Label}\n{box.Sublabel}{reached}{outside}\nClick to select the class";
    }
}
