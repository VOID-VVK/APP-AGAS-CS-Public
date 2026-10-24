#nullable enable

using System;
using System.Collections.Generic;
using Agas.Features.Commands;
using AppAgasCs.Application;
using AppAgasCs.Core.Resources;
using Godot;

namespace Agas.Features.Canvas;

/// <summary>
/// Owns canvas undo/redo stacks and project mutation application.
/// Keeps <see cref="CanvasWorkspace"/> as a view host (SRP): rendering/input stay on the Control;
/// resource mutations and history live here behind narrow callbacks.
/// </summary>
internal sealed class CanvasEditApplication
{
    private readonly Stack<ICanvasCommand> _undoStack = new();
    private readonly Stack<ICanvasCommand> _redoStack = new();
    private readonly Func<string, bool> _canMutate;
    private readonly Action<bool, bool> _notifyChanged;
    private readonly Action _refreshCanvasState;
    private readonly Action _reconcileSelectionAfterLayerMutation;

    private ProjectSession? _localProjectSession;

    public CanvasEditApplication(
        Func<string, bool> canMutate,
        Action<bool, bool> notifyChanged,
        Action refreshCanvasState,
        Action reconcileSelectionAfterLayerMutation)
    {
        _canMutate = canMutate ?? throw new ArgumentNullException(nameof(canMutate));
        _notifyChanged = notifyChanged ?? throw new ArgumentNullException(nameof(notifyChanged));
        _refreshCanvasState = refreshCanvasState ?? throw new ArgumentNullException(nameof(refreshCanvasState));
        _reconcileSelectionAfterLayerMutation = reconcileSelectionAfterLayerMutation
            ?? throw new ArgumentNullException(nameof(reconcileSelectionAfterLayerMutation));
    }

    /// <summary>
    /// Shared project session for multi-scene mutations. Production binds
    /// <see cref="ProjectSession"/> from MainWorkspace.
    /// </summary>
    public IProjectMutationSession? MutationSession { get; set; }

    public bool CanUndo => _undoStack.Count > 0;

    public bool CanRedo => _redoStack.Count > 0;

    /// <summary>
    /// Mirrors canvas view project/scene pointers into a private <see cref="ProjectSession"/>
    /// when no shared session is bound (standalone tests).
    /// </summary>
    public void BindLocalWorkspace(string projectRoot, AgasProjectResource? project, AgasSceneResource? scene)
    {
        if (MutationSession != null)
        {
            return;
        }

        _localProjectSession ??= new ProjectSession();
        if (project == null)
        {
            _localProjectSession.Clear();
            return;
        }

        if (!string.Equals(_localProjectSession.ProjectRoot, projectRoot, StringComparison.Ordinal)
            || !ReferenceEquals(_localProjectSession.ProjectOrNull, project))
        {
            _localProjectSession.Reset(projectRoot ?? string.Empty);
            _localProjectSession.AcceptProject(project);
        }

        if (scene != null)
        {
            _localProjectSession.SetCurrentScene(scene, notify: false);
        }
        else if (_localProjectSession.CurrentScene != null)
        {
            _localProjectSession.SetCurrentScene(null, notify: false);
        }
    }

    public void ClearLocalSession()
    {
        _localProjectSession?.Clear();
    }

    public IProjectMutationSession ResolveMutationSession()
    {
        if (MutationSession != null)
        {
            return MutationSession;
        }

        _localProjectSession ??= new ProjectSession();
        return _localProjectSession;
    }

    public ProjectMutationResult MutateImage(
        ImageMutationCommand command,
        bool undoable,
        string projectRoot,
        AgasProjectResource? project,
        AgasSceneResource? scene)
    {
        BindLocalWorkspace(projectRoot, project, scene);
        return new ProjectMutationService(CreateMutationPolicy(CanvasEditScope.Image, undoable))
            .MutateImage(ResolveMutationSession(), command);
    }

    public ProjectMutationResult MutateGrid(
        GridMutationCommand command,
        bool undoable,
        string projectRoot,
        AgasProjectResource? project,
        AgasSceneResource? scene)
    {
        BindLocalWorkspace(projectRoot, project, scene);
        return new ProjectMutationService(CreateMutationPolicy(CanvasEditScope.Grid, undoable))
            .MutateGrid(ResolveMutationSession(), command);
    }

    public ProjectMutationResult MutateLayer(
        LayerMutationCommand command,
        bool undoable,
        string projectRoot,
        AgasProjectResource? project,
        AgasSceneResource? scene)
    {
        BindLocalWorkspace(projectRoot, project, scene);
        return new ProjectMutationService(CreateMutationPolicy(CanvasEditScope.Layer, undoable))
            .MutateLayer(ResolveMutationSession(), command);
    }

    public ProjectMutationResult MutatePrompt(
        PromptMutationCommand command,
        bool undoable,
        string projectRoot,
        AgasProjectResource? project,
        AgasSceneResource? scene)
    {
        BindLocalWorkspace(projectRoot, project, scene);
        return new ProjectMutationService(CreateMutationPolicy(CanvasEditScope.Prompt, undoable))
            .MutatePrompt(ResolveMutationSession(), command);
    }

    public ProjectMutationResult MutateView(
        ViewMutationCommand command,
        string projectRoot,
        AgasProjectResource? project,
        AgasSceneResource? scene)
    {
        BindLocalWorkspace(projectRoot, project, scene);
        return new ProjectMutationService().MutateView(ResolveMutationSession(), command);
    }

    public bool Undo()
    {
        if (!_canMutate("撤销") || _undoStack.Count == 0)
        {
            return false;
        }

        ICanvasCommand command = _undoStack.Pop();
        try
        {
            command.Undo();
        }
        catch (Exception ex)
        {
            _undoStack.Push(command);
            GD.PushError($"Canvas undo '{command.Label}' failed: {ex}");
            _refreshCanvasState();
            return false;
        }

        _redoStack.Push(command);
        _notifyChanged(false, true);
        return true;
    }

    public bool Redo()
    {
        if (!_canMutate("重做") || _redoStack.Count == 0)
        {
            return false;
        }

        ICanvasCommand command = _redoStack.Pop();
        try
        {
            command.Do();
        }
        catch (Exception ex)
        {
            _redoStack.Push(command);
            GD.PushError($"Canvas redo '{command.Label}' failed: {ex}");
            _refreshCanvasState();
            return false;
        }

        _undoStack.Push(command);
        _notifyChanged(false, true);
        return true;
    }

    public void ClearHistory()
    {
        _undoStack.Clear();
        _redoStack.Clear();
        _refreshCanvasState();
    }

    public bool TryExecuteCanvasEdit(string label, CanvasEditScope scope, Action doAction, Action undoAction)
    {
        if (!_canMutate(string.IsNullOrWhiteSpace(label) ? "修改画布" : label))
        {
            return false;
        }

        if (!IsUndoableCanvasEditScope(scope))
        {
            GD.PushWarning($"Canvas edit '{label}' ignored: scope {scope} is not undoable.");
            _refreshCanvasState();
            return false;
        }

        if (string.IsNullOrWhiteSpace(label) || doAction == null || undoAction == null)
        {
            GD.PushWarning("Canvas edit ignored: label/do/undo must be provided.");
            _refreshCanvasState();
            return false;
        }

        return ExecuteCommand(new DelegateCanvasCommand(label.Trim(), doAction, undoAction));
    }

    public bool ExecuteCommand(ICanvasCommand command)
    {
        ArgumentNullException.ThrowIfNull(command);
        if (!_canMutate(command.Label))
        {
            return false;
        }

        try
        {
            command.Do();
        }
        catch (Exception ex)
        {
            GD.PushError($"Canvas command '{command.Label}' failed: {ex}");
            _refreshCanvasState();
            return false;
        }

        _undoStack.Push(command);
        _redoStack.Clear();
        _notifyChanged(false, true);
        return true;
    }

    public static bool IsUndoableCanvasEditScope(CanvasEditScope scope) =>
        scope is CanvasEditScope.CanvasContent
            or CanvasEditScope.Image
            or CanvasEditScope.Layer
            or CanvasEditScope.Prompt
            or CanvasEditScope.Grid;

    private IProjectMutationExecutionPolicy CreateMutationPolicy(CanvasEditScope scope, bool undoable) =>
        undoable
            ? new CanvasMutationExecutionPolicy(this, scope)
            : new DirectProjectMutationExecutionPolicy();

    private sealed class CanvasMutationExecutionPolicy : IProjectMutationExecutionPolicy
    {
        private readonly CanvasEditApplication _edits;
        private readonly CanvasEditScope _scope;

        public CanvasMutationExecutionPolicy(CanvasEditApplication edits, CanvasEditScope scope)
        {
            _edits = edits;
            _scope = scope;
        }

        public void Execute(IProjectMutationOperation operation)
        {
            Action apply = _scope == CanvasEditScope.Layer
                ? () =>
                {
                    operation.Apply();
                    _edits._reconcileSelectionAfterLayerMutation();
                }
                : operation.Apply;
            Action revert = _scope == CanvasEditScope.Layer
                ? () =>
                {
                    operation.Revert();
                    _edits._reconcileSelectionAfterLayerMutation();
                }
                : operation.Revert;

            if (!_edits.TryExecuteCanvasEdit(operation.Description, _scope, apply, revert))
            {
                throw new InvalidOperationException(
                    $"Canvas rejected project mutation '{operation.Description}'.");
            }
        }
    }
}
