#nullable enable

using System;
using System.Collections.Generic;
using System.Linq;
using AppAgasCs.Features.Ai.Generation;
using AppAgasCs.Features.Ai.Settings;
using Godot;

namespace AppAgasCs.Features.Ai.UI;

internal readonly record struct AiGenerationProfileReloadResult(string Message, bool IsError);

/// <summary>
/// Keeps connection selection and model selection independent. Connections own
/// only an endpoint and credential; the document owns one global model choice.
/// </summary>
internal sealed partial class AiGenerationProfileController
{
    private readonly AgasAiSettingsStore _settingsStore;
    private readonly AgasAiSettingsFieldMergeService _fieldMerge;
    private readonly OptionButton _profile;
    private readonly OptionButton _provider;
    private readonly OptionButton _modelPreset;
    private readonly LineEdit _modelName;
    private readonly Label _modelRecommendation;
    private AgasAiProfilesDocument _profiles = AgasAiProfilesDocument.Empty;
    private bool _applyingSelection;
    private bool _connected;
    private Action<string, bool, object[]>? _statusChanged;
    private Action? _selectionChanged;

    internal AiGenerationProfileController(
        AgasAiSettingsStore settingsStore,
        OptionButton profile,
        OptionButton provider,
        OptionButton modelPreset,
        LineEdit modelName,
        Label modelRecommendation,
        AgasAiModelDiscovery? discovery = null)
    {
        _settingsStore = settingsStore ?? throw new ArgumentNullException(nameof(settingsStore));
        _discovery = discovery ?? AgasAiModelDiscovery.Shared;
        _models = new AgasAiConnectionModelAccess(_settingsStore, _discovery);
        _fieldMerge = new AgasAiSettingsFieldMergeService(_settingsStore);
        _profile = profile ?? throw new ArgumentNullException(nameof(profile));
        _provider = provider ?? throw new ArgumentNullException(nameof(provider));
        _modelPreset = modelPreset ?? throw new ArgumentNullException(nameof(modelPreset));
        _modelName = modelName ?? throw new ArgumentNullException(nameof(modelName));
        _modelRecommendation = modelRecommendation ?? throw new ArgumentNullException(nameof(modelRecommendation));
    }

    internal IReadOnlyList<AiGenerationProfileOption> ProfileOptions
    {
        get
        {
            AgasAiModelSnapshot snapshot = _models.Snapshot(SelectedProfileId);
            return Array.AsReadOnly(snapshot.Models.Where(model => snapshot.IsReady && model.IsAvailable)
                .Select(model => new AiGenerationProfileOption(model.ModelName, model.DisplayName)).ToArray());
        }
    }

    internal string? SelectedProfileId => SelectedProfile()?.Id;

    internal string ActiveModelName => SelectedModel()?.ModelName ?? _profiles.ActiveModelName;

    internal int ProfileCount => _profiles.Profiles.Count;

    internal void Connect(Action<string, bool, object[]> statusChanged, Action selectionChanged)
    {
        ArgumentNullException.ThrowIfNull(statusChanged);
        ArgumentNullException.ThrowIfNull(selectionChanged);
        if (_connected) return;

        _statusChanged = statusChanged;
        _selectionChanged = selectionChanged;
        _profile.ItemSelected += OnProfileSelected;
        _modelPreset.ItemSelected += OnModelPresetSelected;
        _provider.ItemSelected += OnCategorySelected;
        _connected = true;
    }

    internal void Disconnect()
    {
        if (!_connected) return;

        _profile.ItemSelected -= OnProfileSelected;
        _modelPreset.ItemSelected -= OnModelPresetSelected;
        _provider.ItemSelected -= OnCategorySelected;
        _refreshGeneration++;
        _refreshCancellation?.Cancel();
        _refreshCancellation?.Dispose();
        _refreshCancellation = null;
        _statusChanged = null;
        _selectionChanged = null;
        _connected = false;
    }

    internal AiGenerationProfileReloadResult ReloadProfiles(string? preferredProfileId = null)
    {
        try
        {
            string? selectedId = preferredProfileId ?? SelectedProfile()?.Id;
            AgasAiProviderCatalog providers = AgasAiProviderCatalog.Load(_settingsStore.HomeDirectory);
            _profiles = AgasAiNewApiSetup.EnsureProfile(_settingsStore, providers);
            PopulateConnections();

            string? preferredId = FindProfileIndex(selectedId) >= 0
                ? selectedId
                : _profiles.ActiveProfileId ?? _profiles.Profiles.FirstOrDefault()?.Id;
            int index = FindProfileIndex(preferredId);
            if (index >= 0) _profile.Select(index);
            PopulateCategories(_profiles.ActiveModelName);
            RefreshModels();

            string message = _profiles.Profiles.Count == 0
                ? "请先在设置中添加 AI 连接。"
                : _settingsStore.CredentialMigrationWarning ?? string.Empty;
            return new AiGenerationProfileReloadResult(message, _profiles.Profiles.Count == 0);
        }
        catch (Exception)
        {
            _profiles = AgasAiProfilesDocument.Empty;
            _profile.Clear();
            _modelPreset.Clear();
            return new AiGenerationProfileReloadResult("AI 连接配置加载失败。", true);
        }
    }

    internal bool SelectProfileById(string profileId)
    {
        int index = FindProfileIndex(profileId);
        if (index < 0) return false;

        _profile.Select(index);
        bool saved = PersistActiveSelection(persistModelFields: false);
        if (saved) { PopulateCategories(_profiles.ActiveModelName); RefreshModels(); }
        return saved;
    }

    internal bool SelectModelByName(string modelName)
    {
        if (!_models.TryGet(modelName, out AgasAiUnifiedModel? model, SelectedProfileId)) return false;

        SelectCategory(model.Category);
        PopulateModels(model.ModelName);

        int index = FindModelIndex(model.ModelName);
        if (index < 0) return false;

        _applyingSelection = true;
        try
        {
            _modelPreset.Select(index);
            _modelName.Text = model.ModelName;
        }
        finally
        {
            _applyingSelection = false;
        }

        if (PersistActiveModel(model.ModelName))
        {
            return true;
        }

        PopulateModels(_profiles.ActiveModelName);
        return false;
    }

    internal bool PersistActiveSelection(bool persistModelFields)
    {
        AgasAiProfile? selected = SelectedProfile();
        if (selected == null) return false;

        try
        {
            if (!string.Equals(_profiles.ActiveProfileId, selected.Id, StringComparison.Ordinal))
                _profiles = _fieldMerge.Activate(selected.Id);
            if (persistModelFields && SelectedModel() is { } model
                && !string.Equals(_profiles.ActiveModelName, model.ModelName, StringComparison.Ordinal))
                _profiles = _fieldMerge.SetActiveModel(model.ModelName);
            return true;
        }
        catch
        {
            _statusChanged?.Invoke("当前 AI 选择未能写入本地配置。", true, Array.Empty<object>());
            return false;
        }
    }

    internal AiGenerationProfileResolution ResolveProviderProfile(string? profileId = null)
    {
        AgasAiProfilesDocument fresh;
        try
        {
            fresh = _settingsStore.LoadProfiles();
        }
        catch
        {
            return AiGenerationProfileResolution.Fail("AI 连接配置读取失败。");
        }

        string? selectedId = string.IsNullOrWhiteSpace(profileId)
            ? SelectedProfile()?.Id ?? fresh.ActiveProfileId
            : profileId.Trim();
        AgasAiProfile? storedProfile = fresh.Profiles.FirstOrDefault(profile =>
            string.Equals(profile.Id, selectedId, StringComparison.Ordinal));
        if (storedProfile == null)
            return AiGenerationProfileResolution.Fail("当前 AI 连接不可用。");
        if (AgasAiConnectionModelResolver.UsesRetiredProtocol(storedProfile,
            AgasAiProviderCatalog.Load(_settingsStore.HomeDirectory)))
            return AiGenerationProfileResolution.Fail("旧 Google 私有协议已停用，请在设置中改为统一 API 地址并重新保存连接及 Key。");
        if (!_models.TryGet(fresh.ActiveModelName, out AgasAiUnifiedModel? model, storedProfile.Id))
            return AiGenerationProfileResolution.Fail("当前生图模型不可用。");

        string? credential;
        try
        {
            credential = _settingsStore.GetCredential(storedProfile.Id);
        }
        catch
        {
            return AiGenerationProfileResolution.Fail("AI 连接凭据读取失败。");
        }
        if (string.IsNullOrWhiteSpace(credential))
            return AiGenerationProfileResolution.Fail("所选连接尚未配置 API Key。");

        try
        {
            return AiGenerationProfileResolution.Ok(AgasAiConnectionModelResolver.Resolve(
                storedProfile with { ApiKey = credential },
                model.ModelName, _discovery));
        }
        catch (AiGenerationException exception)
        {
            return AiGenerationProfileResolution.Fail(exception.Message);
        }
        catch
        {
            return AiGenerationProfileResolution.Fail("所选连接配置无效。");
        }
    }

    internal AgasAiProfile? SelectedProfile()
    {
        int selected = _profile.Selected;
        if (selected < 0 || selected >= _profile.ItemCount) return null;
        string id = _profile.GetItemMetadata(selected).AsString();
        return _profiles.Profiles.FirstOrDefault(profile => profile.Id == id);
    }

    private AgasAiUnifiedModel? SelectedModel()
    {
        int selected = _modelPreset.Selected;
        if (selected < 0 || selected >= _modelPreset.ItemCount) return null;
        return _models.TryGet(
            _modelPreset.GetItemMetadata(selected).AsString(),
            out AgasAiUnifiedModel? model, SelectedProfileId)
            ? model
            : null;
    }

    private void PopulateConnections()
    {
        _profile.Clear();
        foreach (AgasAiProfile profile in _profiles.Profiles)
        {
            _profile.AddItem(profile.Name);
            _profile.SetItemMetadata(_profile.ItemCount - 1, profile.Id);
        }

    }

    private int FindProfileIndex(string? id)
    {
        if (string.IsNullOrWhiteSpace(id)) return -1;
        for (int index = 0; index < _profiles.Profiles.Count; index++)
        {
            if (_profiles.Profiles[index].Id == id) return index;
        }
        return -1;
    }

    private int FindModelIndex(string modelName)
    {
        for (int index = 0; index < _modelPreset.ItemCount; index++)
        {
            if (string.Equals(_modelPreset.GetItemMetadata(index).AsString(), modelName, StringComparison.OrdinalIgnoreCase))
                return index;
        }
        return -1;
    }

    private void OnProfileSelected(long index)
    {
        if (_applyingSelection || index < 0 || index >= _profile.ItemCount) return;
        PersistActiveSelection(persistModelFields: false);
        PopulateCategories(_profiles.ActiveModelName);
        RefreshModels();
        _selectionChanged?.Invoke();
    }

    private void OnModelPresetSelected(long index)
    {
        if (_applyingSelection || index < 0 || index >= _modelPreset.ItemCount) return;
        string modelName = _modelPreset.GetItemMetadata((int)index).AsString();
        _modelName.Text = modelName;
        if (PersistActiveModel(modelName))
        {
            _statusChanged?.Invoke("已选择模型：{0}", false, new object[] { modelName });
            _selectionChanged?.Invoke();
            return;
        }

        PopulateModels(_profiles.ActiveModelName);
        _selectionChanged?.Invoke();
    }

    private bool PersistActiveModel(string modelName)
    {
        try
        {
            lock (_settingsStore.SharedGate)
            {
                if (_settingsStore.LoadProfiles().ActiveProfileId != SelectedProfileId
                    || !_models.TryGet(modelName, out _, SelectedProfileId)) return false;
                _profiles = _fieldMerge.SetActiveModel(modelName);
            }
            return true;
        }
        catch
        {
            _statusChanged?.Invoke("当前模型选择未能写入本地配置。", true, Array.Empty<object>());
            return false;
        }
    }
}
