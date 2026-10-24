using System;
using AppAgasCs.Application;

namespace AppAgasCs.Mcp;

/// <summary>
/// MCP mutation policy: apply immediately, record all persistence effects in one place, then
/// notify the UI. The application operation remains reversible so a later UI adapter can use
/// the same mutation service with its undo stack instead of duplicating domain logic.
/// </summary>
public sealed class AgasMcpProjectMutationPolicy : IProjectMutationExecutionPolicy
{
    private readonly AgasMcpProjectContextProvider _contextProvider;
    private readonly AgasMcpDirtyState _dirtyState;

    public AgasMcpProjectMutationPolicy(
        AgasMcpProjectContextProvider contextProvider,
        AgasMcpDirtyState dirtyState)
    {
        _contextProvider = contextProvider ?? throw new ArgumentNullException(nameof(contextProvider));
        _dirtyState = dirtyState ?? throw new ArgumentNullException(nameof(dirtyState));
    }

    public void Execute(IProjectMutationOperation operation)
    {
        ArgumentNullException.ThrowIfNull(operation);
        operation.Apply();

        ProjectMutationEffects effects = operation.Effects;
        if (effects.DirtyProject) _dirtyState.MarkProjectDirty();
        if (effects.DirtyLayout) _dirtyState.MarkLayoutDirty();
        foreach (string sceneId in effects.DirtySceneIds) _dirtyState.MarkSceneDirty(sceneId);
        foreach (string sceneId in effects.DeletedSceneIds) _dirtyState.MarkSceneDeleted(sceneId);

        AgasMcpDirtySnapshot dirty = _dirtyState.Snapshot();
        AgasMcpProjectContext? context = _contextProvider.GetCurrent();
        context?.NotifyDirtyState(dirty);
        context?.NotifyMutation($"MCP {operation.Description.ToLowerInvariant()} applied.");
    }
}
