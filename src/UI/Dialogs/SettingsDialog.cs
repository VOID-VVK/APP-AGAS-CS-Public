using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using AppAgasCs.Features.Ai.Settings;
using AppAgasCs.Features.Ai.UI;
using AppAgasCs.UI.Shared;
using AppAgasCs.UI.Theming;
using Godot;

namespace AGAS.UI.Dialogs;

public partial class SettingsDialog : AcceptDialog
{
    private const int AiCategoryIndex = 0;
    private const int AppearanceCategoryIndex = 1;
    private const int McpCategoryIndex = 2;

    private readonly AgasAiSettingsStore _store;
    private readonly AgasAiSettingsFieldMergeService _fieldMerge;
    private readonly AgasAiSettingsUiMutationBridge _mcpMutationBridge;
    private readonly AgasUiAppearanceSettingsStore _appearanceStore;
    private AgasAiProviderCatalog _providerCatalog;
    private readonly ItemList _categories = new()
    {
        Name = "SettingsCategories",
        AccessibilityName = "设置分类",
        CustomMinimumSize = new Vector2(210, 1),
        SelectMode = ItemList.SelectModeEnum.Single,
    };
    private readonly VBoxContainer _aiPage = new() { Name = "AiSettingsPage" };
    private readonly HSplitContainer _aiSplit = new()
    {
        Name = "AiSettingsSplit",
        SizeFlagsHorizontal = Control.SizeFlags.ExpandFill,
        SizeFlagsVertical = Control.SizeFlags.ExpandFill,
        SplitOffsets = new[] { 300 },
    };
    private readonly VBoxContainer _appearancePage = new() { Name = "AppearanceSettingsPage", Visible = false };
    private readonly VBoxContainer _mcpPage = new() { Name = "McpSettingsPage", Visible = false };
    private readonly OptionButton _language = new()
    {
        Name = "UiLanguage",
        AutoTranslateMode = AutoTranslateModeEnum.Disabled,
        SizeFlagsHorizontal = Control.SizeFlags.ExpandFill,
        SizeFlagsVertical = Control.SizeFlags.ShrinkCenter,
        CustomMinimumSize = new Vector2(144, 0),
        FitToLongestItem = false,
        ClipText = true,
        TextOverrunBehavior = TextServer.OverrunBehavior.TrimEllipsis,
    };
    private readonly SpinBox _fontSize = new()
    {
        Name = "UiFontSize",
        MinValue = AgasUiAppearanceSettings.MinimumFontSize,
        MaxValue = int.MaxValue,
        Step = 1,
        Value = AgasUiAppearanceSettings.DefaultFontSize,
        Suffix = " px",
        AllowGreater = true,
        AllowLesser = false,
        SizeFlagsHorizontal = Control.SizeFlags.ExpandFill,
    };
    private readonly Button _restoreAppearanceButton = new()
    {
        Name = "RestoreDefaultAppearance",
        Text = "恢复默认",
    };
    private readonly Label _appearanceStatus = new()
    {
        Name = "AppearanceSettingsStatus",
        AutoTranslateMode = AutoTranslateModeEnum.Disabled,
        AutowrapMode = TextServer.AutowrapMode.WordSmart,
    };
    private readonly ItemList _profiles = new()
    {
        Name = "AiProfileList",
        AutoTranslateMode = AutoTranslateModeEnum.Disabled,
        AccessibilityName = "已配置连接",
        TooltipText = "选择要编辑和用于后续 AI 任务的连接",
        CustomMinimumSize = new Vector2(280, 1),
        SizeFlagsVertical = Control.SizeFlags.ExpandFill,
        SelectMode = ItemList.SelectModeEnum.Single,
    };
    private readonly LineEdit _nameEdit = CreateLineEdit("AiProfileName", "例如：AI 主连接");
    private readonly LineEdit _baseUrlEdit = CreateLineEdit("AiBaseUrl", "https://example.com/v1/");
    private readonly Label _baseUrlHint = new()
    {
        Name = "AiBaseUrlHint",
        Text = "填写统一生图网关地址；切换模型不会修改此地址。",
        AutowrapMode = TextServer.AutowrapMode.WordSmart,
    };
    private readonly LineEdit _keyEdit = new()
    {
        Name = "AiApiKey",
        AutoTranslateMode = AutoTranslateModeEnum.Disabled,
        Secret = true,
        PlaceholderText = "输入 API Key",
        SizeFlagsHorizontal = Control.SizeFlags.ExpandFill,
    };
    private readonly Label _credentialFieldLabel = new()
    {
        Name = "AiCredentialLabel",
        Text = "API Key",
    };
    private readonly Label _credentialHint = new()
    {
        Name = "AiApiKeyMaskedHint",
        AutoTranslateMode = AutoTranslateModeEnum.Disabled,
        AutowrapMode = TextServer.AutowrapMode.WordSmart,
    };
    private readonly Label _aiStatus = new()
    {
        Name = "AiSettingsStatus",
        AutoTranslateMode = AutoTranslateModeEnum.Disabled,
        AutowrapMode = TextServer.AutowrapMode.WordSmart,
    };
    private readonly McpSettingsContent _mcpContent = new();
    private readonly Button _deleteButton = new()
    {
        Name = "DeleteAiProfile",
        Text = "删除连接",
        AccessibilityName = "删除 AI 连接",
        TooltipText = "删除所选连接及其已保存的凭据",
    };
    private readonly Button _addButton = new()
    {
        Name = "AddAiProfile",
        Text = "添加连接",
        AccessibilityName = "添加 AI 连接",
        TooltipText = "添加一组独立的服务地址和密钥",
        SizeFlagsHorizontal = Control.SizeFlags.ExpandFill,
    };
    private readonly Button _clearKeyButton = new() { Name = "ClearAiCredential", Text = "清除 API Key" };
    private readonly Button _saveButton = new()
    {
        Name = "SaveAiProfile",
        Text = "保存连接",
        AccessibilityName = "保存 AI 连接",
        TooltipText = "保存此连接的名称、地址和密钥",
    };
    private readonly ConfirmationDialog _deleteConfirmation = new()
    {
        Name = "ConfirmDeleteAiProfile",
        Title = "删除连接",
        DialogText = "确认删除所选连接及其已保存的凭据？此操作不可撤销。",
        OkButtonText = "删除连接",
    };
    private readonly ConfirmationDialog _clearKeyConfirmation = new()
    {
        Name = "ConfirmClearAiCredential",
        Title = "清除 API Key",
        DialogText = "确认清除所选连接已保存的 API Key？",
        OkButtonText = "清除",
    };

    private AgasAiProfilesDocument _document = AgasAiProfilesDocument.Empty;
    private string? _selectedProfileId;
    private bool _applyingProfile;
    private bool _applyingAppearance;
    private AgasUiAppearanceSettings _appearanceSettings = AgasUiAppearanceSettings.Default;
    private string _appearanceStatusTemplate = string.Empty;
    private object[] _appearanceStatusArguments = Array.Empty<object>();
    private string _aiStatusMessage = string.Empty;
    private object[] _aiStatusArguments = Array.Empty<object>();
    private string _credentialMask = string.Empty;
    private bool _applyingCredentialText;
    private bool _credentialEdited;
    private AgasAiSettingsDraftState _drafts;
    private long _mutationGeneration;
    private bool _mcpMutationUiLocked;
    private string _mutationWorkspaceId = string.Empty;
    private Action? _mcpSettingsRequested;
    private bool _compactNavigation;

    public SettingsDialog()
        : this(new AgasAiSettingsStore(), new AgasUiAppearanceSettingsStore())
    {
    }

    internal SettingsDialog(AgasAiSettingsStore store)
        : this(store, new AgasUiAppearanceSettingsStore(store.HomeDirectory))
    {
    }

    internal SettingsDialog(AgasAiSettingsStore store, AgasUiAppearanceSettingsStore appearanceStore)
    {
        _store = store;
        _fieldMerge = new AgasAiSettingsFieldMergeService(store);
        _mcpMutationBridge = new AgasAiSettingsUiMutationBridge(ApplyMcpCommittedDocumentOnMainThread);
        _appearanceStore = appearanceStore;
        _providerCatalog = AgasAiProviderCatalog.Load(store.HomeDirectory);
        Title = "设置";
        MinSize = new Vector2I(1100, 700);
        // The form scrolls independently of its footer. Do not let transient
        // wrapped-label minimum sizes grow the window beyond the host viewport.
        WrapControls = false;
        Unresizable = false;
        OkButtonText = "关闭";

        AddChild(BuildContent());
        SizeChanged += UpdateCompactNavigation;
        _categories.ItemSelected += OnCategorySelected;
        _profiles.ItemSelected += OnProfileSelected;
        _nameEdit.TextChanged += _ => MarkDraft(name: true);
        _baseUrlEdit.TextChanged += _ => MarkDraft(baseUrl: true);
        _keyEdit.TextChanged += OnCredentialTextChanged;
        _fontSize.ValueChanged += OnFontSizeChanged;
        _language.AddItem("English", 0);
        _language.AddItem("简体中文", 1);
        _language.ItemSelected += OnLanguageSelected;
        _restoreAppearanceButton.Pressed += RestoreDefaultAppearance;
        _saveButton.Pressed += SaveSelectedProfile;
        _deleteButton.Pressed += RequestDeleteSelectedProfile;
        _clearKeyButton.Pressed += RequestClearCredential;
        _deleteConfirmation.Confirmed += DeleteSelectedProfile;
        _clearKeyConfirmation.Confirmed += ClearSelectedCredential;
        AddChild(_deleteConfirmation);
        AddChild(_clearKeyConfirmation);

        _categories.AddItem("AI 连接");
        _categories.AddItem("外观");
        _categories.AddItem("MCP");
        _categories.Select(AiCategoryIndex);
        ApplyCredentialPresentation(hasCredential: false);
        SetDetailEnabled(false);
        RefreshTranslations();
    }

    public override void _Notification(int what)
    {
        if (what == NotificationTranslationChanged)
        {
            RefreshTranslations();
        }
    }

    public event Action? McpSettingsRequested
    {
        add => _mcpSettingsRequested += value;
        remove => _mcpSettingsRequested -= value;
    }
    public event Func<bool, Task>? McpEnabledChangeRequested
    {
        add => _mcpContent.EnabledChangeRequested += value;
        remove => _mcpContent.EnabledChangeRequested -= value;
    }
    public event Action<string>? ActiveProfileChanged;
    public event Action<int>? AppearanceFontSizeChanged;

    public string? ActiveProfileId => _document.ActiveProfileId;
    internal AgasAiSettingsUiMutationBridge McpAiMutationBridge => _mcpMutationBridge;

    internal bool StageMcpAiMutation(string? workspaceId)
    {
        _mutationWorkspaceId = workspaceId ?? string.Empty;
        PublishMutationSnapshot(workspaceId);
        if (!_mcpMutationBridge.StageAcquireOnMainThread())
            return false;
        SetMcpMutationUiLocked(true);
        return true;
    }

    internal void FinishMcpAiMutation()
    {
        if (_mcpMutationBridge.IsLeased)
            _mcpMutationBridge.FinishWithoutCommitOnMainThread();
        SetMcpMutationUiLocked(false);
    }

    internal void UpdateMcpAiMutationWorkspace(string? workspaceId)
    {
        _mutationWorkspaceId = workspaceId ?? string.Empty;
        AdvanceGeneration();
    }

    internal AgasAiEffectiveProfileSelection? CaptureEffectiveAiSelection()
    {
        AgasAiProfile? profile = _document.Profiles.FirstOrDefault(item =>
            string.Equals(item.Id, _selectedProfileId, StringComparison.Ordinal));
        return profile == null
            ? null
            : new AgasAiEffectiveProfileSelection(
                profile.Id,
                AgasAiProviderIds.AxonCatNewApi,
                AgasAiUnifiedModelCatalog.ConnectionAdapterKind,
                _document.ActiveModelName);
    }

    public void Open(Node parent)
    {
        if (GetParent() == null)
        {
            parent.AddChild(this);
        }

        ReloadAppearance();
        ReloadProfiles();
        if (parent is Main main) main.BindMcpSettingsPage(this);
        Vector2 available = parent.GetViewport().GetVisibleRect().Size - new Vector2(48, 48);
        Rect2I usableScreen = default;
        if (!IsEmbedded() && DisplayServer.GetName() != "headless")
        {
            usableScreen = DisplayServer.ScreenGetUsableRect(parent.GetWindow().CurrentScreen);
            available = available.Min((Vector2)usableScreen.Size - new Vector2(48, 48));
        }
        var popupSize = new Vector2I(
            Math.Max(1, Math.Min(1600, (int)available.X)),
            Math.Max(1, Math.Min(1000, (int)available.Y)));
        MinSize = new Vector2I(Math.Min(1100, popupSize.X), Math.Min(700, popupSize.Y));
        PopupCentered(popupSize);
        if (usableScreen.HasArea())
        {
            Position = new Vector2I(
                Math.Clamp(Position.X, usableScreen.Position.X + 24, usableScreen.End.X - Size.X - 24),
                Math.Clamp(Position.Y, usableScreen.Position.Y + 24, usableScreen.End.Y - Size.Y - 24));
        }
    }

    public void UpdateMcpState(McpSettingsStatus status, bool enabled, string? lastError)
    {
        _mcpContent.UpdateState(status, lastError, enabled);
    }

    private void UpdateCompactNavigation()
    {
        bool compact = Size.X < 1100;
        if (compact == _compactNavigation) return;
        _compactNavigation = compact;
        _categories.CustomMinimumSize = new Vector2(compact ? 140 : 210, 1);
        _profiles.CustomMinimumSize = new Vector2(compact ? 160 : 280, 1);
        _aiSplit.SplitOffsets = new[] { compact ? 160 : 300 };
    }

    private Control BuildContent()
    {
        MarginContainer margin = AgasMarginContainerFactory.Create(8, 8, 8, 8, "SettingsMargin");
        // Wrapped labels can lower their minimum height after initial layout.
        // AcceptDialog does not otherwise refit its children when that happens.
        margin.MinimumSizeChanged += () =>
        {
            if (Visible) Notification((int)NotificationWMSizeChanged);
        };

        var split = new HSplitContainer
        {
            Name = "SettingsSplit",
            SizeFlagsHorizontal = Control.SizeFlags.ExpandFill,
            SizeFlagsVertical = Control.SizeFlags.ExpandFill,
        };
        margin.AddChild(split);
        split.AddChild(_categories);

        var pages = new PanelContainer
        {
            Name = "SettingsDetail",
            SizeFlagsHorizontal = Control.SizeFlags.ExpandFill,
            SizeFlagsVertical = Control.SizeFlags.ExpandFill,
        };
        split.AddChild(pages);

        MarginContainer pageStack = AgasMarginContainerFactory.Create(8, 8, 8, 8);
        pages.AddChild(pageStack);
        pageStack.AddChild(BuildAiPage());
        pageStack.AddChild(BuildAppearancePage());
        pageStack.AddChild(BuildMcpPage());
        return margin;
    }

    private Control BuildAiPage()
    {
        _aiPage.SizeFlagsHorizontal = Control.SizeFlags.ExpandFill;
        _aiPage.SizeFlagsVertical = Control.SizeFlags.ExpandFill;
        _aiPage.AddThemeConstantOverride("separation", 8);
        var pageScroll = new ScrollContainer
        {
            Name = "AiSettingsPageScroll",
            SizeFlagsHorizontal = Control.SizeFlags.ExpandFill,
            SizeFlagsVertical = Control.SizeFlags.ExpandFill,
        };
        _aiPage.AddChild(pageScroll);

        var pageContent = new VBoxContainer
        {
            SizeFlagsHorizontal = Control.SizeFlags.ExpandFill,
            SizeFlagsVertical = Control.SizeFlags.ExpandFill,
        };
        pageContent.AddThemeConstantOverride("separation", 8);
        pageScroll.AddChild(pageContent);
        pageContent.AddChild(CreatePageTitle("AI 连接"));

        HSplitContainer split = _aiSplit;
        pageContent.AddChild(split);

        var listColumn = new VBoxContainer
        {
            SizeFlagsVertical = Control.SizeFlags.ExpandFill,
        };
        listColumn.AddThemeConstantOverride("separation", 8);
        listColumn.AddChild(CreateSectionLabel("已配置连接"));
        _profiles.SizeFlagsVertical = Control.SizeFlags.ExpandFill;
        listColumn.AddChild(_profiles);
        _addButton.Pressed += AddProfile;
        listColumn.AddChild(_addButton);
        split.AddChild(listColumn);

        var detailColumn = new VBoxContainer
        {
            SizeFlagsHorizontal = Control.SizeFlags.ExpandFill,
            SizeFlagsVertical = Control.SizeFlags.ExpandFill,
        };
        detailColumn.AddThemeConstantOverride("separation", 8);
        split.AddChild(detailColumn);

        var detailsScroll = new ScrollContainer
        {
            Name = "AiProfileDetailsScroll",
            CustomMinimumSize = new Vector2(0, 180),
            SizeFlagsHorizontal = Control.SizeFlags.ExpandFill,
            SizeFlagsVertical = Control.SizeFlags.ExpandFill,
        };
        detailColumn.AddChild(detailsScroll);

        var details = new VBoxContainer
        {
            Name = "AiProfileDetails",
            SizeFlagsHorizontal = Control.SizeFlags.ExpandFill,
            SizeFlagsVertical = Control.SizeFlags.ShrinkBegin,
            CustomMinimumSize = new Vector2(560, 1),
        };
        details.AddThemeConstantOverride("separation", 18);
        detailsScroll.AddChild(details);

        _nameEdit.AccessibilityName = "连接名称";
        _nameEdit.TooltipText = "用于区分多个 AI 连接";
        _baseUrlEdit.AccessibilityName = "连接服务地址";
        _baseUrlEdit.TooltipText = "所有生图模型共用此连接地址";
        _keyEdit.AccessibilityName = "连接 API Key";
        _keyEdit.TooltipText = "所有生图模型共用此连接密钥";
        details.AddChild(CreateFormSection(
            "连接信息",
            CreateCaption("在左侧选择连接即可启用；顶部工具栏负责切换生图模型。"),
            CreateField("连接名称", _nameEdit)));

        details.AddChild(CreateFormSection(
            "地址与密钥",
            CreateCaption("此连接的地址和密钥供 GPT Image、Grok 与 Gemini 模型共用。"),
            CreateField("Base URL", _baseUrlEdit),
            StyleCaption(_baseUrlHint),
            CreateField(_credentialFieldLabel, _keyEdit),
            StyleCaption(_credentialHint),
            CreateCaption("不会回显原值。留空表示保持已保存的 Key。")));

        _aiStatus.SizeFlagsHorizontal = Control.SizeFlags.ExpandFill;
        _aiStatus.AddThemeColorOverride("font_color", AgasSharedIdeTheme.MutedTextColor);
        details.AddChild(_aiStatus);

        detailColumn.AddChild(new HSeparator());
        var footer = new HFlowContainer
        {
            Name = "AiProfileActions",
            SizeFlagsHorizontal = Control.SizeFlags.ExpandFill,
            Alignment = FlowContainer.AlignmentMode.End,
        };
        footer.AddThemeConstantOverride("h_separation", 4);
        footer.AddThemeConstantOverride("v_separation", 4);
        _clearKeyButton.SizeFlagsVertical = Control.SizeFlags.ShrinkCenter;
        _deleteButton.SizeFlagsVertical = Control.SizeFlags.ShrinkCenter;
        _saveButton.SizeFlagsVertical = Control.SizeFlags.ShrinkCenter;
        footer.AddChild(_clearKeyButton);
        footer.AddChild(_deleteButton);
        footer.AddChild(_saveButton);
        detailColumn.AddChild(footer);
        return _aiPage;
    }

    private Control BuildMcpPage()
    {
        _mcpPage.SizeFlagsHorizontal = Control.SizeFlags.ExpandFill;
        _mcpPage.SizeFlagsVertical = Control.SizeFlags.ExpandFill;
        _mcpPage.AddThemeConstantOverride("separation", 12);
        _mcpPage.AddChild(CreatePageTitle("MCP"));
        var scroll = new ScrollContainer
        {
            Name = "McpSettingsScroll",
            SizeFlagsHorizontal = Control.SizeFlags.ExpandFill,
            SizeFlagsVertical = Control.SizeFlags.ExpandFill,
            HorizontalScrollMode = ScrollContainer.ScrollMode.Disabled,
        };
        scroll.AddChild(_mcpContent);
        _mcpPage.AddChild(scroll);
        return _mcpPage;
    }

    private Control BuildAppearancePage()
    {
        _appearancePage.SizeFlagsHorizontal = Control.SizeFlags.ExpandFill;
        _appearancePage.SizeFlagsVertical = Control.SizeFlags.ExpandFill;
        _appearancePage.AddThemeConstantOverride("separation", 12);
        _appearancePage.AddChild(CreatePageTitle("外观"));
        var scroll = new ScrollContainer
        {
            Name = "AppearanceSettingsScroll",
            SizeFlagsHorizontal = Control.SizeFlags.ExpandFill,
            SizeFlagsVertical = Control.SizeFlags.ExpandFill,
            HorizontalScrollMode = ScrollContainer.ScrollMode.Disabled,
        };
        var content = new VBoxContainer
        {
            SizeFlagsHorizontal = Control.SizeFlags.ExpandFill,
            SizeFlagsVertical = Control.SizeFlags.ShrinkBegin,
        };
        content.AddThemeConstantOverride("separation", 12);
        scroll.AddChild(content);
        _appearancePage.AddChild(scroll);
        content.AddChild(CreateAppearanceField("界面语言", _language));
        content.AddChild(CreateAppearanceField("全局字体大小", _fontSize));
        content.AddChild(CreateCaption("调整整个应用的界面字体大小。更改会立即预览并自动保存。"));
        var actions = new HBoxContainer { Alignment = BoxContainer.AlignmentMode.End };
        _restoreAppearanceButton.SizeFlagsVertical = Control.SizeFlags.ShrinkCenter;
        actions.AddChild(_restoreAppearanceButton);
        content.AddChild(actions);
        content.AddChild(StyleCaption(_appearanceStatus));
        return _appearancePage;
    }

    private static Control CreateAppearanceField(string text, Control editor)
    {
        var field = new VBoxContainer { SizeFlagsHorizontal = Control.SizeFlags.ExpandFill };
        field.AddThemeConstantOverride("separation", 8);
        field.AddChild(new Label
        {
            Text = text,
            AutowrapMode = TextServer.AutowrapMode.WordSmart,
            SizeFlagsHorizontal = Control.SizeFlags.ExpandFill,
        });
        editor.SizeFlagsHorizontal = Control.SizeFlags.ExpandFill;
        editor.SizeFlagsVertical = Control.SizeFlags.ShrinkCenter;
        field.AddChild(editor);
        return field;
    }

    private void ReloadProfiles(string? preferredId = null)
    {
        try
        {
            _providerCatalog = AgasAiProviderCatalog.Load(_store.HomeDirectory);
            _document = AgasAiNewApiSetup.EnsureProfile(_store, _providerCatalog);
            ResetDrafts();
            _profiles.Clear();
            foreach (AgasAiProfile profile in _document.Profiles)
            {
                _profiles.AddItem(profile.Name);
                _profiles.SetItemMetadata(_profiles.ItemCount - 1, profile.Id);
            }
            RefreshProfileLabels();

            string? id = preferredId ?? _document.ActiveProfileId ?? _document.Profiles.FirstOrDefault()?.Id;
            int index = FindProfileIndex(id);
            if (index >= 0)
            {
                _profiles.Select(index);
                SelectProfile(_document.Profiles[index]);
            }
            else
            {
                _selectedProfileId = null;
                ClearDetails();
            }
            bool retired = index >= 0 && AgasAiConnectionModelResolver.UsesRetiredProtocol(_document.Profiles[index], _providerCatalog);
            SetStatus(retired
                ? "旧 Google 私有协议已停用。请填写统一 API 地址和对应的新 Key 后保存。"
                : _store.CredentialMigrationWarning
                    ?? "选择左侧连接即可启用；所有模型共用该连接的 Base URL 和 API Key。",
                retired);
        }
        catch
        {
            _document = AgasAiProfilesDocument.Empty;
            _profiles.Clear();
            ClearDetails();
            SetStatus("加载 AI 连接失败。", true);
        }
    }

    private void AddProfile()
    {
        if (_mcpMutationUiLocked) return;
        try
        {
            int ordinal = _document.Profiles.Count + 1;
            var profile = new AgasAiProfile(
                Guid.NewGuid().ToString("N"),
                AppLocale.Format("AI 连接 {0}", ordinal),
                AgasAiProviderIds.AxonCatNewApi,
                _document.ActiveModelName,
                AgasAiProfile.DefaultBaseUrl);
            _document = _fieldMerge.Add(profile);
            AdvanceGeneration();
            ReloadProfiles(profile.Id);
            ActiveProfileChanged?.Invoke(profile.Id);
        }
        catch
        {
            SetStatus("添加 AI 连接失败。", true);
        }
    }

    private void SaveSelectedProfile()
    {
        if (_mcpMutationUiLocked || string.IsNullOrWhiteSpace(_selectedProfileId))
        {
            return;
        }

        try
        {
            AgasAiProfile current = _document.Profiles.First(profile =>
                string.Equals(profile.Id, _selectedProfileId, StringComparison.Ordinal));
            AgasAiProfile updated = current with
            {
                Name = _nameEdit.Text.Trim(),
                BaseUrl = _baseUrlEdit.Text.Trim(),
                ProviderId = AgasAiProviderIds.AxonCatNewApi,
            };
            string? credential = _credentialEdited && !string.IsNullOrWhiteSpace(_keyEdit.Text)
                ? _keyEdit.Text
                : null;
            _document = _fieldMerge.UpdateProfileAndCredential(updated, _drafts, credential);
            AdvanceGeneration();
            ReloadProfiles(updated.Id);
            SetStatus(_store.HasCredential(updated.Id)
                ? "已保存。所有模型将使用此连接的地址和 Key。"
                : "已保存。请为当前服务地址填写 API Key。", false);
            ActiveProfileChanged?.Invoke(updated.Id);
        }
        catch (System.IO.InvalidDataException exception)
        {
            SetCredentialEditorText(string.Empty);
            SetStatus(ConnectionUiMessage(exception.Message), true);
        }
        catch
        {
            SetCredentialEditorText(string.Empty);
            SetStatus("保存 AI 连接失败；Key 未被输出。", true);
        }
    }

    private void RequestDeleteSelectedProfile()
    {
        if (!_mcpMutationUiLocked && !string.IsNullOrWhiteSpace(_selectedProfileId))
        {
            _deleteConfirmation.PopupCentered();
        }
    }

    private void DeleteSelectedProfile()
    {
        if (_mcpMutationUiLocked || string.IsNullOrWhiteSpace(_selectedProfileId))
        {
            return;
        }

        try
        {
            string deletedId = _selectedProfileId;
            _document = _fieldMerge.Delete(deletedId);
            string? activeId = _document.ActiveProfileId;
            AdvanceGeneration();
            ReloadProfiles(activeId);
            if (activeId != null)
            {
                ActiveProfileChanged?.Invoke(activeId);
            }
        }
        catch
        {
            SetStatus("删除 AI 连接失败；原配置已尽力恢复。", true);
        }
    }

    private void RequestClearCredential()
    {
        if (_mcpMutationUiLocked) return;
        try
        {
            if (!string.IsNullOrWhiteSpace(_selectedProfileId) && _store.HasCredential(_selectedProfileId))
            {
                _clearKeyConfirmation.PopupCentered();
            }
        }
        catch
        {
            SetStatus("读取凭据状态失败；凭据未被输出。", true);
        }
    }

    private void ClearSelectedCredential()
    {
        if (_mcpMutationUiLocked || string.IsNullOrWhiteSpace(_selectedProfileId))
        {
            return;
        }

        try
        {
            _fieldMerge.ClearCredential(_selectedProfileId);
            AdvanceGeneration();
            _credentialMask = string.Empty;
            SetCredentialEditorText(string.Empty);
            ApplyCredentialPresentation(hasCredential: false);
            SetStatus("已清除 API Key。", false);
        }
        catch
        {
            SetCredentialEditorText(string.Empty);
            SetStatus("清除凭据失败；凭据未被输出。", true);
        }
    }

    private void OnCategorySelected(long index)
    {
        _aiPage.Visible = index == AiCategoryIndex;
        _appearancePage.Visible = index == AppearanceCategoryIndex;
        _mcpPage.Visible = index == McpCategoryIndex;
    }

    private void OnProfileSelected(long index)
    {
        if (_mcpMutationUiLocked || index < 0 || index >= _document.Profiles.Count)
        {
            return;
        }

        AgasAiProfile profile = _document.Profiles[(int)index];
        try
        {
            if (!string.Equals(_document.ActiveProfileId, profile.Id, StringComparison.Ordinal))
            {
                _document = _fieldMerge.Activate(profile.Id);
                AdvanceGeneration();
                ActiveProfileChanged?.Invoke(profile.Id);
            }
            ReloadProfiles(profile.Id);
            SetStatus("已启用连接：{0}", false, profile.Name);
        }
        catch
        {
            SetStatus("切换当前 AI 连接失败。", true);
            ReloadProfiles();
        }
    }

    private void SelectProfile(AgasAiProfile profile)
    {
        _applyingProfile = true;
        try
        {
            _selectedProfileId = profile.Id;
            _nameEdit.Text = profile.Name;
            _baseUrlEdit.Text = profile.BaseUrl;
            string? credential = _store.GetCredential(profile.Id);
            bool hasCredential = !string.IsNullOrEmpty(credential);
            _credentialMask = hasCredential ? CreateCredentialMask(credential!) : string.Empty;
            SetCredentialEditorText(string.Empty);
            ApplyCredentialPresentation(hasCredential);
            SetDetailEnabled(true);
            ResetDrafts();
        }
        finally
        {
            _applyingProfile = false;
        }
    }

    private int FindProfileIndex(string? profileId)
    {
        if (string.IsNullOrWhiteSpace(profileId))
        {
            return -1;
        }

        for (int index = 0; index < _document.Profiles.Count; index++)
        {
            if (_document.Profiles[index].Id == profileId)
            {
                return index;
            }
        }
        return -1;
    }

    private void ClearDetails()
    {
        _applyingProfile = true;
        _nameEdit.Text = string.Empty;
        _baseUrlEdit.Text = string.Empty;
        _credentialMask = string.Empty;
        SetCredentialEditorText(string.Empty);
        ApplyCredentialPresentation(hasCredential: false);
        SetDetailEnabled(false);
        _applyingProfile = false;
    }

    private void SetDetailEnabled(bool enabled)
    {
        enabled &= !_mcpMutationUiLocked;
        _nameEdit.Editable = enabled;
        _baseUrlEdit.Editable = enabled;
        _keyEdit.Editable = enabled;
        _saveButton.Disabled = !enabled;
        _deleteButton.Disabled = !enabled;
        if (!enabled)
        {
            _clearKeyButton.Disabled = true;
        }
    }

    private void SetStatus(string message, bool error, params object[] arguments)
    {
        _aiStatusMessage = message;
        _aiStatusArguments = arguments;
        RefreshAiStatusTranslation();
        _aiStatus.AddThemeColorOverride(
            "font_color",
            error ? AgasSharedIdeTheme.ErrorTextColor : AgasSharedIdeTheme.MutedTextColor);
    }

    private void RefreshAiStatusTranslation()
    {
        _aiStatus.Text = _aiStatusArguments.Length == 0
            ? AppLocale.Translate(_aiStatusMessage)
            : AppLocale.Format(_aiStatusMessage, _aiStatusArguments);
    }

    private static string ConnectionUiMessage(string message) =>
        string.Equals(message, "AI 中转站名称必须为 1-120 个字符。", StringComparison.Ordinal)
            ? "AI 连接名称必须为 1-120 个字符。"
            : message;

    private void ApplyCredentialPresentation(bool hasCredential)
    {
        _credentialFieldLabel.Text = "API Key";
        _credentialFieldLabel.TooltipText = "API Key";
        _credentialHint.Text = hasCredential
            ? AppLocale.Format("已保存：{0}", _credentialMask)
            : AppLocale.Translate("未保存 API Key。");
        _keyEdit.PlaceholderText = AppLocale.Translate("输入 API Key");
        _keyEdit.AccessibilityName = AppLocale.Translate("连接 API Key");
        _keyEdit.TooltipText = AppLocale.Translate("所有生图模型共用此连接密钥");
        _clearKeyButton.Text = "清除 API Key";
        _clearKeyButton.AccessibilityName = "清除当前连接的 API Key";
        _clearKeyButton.TooltipText = "清除当前连接已保存的 API Key";
        _clearKeyButton.Disabled = !hasCredential || _mcpMutationUiLocked;
        _clearKeyConfirmation.Title = "清除 API Key";
        _clearKeyConfirmation.DialogText = "确认清除所选连接已保存的 API Key？";
    }

    private void ReloadAppearance()
    {
        _appearanceSettings = _appearanceStore.Load();
        _applyingAppearance = true;
        try
        {
            _fontSize.Value = _appearanceSettings.FontSize;
            _language.Select(_appearanceSettings.UiLocale == AgasUiAppearanceSettings.SimplifiedChineseLocale ? 1 : 0);
        }
        finally
        {
            _applyingAppearance = false;
        }
        ApplyAppearancePreview(_appearanceSettings.FontSize);
        AppLocale.Apply(_appearanceSettings.UiLocale);
        SetAppearanceStatus("当前字体大小：{0}px", false, _appearanceSettings.FontSize);
    }

    private void OnFontSizeChanged(double value)
    {
        if (_applyingAppearance)
        {
            return;
        }

        int fontSize = ToValidFontSize(value);
        AgasUiAppearanceSettings next = _appearanceSettings with { FontSize = fontSize };
        try
        {
            _appearanceStore.Save(next);
            _appearanceSettings = next;
            ApplyAppearancePreview(fontSize);
            SetAppearanceStatus("已应用并保存：{0}px", false, fontSize);
        }
        catch
        {
            _applyingAppearance = true;
            try
            {
                _fontSize.Value = _appearanceSettings.FontSize;
            }
            finally
            {
                _applyingAppearance = false;
            }
            SetAppearanceStatus("保存外观设置失败，已保留原字体大小。", true);
        }
    }

    private void OnLanguageSelected(long index)
    {
        if (_applyingAppearance)
        {
            return;
        }

        string locale = index == 1
            ? AgasUiAppearanceSettings.SimplifiedChineseLocale
            : AgasUiAppearanceSettings.DefaultLocale;
        AgasUiAppearanceSettings next = _appearanceSettings with { UiLocale = locale };
        try
        {
            _appearanceStore.Save(next);
            _appearanceSettings = next;
            AppLocale.Apply(locale);
            SetAppearanceStatus("语言已应用并保存。", false);
        }
        catch
        {
            _applyingAppearance = true;
            try
            {
                _language.Select(_appearanceSettings.UiLocale == AgasUiAppearanceSettings.SimplifiedChineseLocale ? 1 : 0);
            }
            finally
            {
                _applyingAppearance = false;
            }
            SetAppearanceStatus("保存语言设置失败，已保留原语言。", true);
        }
    }

    private void SetAppearanceStatus(string template, bool error, params object[] arguments)
    {
        _appearanceStatusTemplate = template;
        _appearanceStatusArguments = arguments;
        _appearanceStatus.Text = AppLocale.Format(template, arguments);
        _appearanceStatus.AddThemeColorOverride(
            "font_color",
            error ? AgasSharedIdeTheme.ErrorTextColor : AgasSharedIdeTheme.MutedTextColor);
    }

    private void RefreshTranslations()
    {
        bool applyingAppearance = _applyingAppearance;
        _applyingAppearance = true;
        try
        {
            if (_language.ItemCount == 2)
            {
                _language.Select(AppLocale.Current == AgasUiAppearanceSettings.SimplifiedChineseLocale ? 1 : 0);
            }
        }
        finally
        {
            _applyingAppearance = applyingAppearance;
        }
        _language.AccessibilityName = AppLocale.Translate("界面语言");
        if (_language.Selected >= 0)
        {
            _language.TooltipText = _language.GetItemText(_language.Selected);
        }
        _profiles.AccessibilityName = AppLocale.Translate("已配置连接");
        _profiles.TooltipText = AppLocale.Translate("选择要编辑和用于后续 AI 任务的连接");
        _nameEdit.PlaceholderText = AppLocale.Translate("例如：AI 主连接");
        _nameEdit.AccessibilityName = AppLocale.Translate("连接名称");
        _nameEdit.TooltipText = AppLocale.Translate("用于区分多个 AI 连接");
        _baseUrlEdit.AccessibilityName = AppLocale.Translate("连接服务地址");
        _baseUrlEdit.TooltipText = AppLocale.Translate("所有生图模型共用此连接地址");
        RefreshAiStatusTranslation();
        _appearanceStatus.Text = AppLocale.Format(_appearanceStatusTemplate, _appearanceStatusArguments);
        ApplyCredentialPresentation(_credentialMask.Length > 0);
        RefreshProfileLabels();
    }

    private void RefreshProfileLabels()
    {
        for (int index = 0; index < Math.Min(_profiles.ItemCount, _document.Profiles.Count); index++)
        {
            AgasAiProfile profile = _document.Profiles[index];
            bool active = string.Equals(profile.Id, _document.ActiveProfileId, StringComparison.Ordinal);
            _profiles.SetItemText(index, active ? AppLocale.Format("{0}  当前", profile.Name) : profile.Name);
            _profiles.SetItemTooltip(index, AppLocale.Translate(active ? "当前启用连接" : "点击以启用此连接"));
        }
    }

    private void RestoreDefaultAppearance()
    {
        if ((int)_fontSize.Value == AgasUiAppearanceSettings.DefaultFontSize)
        {
            OnFontSizeChanged(AgasUiAppearanceSettings.DefaultFontSize);
            return;
        }
        _fontSize.Value = AgasUiAppearanceSettings.DefaultFontSize;
    }

    private void ApplyAppearancePreview(int fontSize)
    {
        Theme = AgasSharedIdeTheme.Create(fontSize);
        AgasSharedIdeTheme.ApplyScaledFontOverrides(this, fontSize);
        AppearanceFontSizeChanged?.Invoke(fontSize);
    }

    private static int ToValidFontSize(double value)
    {
        if (double.IsNaN(value)
            || double.IsNegativeInfinity(value)
            || value < AgasUiAppearanceSettings.MinimumFontSize)
        {
            return AgasUiAppearanceSettings.DefaultFontSize;
        }
        if (double.IsPositiveInfinity(value) || value >= int.MaxValue)
        {
            return int.MaxValue;
        }

        return Math.Max(
            AgasUiAppearanceSettings.MinimumFontSize,
            (int)Math.Round(value, MidpointRounding.AwayFromZero));
    }

    private static string CreateCredentialMask(string credential)
    {
        if (credential.Length <= 6)
        {
            return new string('*', Math.Max(8, credential.Length));
        }

        return $"{credential[..3]}********{credential[^3..]}";
    }

    private void OnCredentialTextChanged(string _)
    {
        if (!_applyingCredentialText)
        {
            _credentialEdited = true;
            MarkDraft(credential: true);
        }
    }

    private void SetCredentialEditorText(string text)
    {
        _applyingCredentialText = true;
        try
        {
            _keyEdit.Text = text;
        }
        finally
        {
            _applyingCredentialText = false;
            _credentialEdited = false;
        }
    }

    private void MarkDraft(
        bool name = false,
        bool provider = false,
        bool model = false,
        bool baseUrl = false,
        bool credential = false)
    {
        if (_applyingProfile || _mcpMutationUiLocked)
            return;
        _drafts = new AgasAiSettingsDraftState(
            _drafts.Name || name,
            _drafts.Provider || provider,
            _drafts.Model || model,
            _drafts.BaseUrl || baseUrl,
            _drafts.Credential || credential);
        AdvanceGeneration();
    }

    private void ResetDrafts()
    {
        _drafts = default;
        _credentialEdited = false;
        PublishMutationSnapshot(_mutationWorkspaceId);
    }

    private void AdvanceGeneration()
    {
        _mutationGeneration = checked(_mutationGeneration + 1);
        PublishMutationSnapshot(_mutationWorkspaceId);
    }

    private void PublishMutationSnapshot(string? workspaceId) =>
        _mcpMutationBridge.PublishMainThreadSnapshot(
            _drafts,
            _mutationGeneration,
            workspaceId ?? string.Empty,
            _document.ActiveProfileId);

    private void ApplyMcpCommittedDocumentOnMainThread(AgasAiProfilesDocument committed)
    {
        _document = committed;
        AdvanceGeneration();
        ReloadProfiles(committed.ActiveProfileId);
        ActiveProfileChanged?.Invoke(committed.ActiveProfileId ?? string.Empty);
    }

    private void SetMcpMutationUiLocked(bool locked)
    {
        _mcpMutationUiLocked = locked;
        _profiles.MouseFilter = locked ? Control.MouseFilterEnum.Ignore : Control.MouseFilterEnum.Stop;
        _addButton.Disabled = locked;
        bool enabled = !locked && !string.IsNullOrWhiteSpace(_selectedProfileId);
        _nameEdit.Editable = enabled;
        _baseUrlEdit.Editable = enabled;
        _keyEdit.Editable = enabled;
        _saveButton.Disabled = !enabled;
        _deleteButton.Disabled = !enabled;
        _clearKeyButton.Disabled = !enabled || _credentialMask.Length == 0;
    }

    private static Label CreatePageTitle(string text)
    {
        var title = CreateSingleLineLabel(text);
        title.AddThemeFontSizeOverride("font_size", 18);
        return title;
    }

    private static Label CreateSectionLabel(string text)
    {
        var label = CreateSingleLineLabel(text);
        label.AddThemeFontSizeOverride("font_size", 12);
        label.AddThemeColorOverride("font_color", AgasSharedIdeTheme.MutedTextColor);
        return label;
    }

    private static Label CreateCaption(string text)
    {
        var caption = new Label
        {
            Text = text,
            AutowrapMode = TextServer.AutowrapMode.WordSmart,
        };
        return StyleCaption(caption);
    }

    private static Label StyleCaption(Label caption)
    {
        caption.AutowrapMode = TextServer.AutowrapMode.WordSmart;
        caption.AddThemeFontSizeOverride("font_size", 12);
        caption.AddThemeColorOverride("font_color", AgasSharedIdeTheme.MutedTextColor);
        return caption;
    }

    private static Control CreateFormSection(string title, params Control[] children)
    {
        var section = new VBoxContainer { SizeFlagsHorizontal = Control.SizeFlags.ExpandFill };
        section.AddThemeConstantOverride("separation", 8);
        var heading = CreateSingleLineLabel(title);
        heading.AddThemeFontSizeOverride("font_size", 13);
        section.AddChild(heading);
        section.AddChild(new HSeparator());
        foreach (Control child in children)
        {
            section.AddChild(child);
        }
        return section;
    }

    private static LineEdit CreateLineEdit(string name, string placeholder) => new()
    {
        Name = name,
        AutoTranslateMode = AutoTranslateModeEnum.Disabled,
        PlaceholderText = placeholder,
        SizeFlagsHorizontal = Control.SizeFlags.ExpandFill,
    };

    private static Control CreateField(string label, Control editor)
    {
        return CreateField(new Label { Text = label }, editor);
    }

    private static Control CreateField(Label label, Control editor)
    {
        var row = new HBoxContainer { SizeFlagsHorizontal = Control.SizeFlags.ExpandFill };
        row.AddThemeConstantOverride("separation", 16);
        label.CustomMinimumSize = new Vector2(108, 1);
        label.ClipText = true;
        label.TextOverrunBehavior = TextServer.OverrunBehavior.TrimEllipsis;
        label.TooltipText = label.Text;
        label.SizeFlagsVertical = Control.SizeFlags.ShrinkCenter;
        label.VerticalAlignment = VerticalAlignment.Center;
        row.AddChild(label);
        editor.SizeFlagsHorizontal = Control.SizeFlags.ExpandFill;
        editor.SizeFlagsVertical = Control.SizeFlags.ShrinkCenter;
        row.AddChild(editor);
        return row;
    }

    private static Label CreateSingleLineLabel(string text) => new()
    {
        Text = text,
        TooltipText = text,
        ClipText = true,
        TextOverrunBehavior = TextServer.OverrunBehavior.TrimEllipsis,
        SizeFlagsHorizontal = Control.SizeFlags.ExpandFill,
        SizeFlagsVertical = Control.SizeFlags.ShrinkCenter,
    };
}
