using System;
using Godot;

namespace AGAS.UI;

public partial class PromptFragmentTextEdit : Control
{
    public const float InitialHeight = 180;
    public const float MinimumHeight = 64;
    public const float MaximumHeight = 320;
    private const float GripSize = 18;

    public TextEdit TextEditor { get; }
    public bool SynchronizingText { get; private set; }

    private bool _resizing;
    private float _previousDragY;
    private Window? _window;
    private string _tooltipKey = "支持多行输入，修改即时同步；拖动右下角调整高度";

    public PromptFragmentTextEdit()
    {
        Name = "PromptFragmentEditor";
        SizeFlagsHorizontal = SizeFlags.ExpandFill;
        CustomMinimumSize = new Vector2(160, InitialHeight);
        MouseFilter = MouseFilterEnum.Ignore;
        TextEditor = new TextEdit
        {
            Name = "PromptFragmentTextEdit",
            AutoTranslateMode = AutoTranslateModeEnum.Disabled,
            PlaceholderText = TranslationServer.Translate("输入图层提示词文本"),
            WrapMode = TextEdit.LineWrappingMode.Boundary,
            ScrollFitContentHeight = false,
            ScrollFitContentWidth = false
        };
        AddChild(TextEditor);
        TextEditor.SetAnchorsAndOffsetsPreset(LayoutPreset.FullRect);

        var grip = new Control
        {
            Name = "PromptFragmentResizeHandle",
            TooltipText = "上下拖动调整片段高度",
            MouseDefaultCursorShape = CursorShape.Vsize,
            MouseFilter = MouseFilterEnum.Stop
        };
        AddChild(grip);
        grip.SetAnchorsAndOffsetsPreset(LayoutPreset.BottomRight);
        grip.OffsetLeft = -GripSize;
        grip.OffsetTop = -GripSize;
        grip.Draw += () =>
        {
            Color color = TextEditor.GetThemeColor("font_color");
            for (int inset = 4; inset <= 12; inset += 4)
            {
                grip.DrawLine(new Vector2(grip.Size.X - inset, grip.Size.Y - 3), new Vector2(grip.Size.X - 3, grip.Size.Y - inset), color, 1.5f, true);
            }
        };
        grip.GuiInput += input =>
        {
            if (input is InputEventMouseButton { ButtonIndex: MouseButton.Left, Pressed: true } mouse)
            {
                _resizing = true;
                _previousDragY = mouse.GlobalPosition.Y;
                SetProcessInput(true);
                grip.AcceptEvent();
            }
        };
    }

    public override void _Ready()
    {
        RefreshPlaceholder();
        // Keep the corner inside the editor while reserving space for text and its scrollbar.
        foreach (string state in new[] { "normal", "read_only" })
        {
            var style = (StyleBox)TextEditor.GetThemeStylebox(state).Duplicate();
            style.ContentMarginBottom = Math.Max(style.GetMargin(Side.Bottom), GripSize);
            TextEditor.AddThemeStyleboxOverride(state, style);
        }
        SetProcessInput(false);
        _window = GetWindow();
        _window.FocusExited += CancelResize;
        VisibilityChanged += OnVisibilityChanged;
    }

    public override void _Notification(int what)
    {
        if (what == NotificationTranslationChanged && TextEditor != null)
        {
            RefreshPlaceholder();
        }
    }

    private void RefreshPlaceholder()
    {
        TextEditor.PlaceholderText = TranslationServer.Translate("输入图层提示词文本");
        TextEditor.TooltipText = TranslationServer.Translate(_tooltipKey);
    }

    public void SetTooltipKey(string key)
    {
        _tooltipKey = key;
        TextEditor.TooltipText = TranslationServer.Translate(key);
    }

    public override void _ExitTree()
    {
        CancelResize();
        if (_window != null)
        {
            _window.FocusExited -= CancelResize;
            _window = null;
        }
        VisibilityChanged -= OnVisibilityChanged;
    }

    public override void _Input(InputEvent input)
    {
        if (!_resizing)
        {
            return;
        }
        if (input is InputEventMouseMotion motion)
        {
            if ((motion.ButtonMask & MouseButtonMask.Left) == 0)
            {
                CancelResize();
                return;
            }
            float height = Math.Clamp(CustomMinimumSize.Y + motion.GlobalPosition.Y - _previousDragY, MinimumHeight, MaximumHeight);
            _previousDragY = motion.GlobalPosition.Y;
            CustomMinimumSize = new Vector2(CustomMinimumSize.X, height);
            GetViewport().SetInputAsHandled();
        }
        else if (input is InputEventMouseButton { ButtonIndex: MouseButton.Left, Pressed: false })
        {
            CancelResize();
            GetViewport().SetInputAsHandled();
        }
    }

    public void CancelResize()
    {
        _resizing = false;
        SetProcessInput(false);
    }

    public void SynchronizeText(string value)
    {
        if (TextEditor.Text == value)
        {
            return;
        }
        int line = TextEditor.GetCaretLine();
        int column = TextEditor.GetCaretColumn();
        double vertical = TextEditor.ScrollVertical;
        int horizontal = TextEditor.ScrollHorizontal;
        SynchronizingText = true;
        try
        {
            TextEditor.Text = value;
            TextEditor.SetCaretLine(line, false);
            TextEditor.SetCaretColumn(column, false);
            TextEditor.ScrollVertical = vertical;
            TextEditor.ScrollHorizontal = horizontal;
        }
        finally
        {
            SynchronizingText = false;
        }
    }

    private void OnVisibilityChanged()
    {
        if (!IsVisibleInTree())
        {
            CancelResize();
        }
    }
}
