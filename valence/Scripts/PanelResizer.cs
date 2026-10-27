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

    /// <summary>Horizontal drag distance in pixels since the last event.</summary>
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
                Dragged?.Invoke(motion.Relative.X);
                AcceptEvent();
                break;
        }
    }

    public override void _Draw()
    {
        DrawRect(new Rect2(Vector2.Zero, Size), UiKit.AppBackground);
        if (!_hover && !_dragging) return;
        const float width = 2f;
        DrawRect(new Rect2((Size.X - width) / 2f, 8f, width, Math.Max(0f, Size.Y - 16f)), Palette.Accent);
    }
}
