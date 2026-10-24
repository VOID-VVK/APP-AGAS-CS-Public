using System;
using System.Linq;
using System.Threading;
using AppAgasCs.Features.Ai.Settings;

namespace AppAgasCs.Features.Ai.UI;

internal sealed partial class AiGenerationProfileController
{
    private readonly AgasAiConnectionModelAccess _models;
    private readonly AgasAiModelDiscovery _discovery;
    private CancellationTokenSource? _refreshCancellation;
    private long _refreshGeneration;

    private async void RefreshModels()
    {
        _refreshCancellation?.Cancel();
        _refreshCancellation?.Dispose();
        _refreshCancellation = new CancellationTokenSource();
        long generation = ++_refreshGeneration;
        string? profileId = SelectedProfileId;
        try
        {
            var pending = _models.RefreshAsync(profileId, _refreshCancellation.Token);
            PopulateCategories(_profiles.ActiveModelName);
            _selectionChanged?.Invoke();
            AgasAiModelSnapshot snapshot = await pending;
            if (!_connected || generation != _refreshGeneration || SelectedProfileId != profileId) return;
            PopulateCategories(_profiles.ActiveModelName);
            _statusChanged?.Invoke(snapshot.Message, !snapshot.IsReady || !snapshot.Models.Any(model => model.IsAvailable), Array.Empty<object>());
            _selectionChanged?.Invoke();
        }
        catch (OperationCanceledException) { }
        catch
        {
            if (_connected && generation == _refreshGeneration)
            {
                _modelPreset.Disabled = true;
                _statusChanged?.Invoke("模型列表刷新失败，请重试。", true, Array.Empty<object>());
                _selectionChanged?.Invoke();
            }
        }
    }

    private void PopulateCategories(string modelName)
    {
        AgasAiModelSnapshot snapshot = _models.Snapshot(SelectedProfileId);
        string selected = _provider.Selected >= 0 ? _provider.GetItemMetadata(_provider.Selected).AsString() : string.Empty;
        if (snapshot.TryGet(modelName, out AgasAiUnifiedModel model)) selected = model.Category;
        _applyingSelection = true;
        _provider.Clear();
        foreach (var category in new[] { ("gpt", "GPT"), ("gemini", "Gemini"), ("jimeng", "即梦"), ("other", "其他") })
        {
            if (!snapshot.Models.Any(item => item.Category == category.Item1)) continue;
            _provider.AddItem(category.Item2);
            _provider.SetItemMetadata(_provider.ItemCount - 1, category.Item1);
        }
        SelectCategory(selected);
        _provider.Disabled = !snapshot.IsReady || _provider.ItemCount == 0;
        _applyingSelection = false;
        PopulateModels(modelName);
    }

    private void SelectCategory(string category)
    {
        for (int index = 0; index < _provider.ItemCount; index++)
            if (_provider.GetItemMetadata(index).AsString() == category) { _provider.Select(index); return; }
        if (_provider.ItemCount > 0) _provider.Select(0);
    }

    private void PopulateModels(string selectedModelName)
    {
        AgasAiModelSnapshot snapshot = _models.Snapshot(SelectedProfileId);
        string category = _provider.Selected >= 0 ? _provider.GetItemMetadata(_provider.Selected).AsString() : string.Empty;
        _applyingSelection = true;
        try
        {
            _modelPreset.Clear();
            int selectedIndex = -1;
            foreach (AgasAiUnifiedModel model in snapshot.Models.Where(model => model.Category == category))
            {
                int index = _modelPreset.ItemCount;
                _modelPreset.AddItem(model.DisplayName);
                _modelPreset.SetItemMetadata(index, model.ModelName);
                _modelPreset.SetItemDisabled(index, !snapshot.IsReady || !model.IsAvailable);
                _modelPreset.SetItemTooltip(index, model.ModelName);
                if (string.Equals(model.ModelName, selectedModelName, StringComparison.OrdinalIgnoreCase)) selectedIndex = index;
            }
            if (selectedIndex < 0)
            {
                for (int index = 0; index < _modelPreset.ItemCount; index++)
                    if (!_modelPreset.IsItemDisabled(index)) { selectedIndex = index; break; }
            }
            _modelPreset.Disabled = !snapshot.IsReady || selectedIndex < 0;
            _modelName.Text = string.Empty;
            if (selectedIndex >= 0)
            {
                _modelPreset.Select(selectedIndex);
                _modelName.Text = _modelPreset.GetItemMetadata(selectedIndex).AsString();
                if (snapshot.IsReady && _modelName.Text != _profiles.ActiveModelName) PersistActiveModel(_modelName.Text);
            }
            _modelRecommendation.Text = snapshot.Message;
            if (snapshot.TryGet(_modelName.Text, out AgasAiUnifiedModel selected))
            {
                string limits = string.Join(" / ", selected.Ratios.Concat(selected.Resolutions));
                _modelRecommendation.Text = string.IsNullOrWhiteSpace(limits)
                    ? "模型使用当前连接的 Base URL 和 API Key。"
                    : $"模型固定输出：{limits}；结果按画布区域应用。";
            }
        }
        finally { _applyingSelection = false; }
    }

    private void OnCategorySelected(long index)
    {
        if (_applyingSelection || index < 0 || index >= _provider.ItemCount) return;
        PopulateModels(_profiles.ActiveModelName);
        _selectionChanged?.Invoke();
    }
}
