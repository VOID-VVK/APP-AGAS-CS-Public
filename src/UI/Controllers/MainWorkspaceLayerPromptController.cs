using System;
using System.Linq;
using Agas.Features.Canvas;
using Agas.Features.Commands;
using AppAgasCs.Application;
using AppAgasCs.Core.Resources;
using Godot;

namespace AGAS.UI.Controllers;

/// <summary>
/// Coordinates layer and prompt commands between MainWorkspace UI events, the canvas undo
/// policy, and the application mutation service. MainWorkspace retains only event routing.
/// </summary>
internal sealed class MainWorkspaceLayerPromptController
{
    private readonly Func<IProjectMutationSession> _session;
    private readonly Func<AgasSceneResource?> _currentScene;
    private readonly Func<CanvasWorkspace?> _canvas;
    private readonly Func<string, bool> _canMutate;
    private readonly Action<string, bool> _setStatus;
    private readonly Action<string, bool> _markDirty;
    private readonly Action<string, bool, object[]> _setStatusTemplate;
    private readonly Action<string, bool, object[]> _markDirtyTemplate;
    private readonly Action _refreshManagementUi;
    private readonly Action _updateAiContext;
    private readonly Action _updateCanvasStatus;
    private readonly Action<AgasSceneResource?> _showLayerTabsScene;
    private readonly Action<AgasLayerResource?> _showPromptLayer;
    private readonly Action _openPromptDock;

    internal MainWorkspaceLayerPromptController(
        Func<IProjectMutationSession> session,
        Func<AgasSceneResource?> currentScene,
        Func<CanvasWorkspace?> canvas,
        Func<string, bool> canMutate,
        Action<string, bool> setStatus,
        Action<string, bool> markDirty,
        Action refreshManagementUi,
        Action updateAiContext,
        Action updateCanvasStatus,
        Action<AgasSceneResource?> showLayerTabsScene,
        Action<AgasLayerResource?> showPromptLayer,
        Action openPromptDock,
        Action<string, bool, object[]>? setStatusTemplate = null,
        Action<string, bool, object[]>? markDirtyTemplate = null)
    {
        _session = session ?? throw new ArgumentNullException(nameof(session));
        _currentScene = currentScene ?? throw new ArgumentNullException(nameof(currentScene));
        _canvas = canvas ?? throw new ArgumentNullException(nameof(canvas));
        _canMutate = canMutate ?? throw new ArgumentNullException(nameof(canMutate));
        _setStatus = setStatus ?? throw new ArgumentNullException(nameof(setStatus));
        _markDirty = markDirty ?? throw new ArgumentNullException(nameof(markDirty));
        _setStatusTemplate = setStatusTemplate ?? ((key, error, arguments) => setStatus(string.Format(TranslationServer.Translate(key), arguments.Select(argument => argument is StringName translationKey ? TranslationServer.Translate(translationKey).ToString() : argument).ToArray()), error));
        _markDirtyTemplate = markDirtyTemplate ?? ((key, error, arguments) => markDirty(string.Format(TranslationServer.Translate(key), arguments), error));
        _refreshManagementUi = refreshManagementUi ?? throw new ArgumentNullException(nameof(refreshManagementUi));
        _updateAiContext = updateAiContext ?? throw new ArgumentNullException(nameof(updateAiContext));
        _updateCanvasStatus = updateCanvasStatus ?? throw new ArgumentNullException(nameof(updateCanvasStatus));
        _showLayerTabsScene = showLayerTabsScene ?? throw new ArgumentNullException(nameof(showLayerTabsScene));
        _showPromptLayer = showPromptLayer ?? throw new ArgumentNullException(nameof(showPromptLayer));
        _openPromptDock = openPromptDock ?? throw new ArgumentNullException(nameof(openPromptDock));
    }

    internal void AddLayer()
    {
        if (!_canMutate("新增图层")) return;
        AgasSceneResource? scene = _currentScene();
        if (scene == null || _canvas() == null) return;
        ApplyLayerMutation(new LayerMutationCommand(
            LayerMutationKind.Add,
            scene.Id,
            Name: CreateUniqueLayerName(scene)),
            "新增图层");
    }

    internal void RenameLayer(AgasLayerResource layer, string newName)
    {
        if (!_canMutate("重命名图层")) return;
        AgasSceneResource? scene = _currentScene();
        if (scene == null || _canvas() == null || string.IsNullOrWhiteSpace(newName)) return;
        string sanitized = newName.Trim();
        if (layer.Name == sanitized) return;
        ApplyLayerMutation(new LayerMutationCommand(
            LayerMutationKind.Rename, scene.Id, layer.Id, sanitized), "重命名图层");
    }

    internal void DeleteLayer(AgasLayerResource layer)
    {
        if (!_canMutate("删除图层")) return;
        AgasSceneResource? scene = _currentScene();
        if (scene == null || _canvas() == null) return;
        ApplyLayerMutation(new LayerMutationCommand(
            LayerMutationKind.Delete, scene.Id, layer.Id), "删除图层");
    }

    internal void MoveLayer(AgasLayerResource layer, int direction)
    {
        if (!_canMutate("移动图层")) return;
        AgasSceneResource? scene = _currentScene();
        if (scene == null || _canvas() == null) return;
        ApplyLayerMutation(new LayerMutationCommand(
            LayerMutationKind.Move, scene.Id, layer.Id, Direction: direction), "移动图层");
    }

    internal void OpenLayerPrompt(AgasLayerResource layer)
    {
        if (_currentScene() == null) return;
        SetCurrentLayer(layer);
        _openPromptDock();
        _showPromptLayer(layer);
    }

    internal void AddPromptFragment(AgasLayerResource layer)
    {
        if (!_canMutate("新增提示词")) return;
        CanvasWorkspace? canvas = _canvas();
        if (canvas == null) return;
        if (canvas.AddPromptFragment(layer) == null)
        {
            PromptFailure(layer, "新增提示词失败。");
            return;
        }
        PromptSucceeded("已新增提示词片段。");
    }

    internal void DeletePromptFragment(AgasLayerResource layer, PromptFragmentResource fragment)
    {
        if (!_canMutate("删除提示词")) return;
        CanvasWorkspace? canvas = _canvas();
        if (canvas == null) return;
        if (!canvas.DeletePromptFragment(layer, fragment))
        {
            PromptFailure(layer, "删除提示词失败。");
            return;
        }
        PromptSucceeded("已删除提示词片段。");
    }

    internal void UpdatePromptFragmentText(PromptFragmentResource fragment, string text)
    {
        if (!_canMutate("更新提示词")) return;
        CanvasWorkspace? canvas = _canvas();
        if (canvas == null) return;
        if (!canvas.UpdatePromptFragmentText(fragment, text))
        {
            PromptFailure(null, "提示词文本更新失败。");
            return;
        }
        PromptSucceeded("提示词文本已更新。");
    }

    internal void SetPromptFragmentEnabled(PromptFragmentResource fragment, bool enabled)
    {
        if (!_canMutate("更新提示词")) return;
        CanvasWorkspace? canvas = _canvas();
        if (canvas == null) return;
        if (!canvas.SetPromptFragmentEnabled(fragment, enabled))
        {
            PromptFailure(null, "提示词启用状态更新失败。");
            return;
        }
        PromptSucceeded("提示词启用状态已更新。");
    }

    internal void SetPromptFragmentRole(PromptFragmentResource fragment, PromptFragmentRole role)
    {
        if (!_canMutate("更新提示词分类")) return;
        CanvasWorkspace? canvas = _canvas();
        if (canvas == null) return;
        if (!canvas.SetPromptFragmentRole(fragment, (int)role))
        {
            PromptFailure(null, "提示词分类更新失败。");
            return;
        }
        PromptSucceeded("提示词分类已更新。");
    }

    internal void MovePromptFragment(
        AgasLayerResource layer,
        PromptFragmentResource fragment,
        int direction)
    {
        if (!_canMutate("移动提示词")) return;
        CanvasWorkspace? canvas = _canvas();
        if (canvas == null) return;
        if (!canvas.MovePromptFragment(layer, fragment, direction))
        {
            PromptFailure(layer, "移动提示词失败。");
            return;
        }
        PromptSucceeded("提示词顺序已更新。");
    }

    internal void SetCurrentLayer(AgasLayerResource layer)
    {
        if (!_canMutate("切换当前图层")) return;
        AgasSceneResource? scene = _currentScene();
        if (scene == null || string.Equals(scene.ActiveLayerId, layer.Id, StringComparison.Ordinal)) return;

        ProjectMutationResult mutation = new ProjectMutationService().MutateLayer(
            _session(),
            new LayerMutationCommand(LayerMutationKind.SetCurrent, scene.Id, layer.Id));
        if (!mutation.Success)
        {
            _setStatusTemplate("切换当前图层失败：{0}", true, new object[] { mutation.Message });
            return;
        }

        _canvas()?.RefreshCanvasState();
        _showPromptLayer(layer);
        _updateAiContext();
        _updateCanvasStatus();
        _markDirtyTemplate("当前图层：{0}", false, new object[] { layer.Name });
    }

    internal void SetLayerVisible(AgasLayerResource layer, bool visible)
    {
        if (!_canMutate("修改图层可见性")) return;
        AgasSceneResource? scene = _currentScene();
        if (scene != null && ApplyLayerMutation(new LayerMutationCommand(
                LayerMutationKind.SetVisible, scene.Id, layer.Id, Flag: visible),
                "修改图层可见性"))
        {
            _showLayerTabsScene(scene);
        }
    }

    internal void SetLayerLocked(AgasLayerResource layer, bool locked)
    {
        if (!_canMutate("修改图层锁定状态")) return;
        AgasSceneResource? scene = _currentScene();
        if (scene != null && ApplyLayerMutation(new LayerMutationCommand(
                LayerMutationKind.SetLocked, scene.Id, layer.Id, Flag: locked),
                "修改图层锁定状态"))
        {
            _showLayerTabsScene(scene);
        }
    }

    internal void SetLayerShared(AgasLayerResource layer, bool shared)
    {
        if (!_canMutate("修改图层分享状态")) return;
        AgasSceneResource? scene = _currentScene();
        if (scene != null && ApplyLayerMutation(new LayerMutationCommand(
                LayerMutationKind.SetShared, scene.Id, layer.Id, Flag: shared),
                "修改图层分享状态"))
        {
            _showLayerTabsScene(scene);
        }
    }

    private bool ApplyLayerMutation(LayerMutationCommand command, string action)
    {
        CanvasWorkspace? canvas = _canvas();
        if (canvas == null) return false;
        try
        {
            var service = new ProjectMutationService(
                new CanvasUndoProjectMutationPolicy(canvas, CanvasEditScope.Layer));
            ProjectMutationResult result = service.MutateLayer(_session(), command);
            if (result.Success) return true;
            _setStatusTemplate("{0}失败：{1}", true, new object[] { (StringName)action, result.Message });
            return false;
        }
        catch (Exception exception)
        {
            _setStatusTemplate("{0}失败：{1}", true, new object[] { (StringName)action, exception.Message });
            return false;
        }
    }

    private void PromptFailure(AgasLayerResource? layer, string fallback)
    {
        _setStatus(layer?.Locked == true ? "图层已锁定，提示词只读。" : fallback, true);
        _refreshManagementUi();
    }

    private void PromptSucceeded(string message)
    {
        _refreshManagementUi();
        _markDirty(message, false);
    }

    private static string CreateUniqueLayerName(AgasSceneResource scene)
    {
        int number = scene.Layers.Count + 1;
        string candidate;
        do
        {
            candidate = $"图层 {number}";
            number++;
        }
        while (scene.Layers.Any(layer => string.Equals(
            layer.Name, candidate, StringComparison.OrdinalIgnoreCase)));
        return candidate;
    }
}
