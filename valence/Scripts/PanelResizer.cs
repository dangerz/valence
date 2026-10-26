using System;
using Godot;

namespace Valence.Viewer;

/// <summary>
/// A side panel's border that can be dragged to resize it. Draws the border line itself and
/// highlights it on hover or while dragging.
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
        var background = new Color(0.06f, 0.08f, 0.12f, 0.92f);
        DrawRect(new Rect2(Vector2.Zero, Size), background);
        var color = _hover || _dragging ? Palette.Accent : Palette.PanelBorder;
        var width = _hover || _dragging ? 3f : 2f;
        DrawRect(new Rect2((Size.X - width) / 2f, 0f, width, Size.Y), color);
    }
}
