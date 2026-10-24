#nullable enable

using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using AppAgasCs.Application.Contracts;
using AppAgasCs.Features.Ai.Generation;
using AppAgasCs.Features.Ai.Settings;
using AppAgasCs.Features.Ai.Workspace;
using AppAgasCs.UI.Shared;
using AppAgasCs.UI.Theming;
using Agas.Features.Canvas;
using Godot;

namespace AppAgasCs.Features.Ai.UI;

public partial class AiGenerationPanel : PanelContainer
{
    private const int ReplaceTargetIndex = 0;
    private const int AddTargetIndex = 1;
    public const int MaximumConcurrentGenerations = AgasAiGenerationCoordinator.DefaultMaximumConcurrentGenerations;
    private const int PreparingOverlayRunId = 0;
    private const string GenerateIdleText = "生成";
    private const string GeneratePreparingText = "准备中…";
    private const ulong FailureOverlayDurationMsec = 10_000;
    private const ulong CanceledOverlayDurationMsec = 4_000;
    private static readonly Dictionary<string, string> ApplicationMessageAliases = new(StringComparer.Ordinal)
    {
        ["Layer was not found."] = "找不到目标图层，无法应用生成结果。",
        ["Target layer is hidden and cannot be modified."] = "目标图层已隐藏，无法应用生成结果。",
        ["Target layer is locked and cannot be modified."] = "目标图层已锁定，无法应用生成结果。",
    };
    private static readonly HashSet<string> ApplicationMessageKeys = new(StringComparer.Ordinal)
    {
        "请先在设置中添加 AI 连接。",
        "旧凭据未能导入，原文件已保留。请为需要的旧连接重新填写 API Key。",
        "AI 连接配置加载失败。",
        "AI 连接配置读取失败。",
        "当前 AI 连接不可用。",
        "当前生图模型不可用。",
        "AI 连接凭据读取失败。",
        "所选连接尚未配置 API Key。",
        "所选连接配置无效。",
        "AI 生成控制器不可用。",
        "请先框选 Q 参考区域。",
        "W 必须位于 Q 区域内。",
        "Q 参考区域最大支持 8192×8192 像素。",
        "Q 参考区域尺寸过大。",
        "当前项目已关闭，无法准备区域生成请求。",
        "画布区域或参考合成图无法准备，请检查 Q/W 区域及图片资源。",
        "区域生成准备器不可用，未发送普通整图生成请求。",
        "当前模型不支持质量档位；请切换到 Grok Imagine Image 2.0。",
        "当前模型不支持完整 Q 区域生图。",
        "当前模型不支持局部区域编辑（W）。请清除 W 后生成完整 Q 区域，或切换到支持局部编辑的模型。",
        "当前项目已关闭，无法应用生成结果。",
        "请求期间项目已切换，未应用生成结果。",
        "原场景已不存在，未应用生成结果。",
        "提交后画布内容已变化，未应用生成结果。",
        "生成结果正在写入，跳过重复回写。",
        "完整 Q 生成结果无法合成，未修改画布。",
        "目标图层不可编辑，完整 Q 生成结果无法写回。",
        "目标图层不可编辑，完整 Q 生成结果无法写回原场景。",
        "已把完整 Q 生成结果覆盖写入请求时的图层；项目尚未保存。",
        "图片已应用，但自动保存失败，请按 Ctrl/Cmd+S 重试。",
        "已把完整 Q 生成结果写入请求时的图层并自动保存；可使用撤销恢复。",
        "图片已写入原场景，但自动保存失败，请切回该场景后按 Ctrl/Cmd+S 重试。",
        "已把完整 Q 生成结果写入请求时的场景图层并保存。切回该场景即可看到。",
        "自动应用生成结果失败，画布未修改。",
        "生成结果没有可用的画布接收方。",
        "切回原场景后自动写入失败，画布未修改。",
        "生成候选路径无效。",
        "生成候选为空。",
        "生成候选无法读取。",
        "区域合成结果不是有效 PNG。",
        "生成目录无法创建。",
        "区域合成结果无法保存。",
    };

    private readonly IAiGenerationRunner _runner;
    private readonly IAgasAiGenerationCoordinator _coordinator;
    private readonly bool _ownsRunner;
    private readonly OptionButton _profile = new()
    {
        Name = "AiGenerationProfile",
        AutoTranslateMode = AutoTranslateModeEnum.Disabled,
        FitToLongestItem = false,
        ClipText = true,
        TextOverrunBehavior = TextServer.OverrunBehavior.TrimEllipsis,
        CustomMinimumSize = new Vector2(120, 0),
        SizeFlagsHorizontal = SizeFlags.ExpandFill,
    };
    private readonly OptionButton _provider = new()
    {
        Name = "AiGenerationProvider",
        FitToLongestItem = false,
        ClipText = true,
        TextOverrunBehavior = TextServer.OverrunBehavior.TrimEllipsis,
        CustomMinimumSize = new Vector2(120, 0),
        SizeFlagsHorizontal = SizeFlags.ExpandFill,
    };
    private readonly OptionButton _modelPreset = new()
    {
        Name = "AiGenerationModelPreset",
        AutoTranslateMode = AutoTranslateModeEnum.Disabled,
        FitToLongestItem = false,
        ClipText = true,
        TextOverrunBehavior = TextServer.OverrunBehavior.TrimEllipsis,
        CustomMinimumSize = new Vector2(120, 0),
        SizeFlagsHorizontal = SizeFlags.ExpandFill,
    };
    private readonly LineEdit _modelName = new()
    {
        Name = "AiGenerationModelName",
        AutoTranslateMode = AutoTranslateModeEnum.Disabled,
        PlaceholderText = "具体模型名",
        SizeFlagsHorizontal = SizeFlags.ExpandFill,
    };
    private readonly Label _modelRecommendation = new()
    {
        Name = "AiGenerationModelRecommendation",
        AutowrapMode = TextServer.AutowrapMode.WordSmart,
    };
    private readonly OptionButton _target = new()
    {
        Name = "AiGenerationTarget",
        FitToLongestItem = false,
        ClipText = true,
        TextOverrunBehavior = TextServer.OverrunBehavior.TrimEllipsis,
        CustomMinimumSize = new Vector2(120, 0),
        SizeFlagsHorizontal = SizeFlags.ExpandFill,
    };
    private readonly TextEdit _prompt = new()
    {
        Name = "AiGenerationPrompt",
        AutoTranslateMode = AutoTranslateModeEnum.Disabled,
        PlaceholderText = "描述要生成的图片",
        CustomMinimumSize = new Vector2(1, 92),
        SizeFlagsHorizontal = SizeFlags.ExpandFill,
    };
    private readonly Label _regionStatus = new()
    {
        Name = "AiGenerationRegionStatus",
        AutoTranslateMode = AutoTranslateModeEnum.Disabled,
        AutowrapMode = TextServer.AutowrapMode.WordSmart,
    };
    private readonly Label _status = new()
    {
        Name = "AiGenerationStatus",
        AutoTranslateMode = AutoTranslateModeEnum.Disabled,
        AutowrapMode = TextServer.AutowrapMode.WordSmart,
    };
    private readonly Button _generate = new()
    {
        Name = "AiGenerateButton",
        AutoTranslateMode = AutoTranslateModeEnum.Disabled,
        Text = GenerateIdleText,
        ClipText = true,
        TextOverrunBehavior = TextServer.OverrunBehavior.TrimEllipsis,
        CustomMinimumSize = new Vector2(112, 0),
        SizeFlagsHorizontal = SizeFlags.ExpandFill,
    };
    private readonly Button _cancel = new()
    {
        Name = "AiCancelButton",
        Text = "取消",
        Disabled = true,
        AutoTranslateMode = AutoTranslateModeEnum.Disabled,
        ClipText = true,
        TextOverrunBehavior = TextServer.OverrunBehavior.TrimEllipsis,
        CustomMinimumSize = new Vector2(112, 0),
        SizeFlagsHorizontal = SizeFlags.ExpandFill,
    };
    private AiGenerationQuality _selectedQuality = AiGenerationQuality.Medium;
    private readonly AiGenerationProfileController _profileController;
    private readonly List<PendingGenerationStart> _pendingStarts = new();
    private readonly Texture2D _generateLoadingIcon = CreateGenerateLoadingIcon();
    private AiGenerationCanvasContext? _context;
    private bool _replaceWasAvailable;
    private bool _exiting;
    private bool _isPreparingGeneration;
    private bool _flushPendingStartsQueued;
    private AiGenerationOverlayState? _preparingOverlay;
    private readonly Dictionary<int, ActiveGeneration> _activeGenerations = new();
    private readonly Dictionary<int, TerminalGenerationOverlay> _terminalOverlays = new();
    private readonly List<ParkedWriteBack> _parkedWriteBacks = new();
    private bool _flushingParkedWriteBacks;
    private int _nextGenerationRunId;
    private Func<string>? _statusText;
    private bool _statusIsError;

    private sealed record TerminalGenerationOverlay(
        AiGenerationOverlayState State,
        Func<string> RenderText,
        ulong ExpiresMsec);

    private sealed class ActiveGeneration
    {
        public ActiveGeneration(
            int runId,
            CancellationTokenSource cancellation,
            IAgasAiGenerationPermit permit,
            AiGenerationCanvasContext context,
            Rect2 worldRect,
            ulong startedMsec)
        {
            RunId = runId;
            Cancellation = cancellation;
            Permit = permit;
            Context = context;
            WorldRect = worldRect;
            StartedMsec = startedMsec;
        }

        public int RunId { get; }
        public CancellationTokenSource Cancellation { get; }
        public IAgasAiGenerationPermit Permit { get; }
        public AiGenerationCanvasContext Context { get; }
        public Rect2 WorldRect { get; }
        public ulong StartedMsec { get; }
        public int LastElapsedSeconds { get; set; } = -1;
    }

    private sealed record PendingGenerationStart(
        AiGenerationCanvasContext Context,
        AiGenerationProviderProfile Profile,
        AiGenerationTarget Target,
        string Prompt,
        AiGenerationGridRegion Reference,
        AiGenerationQuality? Quality);

    private sealed record ParkedWriteBack(
        int RunId,
        AiGeneratedImageApplication Application,
        AiGenerationCanvasContext Context,
        Rect2 WorldRect,
        ulong StartedMsec);

    public AiGenerationPanel()
        : this(
            new AgasAiSettingsStore(),
            new AiGenerationServiceRunner(),
            new AgasAiGenerationCoordinator(),
            ownsRunner: true)
    {
    }

    public AiGenerationPanel(AgasAiSettingsStore settingsStore, IAiGenerationRunner runner)
        : this(settingsStore, runner, new AgasAiGenerationCoordinator(), ownsRunner: false)
    {
    }

    public AiGenerationPanel(AgasAiSettingsStore settingsStore, IAiGenerationRunner runner, AgasAiModelDiscovery discovery)
        : this(settingsStore, runner, new AgasAiGenerationCoordinator(), ownsRunner: false, discovery)
    {
    }

    public AiGenerationPanel(AgasAiSettingsStore settingsStore, IAiGenerationRunner runner,
        IAgasAiGenerationCoordinator coordinator, AgasAiModelDiscovery discovery)
        : this(settingsStore, runner, coordinator, ownsRunner: false, discovery)
    {
    }

    public AiGenerationPanel(
        AgasAiSettingsStore settingsStore,
        IAiGenerationRunner runner,
        IAgasAiGenerationCoordinator coordinator)
        : this(settingsStore, runner, coordinator, ownsRunner: false)
    {
    }

    public AiGenerationPanel(IAgasAiGenerationCoordinator coordinator)
        : this(new AgasAiSettingsStore(), new AiGenerationServiceRunner(), coordinator, ownsRunner: true)
    {
    }

    private AiGenerationPanel(
        AgasAiSettingsStore settingsStore,
        IAiGenerationRunner runner,
        IAgasAiGenerationCoordinator coordinator,
        bool ownsRunner,
        AgasAiModelDiscovery? discovery = null)
    {
        ArgumentNullException.ThrowIfNull(settingsStore);
        _runner = runner ?? throw new ArgumentNullException(nameof(runner));
        _coordinator = coordinator ?? throw new ArgumentNullException(nameof(coordinator));
        _ownsRunner = ownsRunner;
        _profileController = new AiGenerationProfileController(
            settingsStore,
            _profile,
            _provider,
            _modelPreset,
            _modelName,
            _modelRecommendation,
            discovery);
    }

    public Func<AiGeneratedImageApplication, AiGeneratedImageApplyResult>? ApplyGeneratedImage { get; set; }

    public Func<AiGenerationPreparationInput, AiGenerationPreparationResult>? PrepareGenerationRequest { get; set; }

    public Action<AiGeneratedImageApplication>? DiscardGeneratedImage { get; set; }

    public int ActiveGenerationCount => _activeGenerations.Count;

    public int ParkedWriteBackCount => _parkedWriteBacks.Count;

    public bool IsPreparingGeneration => _isPreparingGeneration || _pendingStarts.Count > 0;

    public IReadOnlyList<AiGenerationProfileOption> ProfileOptions => _profileController.ProfileOptions;

    public string? SelectedProfileId => _profileController.SelectedProfileId;

    public string ActiveModelName => _profileController.ActiveModelName;

    public AiGenerationQuality SelectedQuality => _selectedQuality;

    public AiGenerationExternalJobStatus? LatestExternalJobStatus { get; private set; }

    public AiGenerationOverlayState? GenerationOverlayState => GenerationOverlayStates.FirstOrDefault();

    public IReadOnlyList<AiGenerationOverlayState> GenerationOverlayStates => Array.AsReadOnly(
        VisibleOverlays(
                (_preparingOverlay == null
                    ? Array.Empty<AiGenerationOverlayState>()
                    : new[] { _preparingOverlay with { Text = Tr("AI 准备中…") } })
                    .Concat(_activeGenerations.Values.Select(CreateOverlayState))
                    .Concat(_parkedWriteBacks.Select(CreateParkedOverlayState))
                    .Concat(_terminalOverlays.Values.Select(terminal => terminal.State with { Text = terminal.RenderText() })))
            .OrderBy(state => state.RunId)
            .ThenBy(state => state.Phase)
            .ToArray());

    public Action<IReadOnlyList<AiGenerationOverlayState>>? GenerationOverlaysChanged { get; set; }

    public event Action<string, bool>? StatusChanged;

    public event Action? ProfilesChanged;

    public void ReportExternalJobStatus(AiGenerationExternalJobStatus status)
    {
        ArgumentNullException.ThrowIfNull(status);
        if (_exiting)
        {
            return;
        }

        LatestExternalJobStatus = status;
        if (_activeGenerations.Count > 0)
        {
            // A manual task owns the panel's primary status while it is
            // running. Preserve the external state for inspection without
            // replacing the user's active-task feedback.
            return;
        }

        SetStatus(() => FormatExternalJobStatus(status), status.State == AiGenerationExternalJobState.Failed);
    }

    private string FormatExternalJobStatus(AiGenerationExternalJobStatus status)
    {
        string state = status.State switch
        {
            AiGenerationExternalJobState.Queued => "排队中",
            AiGenerationExternalJobState.Running => "生成中",
            AiGenerationExternalJobState.Succeeded => "生成完成，待应用",
            AiGenerationExternalJobState.Failed => "生成失败",
            AiGenerationExternalJobState.Canceled => "已取消",
            AiGenerationExternalJobState.Applied => "已应用，待保存",
            _ => "状态已更新",
        };
        string jobLabel = status.JobId.Length <= 14 ? status.JobId : status.JobId[..14];
        string detail = string.IsNullOrWhiteSpace(status.Message) ? string.Empty : FormatUi("：{0}", status.Message);
        return FormatUi("MCP AI {0}… {1}{2}", jobLabel, Tr(state), detail);
    }

    /// <summary>
    /// Sets the visible AI panel prompt text without starting generation.
    /// Used by MCP generate so the operator can see the submitted prompt.
    /// </summary>
    public void SetPromptText(string? prompt)
    {
        _prompt.Text = prompt ?? string.Empty;
    }

    public void BeginCanvasGeneration(
        AiGenerationCanvasContext context,
        string prompt,
        bool submit,
        string? preparationMessage = null,
        AiGenerationTarget? target = null)
    {
        ArgumentNullException.ThrowIfNull(context);
        SetCanvasContext(context);
        if (target.HasValue)
        {
            SelectTarget(target.Value);
        }
        SetPromptText(prompt);

        if (!TryResolveEffectiveGenerationRegion(context.RegionMetadata, out _, out string regionError))
        {
            SetStatus(() => FormatUi("{0} 当前未发送请求。", Tr(regionError)), true);
            return;
        }
        if (!string.IsNullOrWhiteSpace(preparationMessage))
        {
            SetStatus(() => TranslateApplicationMessage(preparationMessage), true);
            return;
        }
        if (submit)
        {
            StartGeneration();
        }
    }

    private void SelectTarget(AiGenerationTarget target)
    {
        int index = target switch
        {
            AiGenerationTarget.ReplaceSelected => ReplaceTargetIndex,
            AiGenerationTarget.AddToCurrentLayer => AddTargetIndex,
            _ => -1,
        };
        if (index >= 0 && index < _target.ItemCount && !_target.GetPopup().IsItemDisabled(index))
        {
            _target.Select(index);
            _target.TooltipText = Tr(_target.GetItemText(index));
        }
    }

    public override void _Ready()
    {
        Name = "AiGenerationPanel";
        BuildUi();
        _profile.GetPopup().AutoTranslateMode = AutoTranslateModeEnum.Disabled;
        _modelPreset.GetPopup().AutoTranslateMode = AutoTranslateModeEnum.Disabled;
        RefreshPlaceholders();
        _profileController.Connect((key, error, arguments) => SetStatus(() => FormatUi(key, arguments), error), OnProfileSelectionChanged);
        ReloadProfiles();
        RefreshContextUi();
        SetProcess(false);
    }

    public override void _Notification(int what)
    {
        if (what != NotificationTranslationChanged || !IsNodeReady() || _exiting)
        {
            return;
        }

        RefreshPlaceholders();
        RefreshRegionStatus();
        RefreshGenerateAvailability();
        RenderStatus(notify: false);
        GenerationOverlaysChanged?.Invoke(GenerationOverlayStates);
    }

    private void RefreshPlaceholders()
    {
        _prompt.PlaceholderText = Tr("描述要生成的图片");
        _modelName.PlaceholderText = Tr("具体模型名");
    }

    private void OnProfileSelectionChanged()
    {
        RefreshGenerateAvailability();
        // The toolbar owns the global model selector; refresh it after either
        // the connection or model state changes.
        ProfilesChanged?.Invoke();
    }

    public override void _Process(double delta)
    {
        if (_activeGenerations.Count == 0 && _terminalOverlays.Count == 0 && !_isPreparingGeneration)
        {
            SetProcess(false);
            return;
        }

        ulong now = Godot.Time.GetTicksMsec();
        bool changed = false;
        foreach (ActiveGeneration generation in _activeGenerations.Values)
        {
            int elapsedSeconds = ElapsedSeconds(generation.StartedMsec, now);
            if (elapsedSeconds == generation.LastElapsedSeconds)
            {
                continue;
            }
            generation.LastElapsedSeconds = elapsedSeconds;
            changed = true;
        }
        foreach (int runId in _terminalOverlays
                     .Where(pair => pair.Value.ExpiresMsec <= now)
                     .Select(pair => pair.Key)
                     .ToArray())
        {
            _terminalOverlays.Remove(runId);
            changed = true;
        }
        if (changed)
        {
            PublishGenerationOverlays();
        }
    }

    public override void _ExitTree()
    {
        _exiting = true;
        CancelAllGenerations(showTerminal: false);
        _terminalOverlays.Clear();
        GenerationOverlaysChanged = null;
        _profileController.Disconnect();
        _generate.Icon = null;
        if (GodotObject.IsInstanceValid(_generateLoadingIcon))
        {
            _generateLoadingIcon.Dispose();
        }
        if (_ownsRunner && _runner is IDisposable disposable)
        {
            disposable.Dispose();
        }
    }

    public void SetCanvasContext(AiGenerationCanvasContext? context)
    {
        if (_exiting)
        {
            return;
        }
        if (!SameProjectBoundary(_context, context))
        {
            CancelAllGenerations(showTerminal: false);
            DiscardParkedWriteBacks();
            _terminalOverlays.Clear();
        }
        _context = context;
        RefreshContextUi();
        PublishGenerationOverlays();
        if (!_flushingParkedWriteBacks && _parkedWriteBacks.Count > 0 && IsInsideTree())
        {
            Callable.From(TryFlushParkedWriteBacks).CallDeferred();
        }
    }

    public void ReloadProfiles(string? preferredProfileId = null)
    {
        if (_exiting)
        {
            return;
        }
        AiGenerationProfileReloadResult result = _profileController.ReloadProfiles(preferredProfileId);
        SetStatus(() => TranslateApplicationMessage(result.Message), result.IsError);

        RefreshGenerateAvailability();
        ProfilesChanged?.Invoke();
    }

    public bool SelectProfileById(string profileId)
    {
        if (!_profileController.SelectProfileById(profileId))
        {
            return false;
        }

        RefreshGenerateAvailability();
        ProfilesChanged?.Invoke();
        return true;
    }

    public bool SelectModelByName(string modelName)
    {
        if (!_profileController.SelectModelByName(modelName))
        {
            return false;
        }

        RefreshGenerateAvailability();
        ProfilesChanged?.Invoke();
        return true;
    }

    public void SelectQuality(AiGenerationQuality quality)
    {
        _selectedQuality = quality;
    }

    /// <summary>
    /// Resolves the provider profile used by a new task without exposing the
    /// credential through UI or MCP DTOs. A null id follows the current UI
    /// connection selection. The model always comes from the persisted global
    /// model choice, including when an explicit connection id is supplied.
    /// </summary>
    public AiGenerationProfileResolution ResolveProviderProfile(string? profileId = null)
    {
        return _profileController.ResolveProviderProfile(profileId);
    }

    private void BuildUi()
    {
        MarginContainer margin = AgasMarginContainerFactory.Create(10, 8, 10, 8);
        AddChild(margin);

        var scroll = new ScrollContainer
        {
            Name = "AiGenerationScroll",
            SizeFlagsHorizontal = SizeFlags.ExpandFill,
            SizeFlagsVertical = SizeFlags.ExpandFill,
            FollowFocus = true,
        };
        margin.AddChild(scroll);
        var root = new VBoxContainer
        {
            Name = "AiGenerationContent",
            SizeFlagsHorizontal = SizeFlags.ExpandFill,
        };
        root.AddThemeConstantOverride("separation", 8);
        scroll.AddChild(root);

        var profileRow = new VBoxContainer();
        var profileHeader = new HBoxContainer();
        profileHeader.AddChild(new Label
        {
            Text = "连接",
            TooltipText = "连接",
            ClipText = true,
            TextOverrunBehavior = TextServer.OverrunBehavior.TrimEllipsis,
            SizeFlagsHorizontal = SizeFlags.ExpandFill,
        });
        var refresh = new Button { Name = "RefreshAiProfiles", Text = "刷新" };
        refresh.Pressed += () => ReloadProfiles();
        profileHeader.AddChild(refresh);
        profileRow.AddChild(profileHeader);
        _profile.SizeFlagsHorizontal = SizeFlags.ExpandFill;
        profileRow.AddChild(_profile);
        root.AddChild(profileRow);

        Control providerRow = CreateFieldRow("模型分类", _provider);
        providerRow.Name = "AiGenerationProviderRow";
        providerRow.Visible = true;
        root.AddChild(providerRow);
        root.AddChild(CreateFieldRow("模型", _modelPreset));
        Control modelNameRow = CreateFieldRow("具体模型", _modelName);
        modelNameRow.Name = "AiGenerationModelNameRow";
        modelNameRow.Visible = false;
        root.AddChild(modelNameRow);
        _modelRecommendation.AddThemeColorOverride("font_color", AgasSharedIdeTheme.MutedTextColor);
        root.AddChild(_modelRecommendation);

        _target.AddItem("参考选中图片，新增完整 Q", (int)AiGenerationTarget.ReplaceSelected);
        _target.AddItem("新增完整 Q 到当前图层", (int)AiGenerationTarget.AddToCurrentLayer);
        _target.ItemSelected += index => _target.TooltipText = Tr(_target.GetItemText((int)index));
        root.AddChild(CreateFieldRow("生成方式", _target));
        root.AddChild(_prompt);

        _regionStatus.AddThemeColorOverride("font_color", AgasSharedIdeTheme.MutedTextColor);
        root.AddChild(_regionStatus);
        var limitation = new Label
        {
            Name = "AiGenerationRegionLimitation",
            Text = "Q 是发送和完整写回范围；W 只用于在发送前从 Q 中挖出透明洞。结果始终作为完整 Q 非破坏性新增到当前图层，不会删除底层旧图。最多可同时生成 5 个任务，单次请求最多等待 8 分钟。",
            AutowrapMode = TextServer.AutowrapMode.WordSmart,
        };
        limitation.AddThemeColorOverride("font_color", AgasSharedIdeTheme.MutedTextColor);
        root.AddChild(limitation);

        var actions = new HBoxContainer { Alignment = BoxContainer.AlignmentMode.End };
        actions.AddChild(_cancel);
        actions.AddChild(_generate);
        root.AddChild(actions);
        root.AddChild(_status);


        _generate.AddThemeConstantOverride("icon_max_width", 16);
        _generate.Pressed += StartGeneration;
        _cancel.Pressed += CancelAllGenerations;
    }

    private static Control CreateFieldRow(string label, Control field)
    {
        var row = new VBoxContainer();
        row.AddChild(new Label
        {
            Text = label,
            TooltipText = label,
            ClipText = true,
            TextOverrunBehavior = TextServer.OverrunBehavior.TrimEllipsis,
            SizeFlagsHorizontal = SizeFlags.ExpandFill,
        });
        row.AddChild(field);
        return row;
    }

    private static string FormatElapsed(int elapsedSeconds)
    {
        int safeSeconds = Math.Max(0, elapsedSeconds);
        return $"{safeSeconds / 60:00}:{safeSeconds % 60:00}";
    }

    private void StartGeneration()
    {
        if (_exiting || !IsInsideTree())
        {
            return;
        }
        if (_activeGenerations.Count + _pendingStarts.Count >= _coordinator.MaximumConcurrentGenerations)
        {
            SetStatus(() => FormatUi("最多同时生成 {0} 个任务。", _coordinator.MaximumConcurrentGenerations), true);
            return;
        }
        if (!TryCaptureGenerationStart(out PendingGenerationStart? start) || start == null)
        {
            return;
        }

        _pendingStarts.Add(start);
        BeginPreparingUi(start);
        QueueFlushPendingStarts();
    }

    private async void QueueFlushPendingStarts()
    {
        if (_flushPendingStartsQueued)
        {
            return;
        }

        // Wait for the next process frame so Godot can paint the loading
        // button/menu before the main thread composites the Q/W reference.
        _flushPendingStartsQueued = true;
        if (IsInsideTree())
        {
            await ToSignal(GetTree(), SceneTree.SignalName.ProcessFrame);
        }

        FlushPendingGenerationStarts();
    }

    private bool TryCaptureGenerationStart(out PendingGenerationStart? start)
    {
        start = null;
        // Capture the latest model/provider choice before the request freezes so
        // free-typed model names also become the next-launch default.
        if (_profileController.PersistActiveSelection(persistModelFields: true))
        {
            ProfilesChanged?.Invoke();
        }

        AiGenerationCanvasContext? context = _context;
        AgasAiProfile? storedProfile = _profileController.SelectedProfile();
        if (context == null
            || string.IsNullOrWhiteSpace(context.ProjectRoot)
            || string.IsNullOrWhiteSpace(context.ProjectId)
            || string.IsNullOrWhiteSpace(context.SceneId)
            || string.IsNullOrWhiteSpace(context.LayerId)
            || storedProfile == null)
        {
            SetStatus("当前项目、场景、图层或 AI 连接不可用。", true);
            return false;
        }
        if (!context.CanEditCurrentLayer)
        {
            SetStatus("当前图层隐藏或锁定，无法应用生成结果。", true);
            return false;
        }

        AiGenerationProfileResolution profileResolution = ResolveProviderProfile();
        if (!profileResolution.Success || profileResolution.Profile == null)
        {
            SetStatus(() => TranslateApplicationMessage(profileResolution.Message), true);
            return false;
        }

        AiGenerationTarget target = (AiGenerationTarget)_target.GetSelectedId();
        if (target == AiGenerationTarget.ReplaceSelected && !context.HasSelectedImage)
        {
            SetStatus("选中图片参考模式要求先在画布中选择图片。", true);
            return false;
        }
        if (!TryResolveEffectiveGenerationRegion(context.RegionMetadata, out _, out string regionError))
        {
            SetStatus(regionError, true);
            return false;
        }

        start = new PendingGenerationStart(
            context,
            profileResolution.Profile,
            target,
            _prompt.Text,
            context.RegionMetadata!.SelectionRegion!.Value,
            AiGenerationQualityPolicy.Supports(profileResolution.Profile.ModelName)
                ? _selectedQuality
                : null);
        return true;
    }

    private void FlushPendingGenerationStarts()
    {
        _flushPendingStartsQueued = false;
        PendingGenerationStart[] starts = _pendingStarts.ToArray();
        _pendingStarts.Clear();
        try
        {
            if (_exiting || !IsInsideTree() || !_isPreparingGeneration)
            {
                return;
            }

            foreach (PendingGenerationStart start in starts)
            {
                SubmitPreparedGeneration(start);
            }
        }
        finally
        {
            EndPreparingUi();
        }
    }

    private void SubmitPreparedGeneration(PendingGenerationStart start)
    {
        if (_activeGenerations.Count >= _coordinator.MaximumConcurrentGenerations)
        {
            SetStatus(() => FormatUi("最多同时生成 {0} 个任务。", _coordinator.MaximumConcurrentGenerations), true);
            return;
        }

        AiGenerationPreparationResult prepared;
        try
        {
            prepared = PrepareGenerationRequest?.Invoke(new AiGenerationPreparationInput(
                start.Profile,
                start.Context,
                start.Target,
                start.Prompt,
                start.Quality))
                ?? AiGenerationPreparationResult.Fail("区域生成准备器不可用，未发送普通整图生成请求。");
        }
        catch
        {
            SetStatus("画布区域或参考合成图无法准备，请检查 Q/W 区域及图片资源。", true);
            return;
        }
        if (!prepared.Success || prepared.Request == null || prepared.PatchCapture == null)
        {
            SetStatus(() => string.IsNullOrWhiteSpace(prepared.Message) ? Tr("无法准备区域生成请求。") : TranslateApplicationMessage(prepared.Message), true);
            return;
        }

        if (!_coordinator.TryAcquire(out IAgasAiGenerationPermit? permit) || permit == null)
        {
            SetStatus(() => FormatUi("最多同时生成 {0} 个任务。", _coordinator.MaximumConcurrentGenerations), true);
            return;
        }

        var cancellation = new CancellationTokenSource();
        int runId = checked(++_nextGenerationRunId);
        float gridSize = Math.Max(1, start.Context.GridSizePx);
        var generation = new ActiveGeneration(
            runId,
            cancellation,
            permit,
            start.Context,
            new Rect2(
                start.Reference.Column * gridSize,
                start.Reference.Row * gridSize,
                start.Reference.Width * gridSize,
                start.Reference.Height * gridSize),
            Godot.Time.GetTicksMsec());
        _activeGenerations.Add(runId, generation);
        PublishGenerationOverlays();
        RefreshGenerateAvailability();
        int activeCount = _activeGenerations.Count;
        SetStatus(() => FormatUi("任务 #{0} 已开始生成（当前 {1}/{2}）。", runId, activeCount, _coordinator.MaximumConcurrentGenerations), false);
        RunGenerationAsync(generation, start.Profile, prepared.Request, prepared.PatchCapture, start.Target);
    }

    private async void RunGenerationAsync(
        ActiveGeneration generation,
        AiGenerationProviderProfile profile,
        AiGenerationRequest request,
        CanvasPatchCapture patchCapture,
        AiGenerationTarget target)
    {
        AiGeneratedImageApplication? application = null;
        try
        {
            AiGenerationResult result = await _runner.GenerateAsync(profile, request, generation.Cancellation.Token);
            application = new AiGeneratedImageApplication(
                result,
                target,
                generation.Context.ProjectRoot,
                generation.Context.ProjectId,
                generation.Context.SceneId,
                generation.Context.LayerId,
                target == AiGenerationTarget.ReplaceSelected ? generation.Context.SelectedImageId : null,
                patchCapture,
                request.Operation,
                generation.Context.LayerScope,
                request.SceneName,
                request.LayerName);
            if (!IsGenerationActive(generation))
            {
                TryDiscardArtifact(application);
                return;
            }

            AiGeneratedImageApplyResult applied;
            try
            {
                applied = ApplyGeneratedImage?.Invoke(application)
                    ?? AiGeneratedImageApplyResult.Fail("生成结果没有可用的画布接收方。");
            }
            catch
            {
                applied = AiGeneratedImageApplyResult.Fail("自动应用生成结果失败，画布未修改。");
            }
            if (!applied.Success
                && !applied.Applied
                && applied.FailureReason == AiGeneratedImageApplyFailureReason.ContextStale
                && IsGenerationActive(generation))
            {
                ParkWriteBack(generation, application);
                SetStatus(() => FormatUi("任务 #{0} 已生成，切回原场景后自动写入。", generation.RunId), false);
                return;
            }

            TryDiscardArtifact(application);
            if (IsGenerationActive(generation))
            {
                if (!applied.Success)
                {
                    AddTerminalOverlay(
                        generation,
                        AiGenerationOverlayPhase.Failure,
                        () => FormatUi(applied.Applied
                            ? "AI #{0} 已应用：自动保存失败"
                            : "AI #{0} 失败：自动应用失败", generation.RunId),
                        FailureOverlayDurationMsec);
                }
                SetStatus(
                    () => FormatUi(applied.Success
                        ? "任务 #{0} 已自动应用：{1}"
                        : applied.Applied
                            ? "任务 #{0} 已自动应用，但自动保存失败：{1}"
                            : "任务 #{0} 自动应用失败：{1}", generation.RunId, TranslateApplicationMessage(applied.Message)),
                    !applied.Success);
            }
        }
        catch (AiGenerationException exception) when (exception.Code == AiGenerationErrorCode.RequestCanceled)
        {
            if (IsGenerationActive(generation))
            {
                AddTerminalOverlay(
                    generation,
                    AiGenerationOverlayPhase.Canceled,
                    () => FormatUi("AI #{0} 已取消", generation.RunId),
                    CanceledOverlayDurationMsec);
                SetStatus(() => FormatUi("任务 #{0} 已取消。", generation.RunId), false);
            }
        }
        catch (OperationCanceledException)
        {
            if (IsGenerationActive(generation))
            {
                AddTerminalOverlay(
                    generation,
                    AiGenerationOverlayPhase.Canceled,
                    () => FormatUi("AI #{0} 已取消", generation.RunId),
                    CanceledOverlayDurationMsec);
                SetStatus(() => FormatUi("任务 #{0} 已取消。", generation.RunId), false);
            }
        }
        catch (AiGenerationException exception)
        {
            if (IsGenerationActive(generation))
            {
                AiGenerationSafeDiagnostic diagnostic = exception.Diagnostic
                    ?? AiGenerationDiagnostics.Create(
                        exception.Code,
                        AiGenerationFailurePhase.PanelRunner,
                        AiGenerationDiagnostics.CategoryFor(exception.Code),
                        profile);
                if (exception.Diagnostic == null)
                {
                    AiGenerationDiagnostics.DefaultSink.TryWrite(diagnostic);
                }
                AddTerminalOverlay(
                    generation,
                    AiGenerationOverlayPhase.Failure,
                    () => FormatUi("AI #{0} 失败：{1}", generation.RunId, ShortFailureReason(diagnostic)),
                    FailureOverlayDurationMsec);
                SetStatus(() => FormatUi("任务 #{0} 生成失败：{1}", generation.RunId, FormatDiagnostic(diagnostic)), true);
            }
        }
        catch (Exception exception)
        {
            if (IsGenerationActive(generation))
            {
                AiGenerationSafeDiagnostic diagnostic = AiGenerationDiagnostics.Create(
                    AiGenerationErrorCode.ProviderRequestFailed,
                    AiGenerationFailurePhase.PanelRunner,
                    AiGenerationDiagnostics.CategoryFor(exception),
                    profile);
                AiGenerationDiagnostics.DefaultSink.TryWrite(diagnostic);
                AddTerminalOverlay(
                    generation,
                    AiGenerationOverlayPhase.Failure,
                    () => FormatUi("AI #{0} 失败：{1}", generation.RunId, ShortFailureReason(diagnostic)),
                    FailureOverlayDurationMsec);
                SetStatus(() => FormatUi("任务 #{0} 生成失败：{1}", generation.RunId, FormatDiagnostic(diagnostic)), true);
            }
        }
        finally
        {
            // Release the shared budget before publishing overlays/UI. Otherwise
            // RefreshAiTaskCount (via overlays) still reads ActiveCount=1 and the
            // toolbar "任务 5/1" label sticks after every finished generation.
            generation.Cancellation.Dispose();
            generation.Permit.Dispose();
            RemoveGeneration(generation);
        }
    }

    private bool IsGenerationActive(ActiveGeneration generation) =>
        !_exiting
        && _activeGenerations.TryGetValue(generation.RunId, out ActiveGeneration? current)
        && ReferenceEquals(current, generation)
        && !generation.Cancellation.IsCancellationRequested;

    private void RemoveGeneration(ActiveGeneration generation)
    {
        if (!_activeGenerations.TryGetValue(generation.RunId, out ActiveGeneration? current)
            || !ReferenceEquals(current, generation))
        {
            return;
        }
        _activeGenerations.Remove(generation.RunId);
        PublishGenerationOverlays();
        RefreshGenerateAvailability();
    }

    private void CancelAllGenerations() => CancelAllGenerations(showTerminal: true);

    private void CancelAllGenerations(bool showTerminal)
    {
        bool hadPending = _pendingStarts.Count > 0 || _isPreparingGeneration;
        if (hadPending)
        {
            _pendingStarts.Clear();
            EndPreparingUi();
        }

        DiscardParkedWriteBacks();
        if (_activeGenerations.Count == 0)
        {
            if (hadPending && !_exiting && showTerminal)
            {
                SetStatus("已取消准备中的生成任务。", false);
            }
            return;
        }

        int count = _activeGenerations.Count;
        foreach (ActiveGeneration generation in _activeGenerations.Values.ToArray())
        {
            if (showTerminal)
            {
                AddTerminalOverlay(
                    generation,
                    AiGenerationOverlayPhase.Canceled,
                    () => FormatUi("AI #{0} 已取消", generation.RunId),
                    CanceledOverlayDurationMsec);
            }
            generation.Cancellation.Cancel();
            // Idempotent with RunGenerationAsync's finally: release budget even if
            // the async continuation is delayed, so the task counter cannot stick.
            generation.Permit.Dispose();
        }
        _activeGenerations.Clear();
        PublishGenerationOverlays();
        RefreshGenerateAvailability();
        if (!_exiting && showTerminal)
        {
            SetStatus(() => FormatUi("已取消 {0} 个生成任务。", count), false);
        }
    }

    private void AddTerminalOverlay(
        ActiveGeneration generation,
        AiGenerationOverlayPhase phase,
        Func<string> text,
        ulong durationMsec)
    {
        ulong now = Godot.Time.GetTicksMsec();
        int elapsedSeconds = ElapsedSeconds(generation.StartedMsec, now);
        _terminalOverlays[generation.RunId] = new TerminalGenerationOverlay(
            new AiGenerationOverlayState(
                generation.RunId,
                generation.Context,
                generation.WorldRect,
                phase,
                elapsedSeconds,
                string.Empty),
            text,
            now + durationMsec);
    }

    private string ShortFailureReason(AiGenerationSafeDiagnostic diagnostic)
    {
        string? reason = diagnostic.Category switch
        {
            AiGenerationFailureCategory.NameResolution => "域名解析失败",
            AiGenerationFailureCategory.Connection => "连接失败",
            AiGenerationFailureCategory.SecureConnection => "TLS/协议连接失败",
            AiGenerationFailureCategory.Proxy => "代理连接失败",
            AiGenerationFailureCategory.InvalidResponse => diagnostic.UpstreamCode switch
            {
                "ResponseEnded" => "响应提前中断",
                "HttpProtocolError" => "HTTP 协议错误",
                _ => "服务响应无效",
            },
            AiGenerationFailureCategory.ProviderRejected => "服务拒绝请求",
            AiGenerationFailureCategory.Timeout => "请求超时",
            AiGenerationFailureCategory.Io => "本地保存失败",
            AiGenerationFailureCategory.InvalidOperation => "请求状态无效",
            _ => null,
        };
        return reason == null
            ? diagnostic.UpstreamCode ?? Tr("客户端请求失败")
            : Tr(reason);
    }

    private string FormatDiagnostic(AiGenerationSafeDiagnostic diagnostic)
    {
        var parts = new List<string>
        {
            FormatUi("阶段：{0}", Tr(AiGenerationDiagnostics.PhaseText(diagnostic.Phase))),
            FormatUi("类别：{0}", Tr(AiGenerationDiagnostics.CategoryText(diagnostic.Category))),
            FormatUi("错误码：{0}", diagnostic.ErrorCode),
        };
        if (diagnostic.HttpStatus.HasValue)
        {
            parts.Add(FormatUi("HTTP：{0}", diagnostic.HttpStatus.Value));
        }
        if (!string.IsNullOrEmpty(diagnostic.UpstreamCode))
        {
            parts.Add(FormatUi("上游代码：{0}", diagnostic.UpstreamCode));
        }
        if (!string.IsNullOrEmpty(diagnostic.EndpointHost))
        {
            parts.Add(FormatUi("主机：{0}", diagnostic.EndpointHost));
        }
        parts.Add(FormatUi("诊断ID：{0}", diagnostic.DiagnosticId));
        return string.Join(" · ", parts);
    }

    private void PublishGenerationOverlays()
    {
        GenerationOverlaysChanged?.Invoke(GenerationOverlayStates);
        SetProcess(_activeGenerations.Count > 0 || _terminalOverlays.Count > 0 || _parkedWriteBacks.Count > 0 || _isPreparingGeneration);
    }

    private void BeginPreparingUi(PendingGenerationStart start)
    {
        _isPreparingGeneration = true;
        float gridSize = Math.Max(1, start.Context.GridSizePx);
        _preparingOverlay = new AiGenerationOverlayState(
            PreparingOverlayRunId,
            start.Context,
            new Rect2(
                start.Reference.Column * gridSize,
                start.Reference.Row * gridSize,
                start.Reference.Width * gridSize,
                start.Reference.Height * gridSize),
            AiGenerationOverlayPhase.Pending,
            0,
            "AI 准备中…");
        PublishGenerationOverlays();
        RefreshGenerateAvailability();
        SetStatus("正在准备画布参考图…", false);
    }

    private void EndPreparingUi()
    {
        if (!_isPreparingGeneration && _preparingOverlay == null)
        {
            RefreshGenerateAvailability();
            return;
        }

        _isPreparingGeneration = false;
        _preparingOverlay = null;
        PublishGenerationOverlays();
        RefreshGenerateAvailability();
    }

    private static Texture2D CreateGenerateLoadingIcon()
    {
        const int size = 16;
        Image image = Image.CreateEmpty(size, size, false, Image.Format.Rgba8);
        image.Fill(Colors.Transparent);
        var bright = new Color(0.45f, 0.72f, 1f, 1f);
        var dim = new Color(0.45f, 0.72f, 1f, 0.22f);
        const float center = (size - 1) / 2f;
        const float radius = 5.5f;
        for (int index = 0; index < 8; index++)
        {
            float angle = index / 8f * MathF.Tau;
            Color color = index < 3 ? bright : dim;
            for (int thickness = 0; thickness < 2; thickness++)
            {
                float length = radius - thickness;
                int x = (int)MathF.Round(center + MathF.Cos(angle) * length);
                int y = (int)MathF.Round(center + MathF.Sin(angle) * length);
                if ((uint)x < size && (uint)y < size)
                {
                    image.SetPixel(x, y, color);
                }
            }
        }

        return ImageTexture.CreateFromImage(image);
    }

    private void ParkWriteBack(ActiveGeneration generation, AiGeneratedImageApplication application)
    {
        _parkedWriteBacks.RemoveAll(item => item.RunId == generation.RunId);
        _parkedWriteBacks.Add(new ParkedWriteBack(
            generation.RunId,
            application,
            generation.Context,
            generation.WorldRect,
            generation.StartedMsec));
        PublishGenerationOverlays();
    }

    private void TryFlushParkedWriteBacks()
    {
        if (_exiting || _flushingParkedWriteBacks || _parkedWriteBacks.Count == 0 || ApplyGeneratedImage == null)
        {
            return;
        }

        _flushingParkedWriteBacks = true;
        try
        {
            FlushParkedWriteBacksCore();
        }
        finally
        {
            _flushingParkedWriteBacks = false;
            PublishGenerationOverlays();
        }
    }

    private void FlushParkedWriteBacksCore()
    {
        foreach (ParkedWriteBack parked in _parkedWriteBacks.ToArray())
        {
            if (!SameGenerationBoundary(_context, parked.Context))
            {
                continue;
            }

            _parkedWriteBacks.RemoveAll(item => item.RunId == parked.RunId);
            Func<AiGeneratedImageApplication, AiGeneratedImageApplyResult>? apply = ApplyGeneratedImage;
            if (apply == null)
            {
                continue;
            }

            AiGeneratedImageApplyResult applied;
            try
            {
                applied = apply(parked.Application);
            }
            catch
            {
                applied = AiGeneratedImageApplyResult.Fail("切回原场景后自动写入失败，画布未修改。");
            }

            if (!applied.Success
                && !applied.Applied
                && applied.FailureReason == AiGeneratedImageApplyFailureReason.ContextStale)
            {
                if (_parkedWriteBacks.TrueForAll(item => item.RunId != parked.RunId))
                {
                    _parkedWriteBacks.Add(parked);
                }
                continue;
            }

            TryDiscardArtifact(parked.Application);
            if (!applied.Success)
            {
                _terminalOverlays[parked.RunId] = new TerminalGenerationOverlay(
                    new AiGenerationOverlayState(
                        parked.RunId,
                        parked.Context,
                        parked.WorldRect,
                        AiGenerationOverlayPhase.Failure,
                        0,
                        string.Empty),
                    () => FormatUi(applied.Applied
                        ? "AI #{0} 已应用：自动保存失败"
                        : "AI #{0} 失败：自动应用失败", parked.RunId),
                    Godot.Time.GetTicksMsec() + FailureOverlayDurationMsec);
            }

            SetStatus(
                () => FormatUi(applied.Success
                    ? "任务 #{0} 已自动应用：{1}"
                    : applied.Applied
                        ? "任务 #{0} 已自动应用，但自动保存失败：{1}"
                        : "任务 #{0} 自动应用失败：{1}", parked.RunId, TranslateApplicationMessage(applied.Message)),
                !applied.Success);
        }
    }

    private void DiscardParkedWriteBacks()
    {
        foreach (ParkedWriteBack parked in _parkedWriteBacks.ToArray())
        {
            TryDiscardArtifact(parked.Application);
        }
        _parkedWriteBacks.Clear();
    }

    private IEnumerable<AiGenerationOverlayState> VisibleOverlays(IEnumerable<AiGenerationOverlayState> overlays)
    {
        if (_context == null)
        {
            return overlays;
        }

        return overlays.Where(state =>
            string.Equals(state.Context.ProjectId, _context.ProjectId, StringComparison.Ordinal)
            && string.Equals(state.Context.SceneId, _context.SceneId, StringComparison.Ordinal));
    }

    private AiGenerationOverlayState CreateParkedOverlayState(ParkedWriteBack parked) => new(
        parked.RunId,
        parked.Context,
        parked.WorldRect,
        AiGenerationOverlayPhase.Pending,
        0,
        FormatUi("AI #{0} 已生成，切回原场景后写入", parked.RunId));

    private static bool SameProjectBoundary(AiGenerationCanvasContext? left, AiGenerationCanvasContext? right)
    {
        if (left == null || right == null)
        {
            return left == right;
        }

        return string.Equals(left.ProjectRoot, right.ProjectRoot, StringComparison.Ordinal)
            && string.Equals(left.ProjectId, right.ProjectId, StringComparison.Ordinal);
    }

    private AiGenerationOverlayState CreateOverlayState(ActiveGeneration generation) => new(
        generation.RunId,
        generation.Context,
        generation.WorldRect,
        AiGenerationOverlayPhase.Generating,
        Math.Max(0, generation.LastElapsedSeconds),
        FormatUi("AI #{0} 生成中 {1}", generation.RunId, FormatElapsed(Math.Max(0, generation.LastElapsedSeconds))));

    private static int ElapsedSeconds(ulong startedMsec, ulong nowMsec)
    {
        if (startedMsec == 0 || nowMsec <= startedMsec)
        {
            return 0;
        }
        ulong elapsed = (nowMsec - startedMsec) / 1000;
        return elapsed > int.MaxValue ? int.MaxValue : (int)elapsed;
    }

    private static bool SameGenerationBoundary(AiGenerationCanvasContext? left, AiGenerationCanvasContext? right)
    {
        if (left == null || right == null)
        {
            return left == right;
        }
        return string.Equals(left.ProjectRoot, right.ProjectRoot, StringComparison.Ordinal)
            && string.Equals(left.ProjectId, right.ProjectId, StringComparison.Ordinal)
            && string.Equals(left.SceneId, right.SceneId, StringComparison.Ordinal)
            && string.Equals(left.LayerId, right.LayerId, StringComparison.Ordinal)
            && left.LayerScope == right.LayerScope;
    }

    private void TryDiscardArtifact(AiGeneratedImageApplication application)
    {
        try
        {
            DiscardGeneratedImage?.Invoke(application);
        }
        catch
        {
            // Artifact cleanup is best-effort and must not break panel lifecycle.
        }
    }


    private void RefreshContextUi()
    {
        bool canReplace = _context?.HasSelectedImage == true;
        _target.GetPopup().SetItemDisabled(ReplaceTargetIndex, !canReplace);
        if (!canReplace)
        {
            _target.Select(AddTargetIndex);
        }
        else if (!_replaceWasAvailable)
        {
            _target.Select(ReplaceTargetIndex);
        }
        _replaceWasAvailable = canReplace;

        RefreshRegionStatus();
        RefreshGenerateAvailability();
    }

    private void RefreshRegionStatus()
    {
        AiGenerationRegionMetadata? regions = _context?.RegionMetadata;
        string layerScope = _context?.LayerScope switch
        {
            CanvasPatchLayerScope.CurrentLayer => "当前活动图层",
            CanvasPatchLayerScope.VisibleLayersExceptCurrent => "仅其他可见图层",
            _ => "所有可见图层",
        };
        bool regionReady = TryResolveEffectiveGenerationRegion(regions, out _, out string regionError);
        string effectiveHint = regions?.SelectionRegion is not null && regions.ReplacementRegion is null
            ? Tr("    未设置 W，完整 Q 作为生成输入和写回范围").ToString()
            : string.Empty;
        string capacityHint = _activeGenerations.Count > 0
            ? FormatUi("    进行中 {0}/{1}", _activeGenerations.Count, _coordinator.MaximumConcurrentGenerations)
            : string.Empty;
        string regionIssue = regionReady || string.IsNullOrWhiteSpace(regionError)
            ? string.Empty
            : FormatUi("    ⚠ {0}", Tr(regionError));
        _regionStatus.Text =
            FormatUi("参考范围：{0}    Q 发送/写回：{1}    W 透明挖空区：{2}{3}{4}{5}", Tr(layerScope), FormatRegion(regions?.SelectionRegion), FormatRegion(regions?.ReplacementRegion), effectiveHint, capacityHint, regionIssue);
    }

    private void RefreshGenerateAvailability()
    {
        _profile.TooltipText = _profile.Selected >= 0 ? _profile.GetItemText(_profile.Selected) : string.Empty;
        _modelPreset.TooltipText = _modelPreset.Selected >= 0 ? _modelPreset.GetItemText(_modelPreset.Selected) : string.Empty;
        _target.TooltipText = _target.Selected >= 0 ? Tr(_target.GetItemText(_target.Selected)) : string.Empty;
        int activeCount = _activeGenerations.Count;
        int maximum = _coordinator.MaximumConcurrentGenerations;
        bool preparing = IsPreparingGeneration;
        _cancel.Disabled = activeCount == 0 && !preparing;
        _cancel.Text = activeCount > 0 ? FormatUi("取消全部 ({0})", activeCount) : Tr("取消全部");
        _cancel.TooltipText = _cancel.Text;
        // Capacity must match the shared coordinator budget so UI can start as many
        // independent Q boxes as MaximumConcurrentGenerations (default 5).
        bool atCapacity = activeCount >= maximum;
        if (preparing)
        {
            _generate.Disabled = true;
            _generate.Text = Tr(GeneratePreparingText);
            _generate.TooltipText = _generate.Text;
            _generate.Icon = GodotObject.IsInstanceValid(_generateLoadingIcon) ? _generateLoadingIcon : null;
            _generate.ExpandIcon = false;
            return;
        }

        _generate.Icon = null;
        _generate.Disabled = atCapacity
            || _profileController.ProfileCount == 0
            || _context?.CanEditCurrentLayer != true
            || !TryResolveEffectiveGenerationRegion(_context?.RegionMetadata, out _, out _);
        _generate.Text = atCapacity
            ? FormatUi("已满 ({0}/{1})", activeCount, maximum)
            : activeCount > 0
                ? FormatUi("生成 ({0}/{1})", activeCount, maximum)
                : Tr(GenerateIdleText);
        _generate.TooltipText = _generate.Text;
    }

    private static bool TryResolveEffectiveGenerationRegion(
        AiGenerationRegionMetadata? metadata,
        out AiGenerationGridRegion effectiveRegion,
        out string error)
    {
        if (metadata?.SelectionRegion is not AiGenerationGridRegion q)
        {
            effectiveRegion = default;
            error = "请先框选 Q 参考区域。";
            return false;
        }

        effectiveRegion = metadata.ReplacementRegion ?? q;
        long qEndColumn = (long)q.Column + q.Width;
        long qEndRow = (long)q.Row + q.Height;
        long effectiveEndColumn = (long)effectiveRegion.Column + effectiveRegion.Width;
        long effectiveEndRow = (long)effectiveRegion.Row + effectiveRegion.Height;
        if (effectiveRegion.Column < q.Column
            || effectiveRegion.Row < q.Row
            || effectiveEndColumn > qEndColumn
            || effectiveEndRow > qEndRow)
        {
            error = "W 必须位于 Q 区域内。";
            return false;
        }

        error = string.Empty;
        return true;
    }

    private static (int Width, int Height) ResolveRequestSize(AiGenerationRegionMetadata? metadata, int gridSizePx)
    {
        if (!TryResolveEffectiveGenerationRegion(metadata, out AiGenerationGridRegion region, out _))
        {
            return (1024, 1024);
        }
        int grid = Math.Max(1, gridSizePx);
        return (
            Math.Clamp(region.Width * grid, 1, 8192),
            Math.Clamp(region.Height * grid, 1, 8192));
    }

    private string FormatRegion(AiGenerationGridRegion? region) => region.HasValue
        ? FormatUi("({0},{1}) {2}×{3} 格", region.Value.Column, region.Value.Row, region.Value.Width, region.Value.Height)
        : Tr("未设置");

    private string FormatUi(string key, params object[] arguments) => string.Format(Tr(key), arguments);

    private void SetStatus(string message, bool error)
    {
        SetStatus(() => Tr(message), error);
    }

    private void SetStatus(Func<string> message, bool error)
    {
        _statusText = message;
        _statusIsError = error;
        RenderStatus(notify: true);
    }

    private void RenderStatus(bool notify)
    {
        string message = _statusText?.Invoke() ?? string.Empty;
        _status.Text = message;
        _status.AddThemeColorOverride(
            "font_color",
            _statusIsError ? AgasSharedIdeTheme.ErrorTextColor : AgasSharedIdeTheme.MutedTextColor);
        if (notify)
        {
            StatusChanged?.Invoke(message, _statusIsError);
        }
    }

    private string TranslateApplicationMessage(string message)
    {
        if (ApplicationMessageAliases.TryGetValue(message, out string? key))
        {
            return Tr(key);
        }
        return ApplicationMessageKeys.Contains(message) ? Tr(message) : TranslateReadOnlyMessage(message);
    }

    private string TranslateReadOnlyMessage(string message)
    {
        const string prefix = "项目为只读状态（";
        if (!message.StartsWith(prefix, StringComparison.Ordinal))
        {
            return message;
        }
        foreach (string suffix in new[] { "）。", "），不能应用或保存生成结果。" })
        {
            if (message.EndsWith(suffix, StringComparison.Ordinal))
            {
                string reason = message[prefix.Length..^suffix.Length];
                return FormatUi(suffix == "）。"
                    ? "项目为只读状态（{0}）。"
                    : "项目为只读状态（{0}），不能应用或保存生成结果。", reason);
            }
        }
        return message;
    }
}
