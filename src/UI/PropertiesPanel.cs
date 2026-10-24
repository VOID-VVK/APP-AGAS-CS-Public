using System;
using AppAgasCs.Core.Resources;
using Agas.Features.Canvas;
using AppAgasCs.UI.Shared;
using Godot;

namespace AGAS.UI;

public partial class PropertiesPanel : PanelContainer
{
    private CanvasWorkspace? _canvas;
    private ImageInstanceResource? _image;
    private bool _syncing;
    private bool _editable = true;

    private Label? _emptyLabel;
    private LineEdit? _imagePath;
    private LineEdit? _order;
    private SpinBox? _x;
    private SpinBox? _y;
    private SpinBox? _scaleX;
    private SpinBox? _scaleY;
    private SpinBox? _rotation;
    private SpinBox? _opacity;
    private TextureRect? _filePreviewTexture;
    private Label? _filePreviewStatus;
    private VSplitContainer? _imagePropertiesView;
    private VBoxContainer? _propertiesContent;
    private VBoxContainer? _sceneReadmeView;
    private TextEdit? _sceneReadmeEdit;
    private bool _readmeSyncing;
    private string _previewMessage = "从左侧文件列表选择图片后预览。";
    private object[] _previewArguments = Array.Empty<object>();

    public event Action<string>? SceneReadmeChanged;

    public Func<string, bool>? MutationGuard { get; set; }

    public bool Editable
    {
        get => _editable;
        set
        {
            _editable = value;
            SyncEditableState();
        }
    }

    public override void _Ready()
    {
        Name = "PropertiesPanel";
        BuildUi();
        SetSelectedImage(null);
    }

    public override void _Notification(int what)
    {
        if (what == NotificationTranslationChanged && IsNodeReady())
        {
            if (_filePreviewStatus != null)
            {
                _filePreviewStatus.Text = string.Format(Tr(_previewMessage), _previewArguments);
                _filePreviewStatus.TooltipText = _filePreviewStatus.Text;
            }
            if (_sceneReadmeEdit != null) _sceneReadmeEdit.PlaceholderText = Tr("场景说明");
        }
    }

    public void BindCanvas(CanvasWorkspace canvas)
    {
        _canvas = canvas;
    }

    public void SetSelectedImage(Resource? image)
    {
        _image = image as ImageInstanceResource;
        ShowImageProperties();
        if (_propertiesContent != null) _propertiesContent.Visible = true;
        SyncFromImage();
        if (_filePreviewTexture != null) _filePreviewTexture.Texture = null;
        SetFilePreviewMessage("从左侧文件列表选择图片后预览。", true);
    }

    public void ShowFilePreview(string relativePath, string absolutePath)
    {
        SetSelectedImage(null);
        if (_propertiesContent != null) _propertiesContent.Visible = false;
        if (_filePreviewTexture == null || _filePreviewStatus == null)
        {
            return;
        }

        try
        {
            Image? image = Image.LoadFromFile(absolutePath);
            if (image == null || image.IsEmpty())
            {
                _filePreviewTexture.Texture = null;
                SetFilePreviewMessage("图片无法读取：{0}", true, relativePath);
                return;
            }

            _filePreviewTexture.Texture = ImageTexture.CreateFromImage(image);
            SetFilePreviewMessage("{0}", false, relativePath);
        }
        catch (Exception exception)
        {
            _filePreviewTexture.Texture = null;
            SetFilePreviewMessage("图片预览失败：{0}", true, exception.Message);
        }
    }

    public void ShowSceneReadme(string readme)
    {
        SetSceneReadme(readme);
        if (_imagePropertiesView != null) _imagePropertiesView.Visible = false;
        if (_sceneReadmeView != null) _sceneReadmeView.Visible = true;
    }

    public void SetSceneReadme(string readme)
    {
        if (_sceneReadmeEdit == null) return;
        _readmeSyncing = true;
        _sceneReadmeEdit.Text = readme ?? string.Empty;
        _readmeSyncing = false;
    }

    private void ShowImageProperties()
    {
        if (_sceneReadmeView != null) _sceneReadmeView.Visible = false;
        if (_imagePropertiesView != null) _imagePropertiesView.Visible = true;
    }

    private void BuildUi()
    {
        MarginContainer margin = AgasMarginContainerFactory.Create(8, 8, 8, 8);
        var panelScroll = new ScrollContainer
        {
            Name = "PropertiesContentScroll",
            HorizontalScrollMode = ScrollContainer.ScrollMode.Auto,
            VerticalScrollMode = ScrollContainer.ScrollMode.Auto,
            SizeFlagsHorizontal = SizeFlags.ExpandFill,
            SizeFlagsVertical = SizeFlags.ExpandFill,
            FollowFocus = true
        };
        panelScroll.GetHScrollBar().CustomMinimumSize = new Vector2(1, 16);
        panelScroll.GetVScrollBar().CustomMinimumSize = new Vector2(16, 1);
        panelScroll.AddThemeStyleboxOverride("panel", new StyleBoxEmpty());
        AddChild(panelScroll);
        margin.SizeFlagsHorizontal = SizeFlags.ExpandFill;
        margin.SizeFlagsVertical = SizeFlags.ExpandFill;
        panelScroll.AddChild(margin);

        var viewRoot = new VBoxContainer
        {
            Name = "PropertiesViewRoot",
            SizeFlagsHorizontal = SizeFlags.ExpandFill,
            SizeFlagsVertical = SizeFlags.ExpandFill
        };
        margin.AddChild(viewRoot);

        var split = new VSplitContainer
        {
            Name = "PropertiesPreviewSplitter",
            SizeFlagsHorizontal = SizeFlags.ExpandFill,
            SizeFlagsVertical = SizeFlags.ExpandFill
        };
        _imagePropertiesView = split;
        viewRoot.AddChild(split);

        var content = new VBoxContainer
        {
            Name = "PropertiesContent",
            CustomMinimumSize = new Vector2(1, 1),
            SizeFlagsHorizontal = SizeFlags.ExpandFill,
            SizeFlagsVertical = SizeFlags.Fill
        };
        content.AddThemeConstantOverride("separation", 8);
        _propertiesContent = content;
        split.AddChild(content);

        var title = new Label { Name = "PropertiesSectionTitle", Text = "详细信息", TooltipText = "详细信息", ClipText = true, TextOverrunBehavior = TextServer.OverrunBehavior.TrimEllipsis };
        title.AddThemeFontSizeOverride("font_size", 12);
        content.AddChild(title);

        var scroll = new ScrollContainer
        {
            Name = "PropertiesFieldsScroll",
            HorizontalScrollMode = ScrollContainer.ScrollMode.Auto,
            VerticalScrollMode = ScrollContainer.ScrollMode.Auto,
            SizeFlagsHorizontal = SizeFlags.ExpandFill,
            SizeFlagsVertical = SizeFlags.ExpandFill
        };
        scroll.GetHScrollBar().CustomMinimumSize = new Vector2(1, 16);
        scroll.GetVScrollBar().CustomMinimumSize = new Vector2(16, 1);
        content.AddChild(scroll);

        var fields = new VBoxContainer
        {
            Name = "PropertiesFields",
            SizeFlagsHorizontal = SizeFlags.ExpandFill
        };
        fields.AddThemeConstantOverride("separation", 8);
        scroll.AddChild(fields);

        _emptyLabel = new Label
        {
            Name = "PropertiesEmptyState",
            AutowrapMode = TextServer.AutowrapMode.WordSmart
        };
        _emptyLabel.AddThemeFontSizeOverride("font_size", 10);
        _emptyLabel.AddThemeColorOverride("font_color", AppAgasCs.UI.Theming.AgasSharedIdeTheme.MutedTextColor);
        fields.AddChild(_emptyLabel);

        _imagePath = AddReadOnlyField(fields, "图片路径");
        _order = AddReadOnlyField(fields, "层级顺序");
        _x = AddNumberField(fields, "X", -100000, 100000, 1);
        _y = AddNumberField(fields, "Y", -100000, 100000, 1);
        _scaleX = AddNumberField(fields, "横向缩放", 0.01, 100, 0.05);
        _scaleY = AddNumberField(fields, "纵向缩放", 0.01, 100, 0.05);
        _rotation = AddNumberField(fields, "旋转角度", -3600, 3600, 1);
        _opacity = AddNumberField(fields, "透明度", 0, 1, 0.05);

        foreach (SpinBox spin in new[] { _x, _y, _scaleX, _scaleY, _rotation, _opacity })
        {
            spin.ValueChanged += _ => PushToCanvas();
        }

        var preview = new VBoxContainer
        {
            Name = "ImagePreviewPanel",
            CustomMinimumSize = new Vector2(1, 1),
            SizeFlagsHorizontal = SizeFlags.ExpandFill,
            SizeFlagsVertical = SizeFlags.ExpandFill
        };
        preview.AddThemeConstantOverride("separation", 8);
        split.AddChild(preview);

        var previewTitle = new Label
        {
            Name = "PreviewTitleLabel",
            Text = "文件预览",
            TooltipText = "文件预览",
            ClipText = true,
            TextOverrunBehavior = TextServer.OverrunBehavior.TrimEllipsis
        };
        previewTitle.AddThemeFontSizeOverride("font_size", 12);
        preview.AddChild(previewTitle);

        var viewport = new PanelContainer
        {
            Name = "ImagePreviewViewport",
            CustomMinimumSize = new Vector2(1, 1),
            SizeFlagsHorizontal = SizeFlags.ExpandFill,
            SizeFlagsVertical = SizeFlags.ExpandFill,
            ClipContents = true
        };
        preview.AddChild(viewport);

        _filePreviewTexture = new TextureRect
        {
            Name = "SelectedImagePreview",
            ExpandMode = TextureRect.ExpandModeEnum.IgnoreSize,
            StretchMode = TextureRect.StretchModeEnum.KeepAspectCentered,
            SizeFlagsHorizontal = SizeFlags.ExpandFill,
            SizeFlagsVertical = SizeFlags.ExpandFill,
            ClipContents = true
        };
        viewport.AddChild(_filePreviewTexture);
        _filePreviewTexture.SetAnchorsPreset(LayoutPreset.FullRect);
        _filePreviewTexture.SetOffsetsPreset(LayoutPreset.FullRect);

        _filePreviewStatus = new Label
        {
            AutoTranslateMode = AutoTranslateModeEnum.Disabled,
            TooltipAutoTranslateMode = AutoTranslateModeEnum.Disabled,
            Name = "PreviewStatusLabel",
            Text = Tr("从左侧文件列表选择图片后预览。"),
            AutowrapMode = TextServer.AutowrapMode.Off,
            ClipText = true,
            TextOverrunBehavior = TextServer.OverrunBehavior.TrimEllipsis,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
            SizeFlagsHorizontal = SizeFlags.ExpandFill,
            SizeFlagsVertical = SizeFlags.Fill,
            CustomMinimumSize = new Vector2(1, 0)
        };
        _filePreviewStatus.AddThemeFontSizeOverride("font_size", 10);
        _filePreviewStatus.AddThemeColorOverride("font_color", AppAgasCs.UI.Theming.AgasSharedIdeTheme.MutedTextColor);
        preview.AddChild(_filePreviewStatus);

        _sceneReadmeView = new VBoxContainer
        {
            Name = "SceneReadmePanel",
            SizeFlagsHorizontal = SizeFlags.ExpandFill,
            SizeFlagsVertical = SizeFlags.ExpandFill,
            Visible = false
        };
        _sceneReadmeView.AddThemeConstantOverride("separation", 8);
        viewRoot.AddChild(_sceneReadmeView);

        var readmeTitle = new Label { Name = "SceneReadmeTitle", Text = "场景说明", TooltipText = "场景说明", ClipText = true, TextOverrunBehavior = TextServer.OverrunBehavior.TrimEllipsis };
        readmeTitle.AddThemeFontSizeOverride("font_size", 12);
        _sceneReadmeView.AddChild(readmeTitle);

        _sceneReadmeEdit = new TextEdit
        {
            AutoTranslateMode = AutoTranslateModeEnum.Disabled,
            Name = "SceneReadmeEdit",
            PlaceholderText = Tr("场景说明"),
            WrapMode = TextEdit.LineWrappingMode.Boundary,
            SizeFlagsHorizontal = SizeFlags.ExpandFill,
            SizeFlagsVertical = SizeFlags.ExpandFill
        };
        _sceneReadmeEdit.TextChanged += OnSceneReadmeTextChanged;
        _sceneReadmeView.AddChild(_sceneReadmeEdit);

        SyncEditableState();
    }

    private void OnSceneReadmeTextChanged()
    {
        if (!_readmeSyncing && _editable)
        {
            SceneReadmeChanged?.Invoke(_sceneReadmeEdit?.Text ?? string.Empty);
        }
    }

    private void SetFilePreviewMessage(string message, bool empty, params object[] arguments)
    {
        if (_filePreviewStatus == null)
        {
            return;
        }

        _previewMessage = message;
        _previewArguments = arguments;
        _filePreviewStatus.Text = string.Format(Tr(message), arguments);
        _filePreviewStatus.TooltipText = _filePreviewStatus.Text;
        _filePreviewStatus.HorizontalAlignment = empty ? HorizontalAlignment.Center : HorizontalAlignment.Left;
        _filePreviewStatus.VerticalAlignment = empty ? VerticalAlignment.Center : VerticalAlignment.Top;
    }

    private static LineEdit AddReadOnlyField(VBoxContainer content, string label)
    {
        var row = new VBoxContainer();
        row.AddThemeConstantOverride("separation", 3);
        row.AddChild(CreateFieldLabel(label));
        var field = new LineEdit { AutoTranslateMode = AutoTranslateModeEnum.Disabled, Editable = false, SizeFlagsHorizontal = SizeFlags.ExpandFill };
        row.AddChild(field);
        content.AddChild(row);
        return field;
    }

    private static SpinBox AddNumberField(VBoxContainer content, string label, double min, double max, double step)
    {
        var row = new VBoxContainer();
        row.AddThemeConstantOverride("separation", 3);
        row.AddChild(CreateFieldLabel(label));
        var field = new SpinBox
        {
            AutoTranslateMode = AutoTranslateModeEnum.Disabled,
            MinValue = min,
            MaxValue = max,
            Step = step,
            SizeFlagsHorizontal = SizeFlags.ExpandFill
        };
        row.AddChild(field);
        content.AddChild(row);
        return field;
    }

    private static Label CreateFieldLabel(string text)
    {
        var label = new Label { Text = text, AutowrapMode = TextServer.AutowrapMode.WordSmart, SizeFlagsHorizontal = SizeFlags.ExpandFill };
        label.AddThemeFontSizeOverride("font_size", 10);
        label.AddThemeColorOverride("font_color", AppAgasCs.UI.Theming.AgasSharedIdeTheme.MutedTextColor);
        return label;
    }

    private void SyncFromImage()
    {
        _syncing = true;
        bool hasImage = _image != null;
        if (_emptyLabel != null)
        {
            _emptyLabel.Visible = !hasImage;
            _emptyLabel.Text = "未选择图片实例。\n选中画布中的图片后可编辑位置、缩放、旋转与透明度。";
        }

        SetLine(_imagePath, _image?.ImagePath ?? string.Empty);
        SetLine(_order, _image?.Order.ToString() ?? string.Empty);
        SetSpin(_x, _image?.X ?? 0);
        SetSpin(_y, _image?.Y ?? 0);
        SetSpin(_scaleX, _image?.ScaleX ?? 1);
        SetSpin(_scaleY, _image?.ScaleY ?? 1);
        SetSpin(_rotation, _image?.RotationDegrees ?? 0);
        SetSpin(_opacity, _image?.Opacity ?? 1);
        _syncing = false;
    }

    private static void SetLine(LineEdit? field, string value)
    {
        if (field != null)
        {
            field.Text = value;
        }
    }

    private static void SetSpin(SpinBox? field, double value)
    {
        if (field != null)
        {
            field.Value = value;
        }
    }

    private void SyncEditableState()
    {
        if (_sceneReadmeEdit != null) _sceneReadmeEdit.Editable = _editable;
        foreach (SpinBox? spin in new[] { _x, _y, _scaleX, _scaleY, _rotation, _opacity })
        {
            if (spin != null)
            {
                spin.Editable = _editable;
            }
        }
    }

    private void PushToCanvas()
    {
        if (_syncing
            || !_editable
            || !CanMutate("修改图片属性")
            || _canvas == null
            || _image == null
            || _x == null
            || _y == null
            || _scaleX == null
            || _scaleY == null
            || _rotation == null
            || _opacity == null)
        {
            return;
        }

        _canvas.UpdateSelectedImageProperties(
            _x.Value,
            _y.Value,
            _scaleX.Value,
            _scaleY.Value,
            _rotation.Value,
            _opacity.Value,
            undoable: true);
    }

    private bool CanMutate(string action)
    {
        if (MutationGuard == null)
        {
            return true;
        }

        try
        {
            return MutationGuard(action);
        }
        catch (Exception exception)
        {
            GD.PushError($"Properties mutation guard failed closed for '{action}': {exception}");
            return false;
        }
    }
}
