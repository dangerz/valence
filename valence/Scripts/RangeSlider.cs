using System;
using Godot;

namespace Valence.Viewer;

/// <summary>A horizontal slider with two handles (low and high) over 0..<see cref="MaxValue"/>.</summary>
public partial class RangeSlider : Control
{
    private const float Padding = 10f;
    private const float HandleRadius = 7f;

    private int _maxValue = 1;
    private int _dragging = -1; // 0 = low handle, 1 = high handle

    public int Low { get; private set; }
    public int High { get; private set; } = 1;

    public int MaxValue
    {
        get => _maxValue;
        set
        {
            _maxValue = Math.Max(1, value);
            Low = Math.Clamp(Low, 0, _maxValue);
            High = Math.Clamp(High, Low, _maxValue);
            QueueRedraw();
        }
    }

    /// <summary>Low, high: raised while dragging.</summary>
    public event Action<int, int>? RangeChanged;

    public RangeSlider()
    {
        CustomMinimumSize = new Vector2(240f, 24f);
        MouseFilter = MouseFilterEnum.Stop;
        MouseDefaultCursorShape = CursorShape.PointingHand;
    }

    /// <summary>Sets both handles without raising <see cref="RangeChanged"/>.</summary>
    public void SetRange(int low, int high)
    {
        Low = Math.Clamp(low, 0, _maxValue);
        High = Math.Clamp(high, Low, _maxValue);
        QueueRedraw();
    }

    public override void _GuiInput(InputEvent @event)
    {
        switch (@event)
        {
            case InputEventMouseButton { ButtonIndex: MouseButton.Left, Pressed: true } press:
            {
                var value = ValueAt(press.Position.X);
                // Grab whichever handle is closer; when they overlap, the side you clicked on decides.
                _dragging = Low == High
                    ? (value > High ? 1 : 0)
                    : (Math.Abs(value - Low) <= Math.Abs(value - High) ? 0 : 1);
                Move(value);
                AcceptEvent();
                break;
            }
            case InputEventMouseButton { ButtonIndex: MouseButton.Left, Pressed: false }:
                _dragging = -1;
                AcceptEvent();
                break;
            case InputEventMouseMotion motion when _dragging >= 0:
                Move(ValueAt(motion.Position.X));
                AcceptEvent();
                break;
        }
    }

    private void Move(int value)
    {
        int low = Low, high = High;
        if (_dragging == 0) low = Math.Min(value, High);
        else high = Math.Max(value, Low);
        if (low == Low && high == High) return;
        Low = low;
        High = high;
        QueueRedraw();
        RangeChanged?.Invoke(Low, High);
    }

    private float TrackWidth => Mathf.Max(1f, Size.X - 2f * Padding);

    private int ValueAt(float x) => (int)Math.Round(Mathf.Clamp((x - Padding) / TrackWidth, 0f, 1f) * _maxValue);

    private float XAt(int value) => Padding + TrackWidth * value / _maxValue;

    public override void _Draw()
    {
        var y = Size.Y / 2f;
        DrawLine(new Vector2(Padding, y), new Vector2(Size.X - Padding, y), Palette.PanelBorder, 4f);
        DrawLine(new Vector2(XAt(Low), y), new Vector2(XAt(High), y), Palette.Accent, 4f);
        foreach (var value in new[] { Low, High })
        {
            var center = new Vector2(XAt(value), y);
            DrawCircle(center, HandleRadius, Palette.Accent);
            DrawCircle(center, HandleRadius - 3f, new Color(0.06f, 0.08f, 0.12f));
        }
    }
}
