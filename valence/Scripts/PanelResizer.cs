using System;
using Godot;

namespace Valence.Viewer;

/// <summary>
/// The gap beside a side panel, dragged to resize it. Drawn in the app background, with a line
/// on hover or while dragging.
/// </summary>
public partial class PanelResizer : Control
{
    private bool _dragging;
    private bool _hover;
    private bool _vertical;

    /// <summary>A gap above a drawer, dragged up/down (Dragged then reports vertical distance).</summary>
    public bool Vertical
    {
        get => _vertical;
        set
        {
            _vertical = value;
            CustomMinimumSize = value ? new Vector2(0f, 8f) : new Vector2(8f, 0f);
            SizeFlagsVertical = value ? SizeFlags.Fill : SizeFlags.ExpandFill;
            SizeFlagsHorizontal = value ? SizeFlags.ExpandFill : SizeFlags.Fill;
            MouseDefaultCursorShape = value ? CursorShape.Vsize : CursorShape.Hsize;
        }
    }

    /// <summary>Drag distance in pixels since the last event (horizontal, or vertical when <see cref="Vertical"/>).</summary>
    public event Action<float>? Dragged;

    public event Action? DragFinished;

    public PanelResizer()
    {
        CustomMinimumSize = new Vector2(8f, 0f);
        SizeFlagsVertical = SizeFlags.ExpandFill;
        MouseFilter = MouseFilterEnum.Stop;
        MouseDefaultCursorShape = CursorShape.Hsize;
        TooltipText = "Drag to resize";
        MouseEntered += () =>
        {
            _hover = true;
            QueueRedraw();
        };
        MouseExited += () =>
        {
            _hover = false;
            QueueRedraw();
        };
    }

    public override void _GuiInput(InputEvent @event)
    {
        switch (@event)
        {
            case InputEventMouseButton { ButtonIndex: MouseButton.Left } button:
                _dragging = button.Pressed;
                if (!button.Pressed) DragFinished?.Invoke();
                AcceptEvent();
                QueueRedraw();
                break;
            case InputEventMouseMotion motion when _dragging:
                Dragged?.Invoke(_vertical ? motion.Relative.Y : motion.Relative.X);
                AcceptEvent();
                break;
        }
    }

    public override void _Draw()
    {
        DrawRect(new Rect2(Vector2.Zero, Size), UiKit.AppBackground);
        if (!_hover && !_dragging) return;
        const float width = 2f;
        if (_vertical) DrawRect(new Rect2(8f, (Size.Y - width) / 2f, Math.Max(0f, Size.X - 16f), width), Palette.Accent);
        else DrawRect(new Rect2((Size.X - width) / 2f, 8f, width, Math.Max(0f, Size.Y - 16f)), Palette.Accent);
    }
}
