using System;
using AppAgasCs.Core.Persistence;

namespace AppAgasCs.Application;

/// <summary>
/// Selects a loaded live scene before fresh disk load. The source adapter is invoked only
/// between main-thread guard checks and can return only immutable scalar contracts.
/// </summary>
public sealed class AgasSceneDetailQuery
{
    private readonly IAgasSceneDetailSnapshotSource _source;
    private readonly IAgasSceneDetailMainThreadGuard _mainThread;
    private readonly AgasSceneDetailInvariantValidator _validator;

    public AgasSceneDetailQuery(
        IAgasSceneDetailSnapshotSource source,
        IAgasSceneDetailMainThreadGuard mainThread,
        AgasSceneDetailInvariantValidator? validator = null)
    {
        _source = source ?? throw new ArgumentNullException(nameof(source));
        _mainThread = mainThread ?? throw new ArgumentNullException(nameof(mainThread));
        _validator = validator ?? new AgasSceneDetailInvariantValidator();
    }

    public AgasSceneDetailResult<AgasSceneDetailCaptureResult> Capture(string sceneId)
    {
        if (string.IsNullOrWhiteSpace(sceneId)
            || !string.Equals(sceneId, sceneId.Trim(), StringComparison.Ordinal)
            || !ProjectPathRules.IsSafeAsciiIdentifier(sceneId))
            return AgasSceneDetailResult<AgasSceneDetailCaptureResult>.Fail(AgasSceneDetailErrorCodes.Invalid);

        _mainThread.EnsureMainThread();
        AgasSceneDetailSnapshot? snapshot;
        AgasSceneDetailSourceKind sourceKind;
        if (_source.TryCaptureLoadedLive(sceneId, out snapshot))
        {
            sourceKind = AgasSceneDetailSourceKind.LoadedLive;
        }
        else
        {
            snapshot = _source.LoadFreshAndProject(sceneId);
            sourceKind = AgasSceneDetailSourceKind.FreshDisk;
        }
        _mainThread.EnsureMainThread();

        if (snapshot == null)
            return AgasSceneDetailResult<AgasSceneDetailCaptureResult>.Fail(AgasSceneDetailErrorCodes.NotFound);
        if (!_validator.IsValid(snapshot) || !string.Equals(snapshot.SceneId, sceneId, StringComparison.Ordinal))
            return AgasSceneDetailResult<AgasSceneDetailCaptureResult>.Fail(AgasSceneDetailErrorCodes.Invalid);
        return AgasSceneDetailResult<AgasSceneDetailCaptureResult>.Ok(new(sourceKind, snapshot));
    }
}
