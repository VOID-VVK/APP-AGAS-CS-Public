using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using AppAgasCs.Application;
using AppAgasCs.Application.Contracts;
using AppAgasCs.Core.Persistence;
using AppAgasCs.Core.Resources;
using AppAgasCs.Features.Ai.Prompting;
using AppAgasCs.Mcp.Contracts;
using AppAgasCs.Mcp.Contracts.Output;
using AppAgasCs.Mcp.Contracts.Runtime;
using AppAgasCs.Mcp.Runtime;

namespace AppAgasCs.Mcp;

public sealed class AgasMcpProjectFacade
{
    private const string InvalidToolArgumentsMessage = "Tool arguments did not match the expected JSON shape.";
    private readonly AgasMcpProjectContextProvider _contextProvider;
    private readonly AgasMcpDirtyState _dirtyState;
    private readonly AgasMcpPathValidator _pathValidator;
    private readonly AgasMcpRepositoryErrorMapper _errorMapper;
    private readonly ProjectMutationService _mutationService;
    private readonly IProjectReadModelQuery _projectQuery;
    private readonly AgasSceneDetailQuery _sceneDetailQuery;
    private readonly AgasSceneDetailCursorBroker _sceneCursorBroker;
    private readonly IProjectAccessIdentityProvider _projectIdentityProvider = new FileSystemProjectAccessIdentityProvider();
    private readonly IReadOnlyDictionary<string, Func<IReadOnlyDictionary<string, JsonElement>?, AgasMcpToolResponse>> _handlers;

    public AgasMcpProjectFacade(
        AgasMcpProjectContextProvider contextProvider,
        AgasMcpDirtyState? dirtyState = null,
        AgasMcpPathValidator? pathValidator = null,
        AgasMcpRepositoryErrorMapper? errorMapper = null,
        IProjectReadModelQuery? projectQuery = null,
        AgasSceneDetailQuery? sceneDetailQuery = null,
        AgasSceneDetailCursorBroker? sceneCursorBroker = null)
    {
        _contextProvider = contextProvider;
        _dirtyState = dirtyState ?? new AgasMcpDirtyState();
        _pathValidator = pathValidator ?? new AgasMcpPathValidator();
        _errorMapper = errorMapper ?? new AgasMcpRepositoryErrorMapper();
        _projectQuery = projectQuery ?? new AgasMcpProjectReadModelQuery(contextProvider);
        _sceneDetailQuery = sceneDetailQuery ?? new AgasSceneDetailQuery(
            new AgasMcpSceneDetailSnapshotSource(contextProvider),
            new AgasSceneDetailMainThreadGuard(Environment.CurrentManagedThreadId));
        _sceneCursorBroker = sceneCursorBroker ?? new AgasSceneDetailCursorBroker();
        _mutationService = new ProjectMutationService(new AgasMcpProjectMutationPolicy(_contextProvider, _dirtyState));
        _handlers = new Dictionary<string, Func<IReadOnlyDictionary<string, JsonElement>?, AgasMcpToolResponse>>(StringComparer.Ordinal)
        {
            [AgasMcpToolNames.ProjectSnapshot] = arguments => BindAndExecute<AgasProjectSnapshotInput>(arguments, ProjectSnapshot),
            [AgasMcpToolNames.AssetsList] = arguments => BindAndExecute<AgasAssetsListInput>(arguments, AssetsList),
            [AgasMcpToolNames.SceneMutate] = arguments => BindAndExecute<AgasSceneMutateInput>(arguments, SceneMutate),
            [AgasMcpToolNames.LayerMutate] = arguments => BindAndExecute<AgasLayerMutateInput>(arguments, LayerMutate),
            [AgasMcpToolNames.LayerMergeVisible] = arguments => BindAndExecute<AgasLayerMergeVisibleInput>(arguments, LayerMergeVisible),
            [AgasMcpToolNames.ImageMutate] = arguments => BindAndExecute<AgasImageMutateInput>(arguments, ImageMutate),
            [AgasMcpToolNames.PromptMutate] = arguments => BindAndExecute<AgasPromptMutateInput>(arguments, PromptMutate),
            [AgasMcpToolNames.ViewSet] = arguments => BindAndExecute<AgasViewSetInput>(arguments, ViewSet),
            [AgasMcpToolNames.ProjectSave] = arguments => BindAndExecute<AgasProjectSaveInput>(arguments, ProjectSave),
            [AgasMcpToolNames.SelectionSet] = arguments => BindAndExecute<AgasSelectionSetInput>(arguments, SelectionSet),
            [AgasMcpToolNames.SelectionGet] = arguments => BindAndExecute<AgasSelectionGetInput>(arguments, SelectionGet),
        };
    }

    public AgasMcpDirtyState DirtyState => _dirtyState;

    public AgasMcpToolResponse ProjectSnapshot(AgasProjectSnapshotInput input)
    {
        if (!TryValidateInput(input, out input, out AgasMcpToolResponse? validationError)) return validationError!;
        AgasMcpProjectContext? context = _contextProvider.GetCurrent();
        if (context == null)
        {
            return NoOpenProject();
        }

        AgasMcpPathValidationResult root = _pathValidator.ValidateProjectRoot(context.ProjectRoot);
        if (!root.Success)
        {
            return PathFailure(root);
        }
        if (!TryBindDirtyStateToContext(context, out AgasMcpToolResponse? bindError))
        {
            return bindError!;
        }

        AgasMcpDirtySnapshot dirty = _dirtyState.Snapshot();
        ProjectReadModelSnapshot snapshot = _projectQuery.GetSnapshot(new ProjectReadModelRequest(
            input.IncludeScenes,
            input.IncludeLayers,
            input.IncludeImages,
            input.IncludePrompts,
            LoadScenes: false));
        AgasMcpSceneSummaryDto[] scenes = snapshot.Scenes.Select(SceneSummary).ToArray();
        AgasMcpLayerSummaryDto[] layers = snapshot.Layers.Select(LayerSummary).ToArray();
        AgasMcpImageSummaryDto[] images = snapshot.Images.Select(ImageSummary).ToArray();
        AgasMcpPromptSummaryDto[] prompts = snapshot.Prompts.Select(PromptSummary).ToArray();

        var data = new AgasMcpProjectSnapshotDataDto(
            SafeText(snapshot.ProjectName, 120),
            new AgasMcpGridDto(snapshot.Grid.SizePx, snapshot.Grid.SnapEnabled, snapshot.Grid.Visible),
            scenes.ToImmutableArray(),
            layers.ToImmutableArray(),
            images.ToImmutableArray(),
            prompts.ToImmutableArray(),
            dirty.DirtyProject,
            dirty.DirtyLayout,
            dirty.DirtySceneIds.ToImmutableArray(),
            dirty.PendingDeletedSceneIds.ToImmutableArray(),
            string.IsNullOrWhiteSpace(snapshot.ActiveSceneId) ? null : snapshot.ActiveSceneId);
        return Success(data);
    }

    public AgasMcpToolResponse AssetsList(AgasAssetsListInput input)
    {
        if (!TryValidateInput(input, out input, out AgasMcpToolResponse? validationError)) return validationError!;
        AgasMcpProjectContext? context = _contextProvider.GetCurrent();
        if (context == null)
        {
            return NoOpenProject();
        }

        string query = input.Query ?? string.Empty;

        IEnumerable<string> requestedExtensions = input.Extensions == null || input.Extensions.Count == 0
            ? AgasMcpPathValidator.DefaultAssetExtensions
            : input.Extensions;
        HashSet<string> extensions = new(requestedExtensions
            .Where(item => !string.IsNullOrWhiteSpace(item))
            .Select(item => item.TrimStart('.').ToLowerInvariant()), StringComparer.OrdinalIgnoreCase);
        AgasMcpPathValidationResult root = _pathValidator.ValidateProjectRoot(context.ProjectRoot);
        if (!root.Success)
        {
            return PathFailure(root);
        }

        IReadOnlyList<AgasMcpAssetItemDto> all = _pathValidator.ListAssets(context.ProjectRoot, query, extensions);
        AgasMcpAssetItemDto[] page = all.Skip(input.Offset).Take(input.Limit).ToArray();
        bool hasMore = input.Offset + page.Length < all.Count;
        var data = new AgasMcpAssetsListDataDto(
            page.ToImmutableArray(),
            page.Length,
            all.Count,
            hasMore,
            hasMore ? input.Offset + page.Length : null);
        return Success(data);
    }

    public AgasMcpToolResponse SceneGet(AgasSceneGetInput input)
    {
        if (!TryValidateInput(input, out input, out AgasMcpToolResponse? validationError)) return validationError!;
        AgasMcpProjectContext? context = _contextProvider.GetCurrent();
        if (context == null) return NoOpenProject();
        AgasMcpPathValidationResult root = _pathValidator.ValidateProjectRoot(context.ProjectRoot);
        if (!root.Success) return PathFailure(root);

        ProjectAccessPathIdentity identity;
        try
        {
            identity = _projectIdentityProvider.Capture(root.AbsolutePath);
        }
        catch
        {
            return Failure(AgasMcpErrorCodes.ProjectRootUnsafe, "Project root identity could not be verified.", "Reopen the project from a stable local directory.");
        }
        if (!identity.Exists || !identity.IsDirectory || identity.IsLinkOrReparse)
            return Failure(AgasMcpErrorCodes.ProjectRootUnsafe, "Project root identity is unsafe.", "Reopen the project from a stable local directory.");
        var scope = AgasSceneDetailProjectScope.Create(identity.IdentityToken, context.ProjectId);
        AgasSceneDetailPage page;
        AgasSceneDetailPageLease? lease = null;
        if (input.Cursor == null)
        {
            AgasSceneDetailResult<AgasSceneDetailCaptureResult> captured = _sceneDetailQuery.Capture(input.SceneId);
            if (!captured.Success || captured.Value == null) return SceneDetailFailure(captured.ErrorCode);
            AgasSceneDetailResult<AgasSceneDetailPage> first = _sceneCursorBroker.CreateFirstPage(
                scope, captured.Value.Snapshot, input.Limit);
            if (!first.Success || first.Value == null) return SceneDetailFailure(first.ErrorCode);
            page = first.Value;
        }
        else
        {
            AgasSceneDetailResult<AgasSceneDetailPageLease> next = _sceneCursorBroker.AcquirePage(
                scope, input.SceneId, input.Cursor, input.Limit);
            if (!next.Success || next.Value == null) return SceneDetailFailure(next.ErrorCode);
            lease = next.Value;
            page = lease.Page;
        }

        try
        {
            var data = new AgasMcpSceneGetDataDto(
                page.SceneId,
                SafeText(page.Name, 120),
                new AgasMcpViewStateDto(page.ViewState.CameraX, page.ViewState.CameraY, page.ViewState.Zoom),
                page.Items.Select(SceneItem).ToImmutableArray(),
                page.ActiveLayerId,
                page.NextCursor,
                string.IsNullOrWhiteSpace(page.Readme) ? null : SafeText(page.Readme, 8000));
            return Success(data);
        }
        finally
        {
            lease?.Dispose();
        }
    }

    public AgasMcpToolResponse GridMutate(AgasGridMutateInput input)
    {
        if (!TryValidateInput(input, out input, out AgasMcpToolResponse? validationError)) return validationError!;
        if (input.Op == "setSize" && input.Enabled.HasValue
            || input.Op is "setSnapEnabled" or "setVisible" && input.SizePx.HasValue)
            return Invalid("Grid mutation contains a field unrelated to the selected operation.");
        if (!TryPrepareMutationContext(out AgasMcpProjectContext context, out AgasMcpToolResponse? prepareError)) return prepareError!;

        GridMutationCommand command = input.Op switch
        {
            "setSize" => new(GridMutationKind.SetSize, input.SizePx!.Value),
            "setSnapEnabled" => new(GridMutationKind.SetSnapEnabled, Flag: input.Enabled!.Value),
            "setVisible" => new(GridMutationKind.SetVisible, Flag: input.Enabled!.Value),
            _ => throw new InvalidOperationException("Validated grid operation was not handled."),
        };
        ProjectMutationResult mutation = _mutationService.MutateGrid(context, command);
        if (!mutation.Success) return MutationFailure(mutation);
        AgasMcpDirtySnapshot dirty = _dirtyState.Snapshot();
        ProjectReadModelSnapshot readModel = _projectQuery.GetSnapshot(new ProjectReadModelRequest(LoadScenes: false));
        var data = new AgasMcpGridMutationDataDto(
            new AgasMcpGridDto(readModel.Grid.SizePx, readModel.Grid.SnapEnabled, readModel.Grid.Visible),
            dirty.DirtyProject,
            dirty.DirtyLayout,
            dirty.DirtySceneIds.ToImmutableArray());
        return SuccessDirty(data);
    }

    public AgasMcpToolResponse SceneMutate(AgasSceneMutateInput input)
    {
        if (!TryValidateInput(input, out input, out AgasMcpToolResponse? validationError)) return validationError!;
        if (!TryPrepareMutationContext(out AgasMcpProjectContext context, out AgasMcpToolResponse? prepareError)) return prepareError!;

        ProjectMutationResult mutation = _mutationService.MutateScene(context, new SceneMutationCommand(
            ParseSceneMutationKind(input.Op), input.SceneId, input.Name, input.Readme));
        if (!mutation.Success) return MutationFailure(mutation);

        AgasMcpDirtySnapshot dirty = _dirtyState.Snapshot();
        ProjectReadModelSnapshot readModel = _projectQuery.GetSnapshot(new ProjectReadModelRequest(IncludeScenes: true, LoadScenes: false));
        var data = new AgasMcpSceneMutationDataDto(
            readModel.Scenes.Select(SceneSummary).ToImmutableArray(),
            context.Project.ActiveSceneId,
            mutation.ItemId!,
            dirty.DirtyProject,
            dirty.DirtyLayout,
            dirty.DirtySceneIds.ToImmutableArray(),
            dirty.PendingDeletedSceneIds.ToImmutableArray());
        return SuccessDirty(data);
    }

    public AgasMcpToolResponse LayerMutate(AgasLayerMutateInput input)
    {
        if (!TryValidateInput(input, out input, out AgasMcpToolResponse? validationError)) return validationError!;
        if (!TryPrepareMutationContext(out AgasMcpProjectContext context, out AgasMcpToolResponse? prepareError)) return prepareError!;
        if (!TryGetScene(context, input.SceneId, out AgasSceneResource? scene, out AgasMcpToolResponse? error)) return error!;
        if (input.Op is "rename" or "delete" or "move" or "setCurrent"
            && !TryGetEditableLayer(scene, input.LayerId, out _, out error))
            return error!;

        ProjectMutationResult mutation = _mutationService.MutateLayer(context, new LayerMutationCommand(
            ParseLayerMutationKind(input.Op),
            scene.Id,
            input.LayerId,
            input.Name,
            input.Direction ?? 0,
            input.Visible ?? input.Locked ?? input.Shared ?? false));
        if (!mutation.Success) return MutationFailure(mutation);
        AgasMcpDirtySnapshot dirty = _dirtyState.Snapshot();
        ProjectReadModelSnapshot readModel = _projectQuery.GetSnapshot(new ProjectReadModelRequest(IncludeLayers: true, SceneId: scene.Id));
        var data = new AgasMcpLayerMutationDataDto(
            scene.Id,
            readModel.Layers.Select(LayerSummary).ToImmutableArray(),
            scene.ActiveLayerId,
            mutation.ItemId!,
            dirty.DirtyProject,
            dirty.DirtyLayout,
            dirty.DirtySceneIds.ToImmutableArray());
        return SuccessDirty(data);
    }

    public AgasMcpToolResponse LayerMergeVisible(AgasLayerMergeVisibleInput input)
    {
        if (!TryValidateInput(input, out input, out AgasMcpToolResponse? validationError)) return validationError!;
        if (!TryPrepareMutationContext(out AgasMcpProjectContext context, out AgasMcpToolResponse? prepareError)) return prepareError!;
        if (!TryGetScene(context, input.SceneId, out AgasSceneResource? scene, out AgasMcpToolResponse? error)) return error!;
        if (!TryGetEditableLayer(scene, input.LayerId, out AgasLayerResource? layer, out error)) return error!;
        if (!AgasMcpSelectionValidation.IsActiveScene(context.Project.ActiveSceneId, context.CurrentScene?.Id, input.SceneId))
        {
            return Failure(
                AgasMcpErrorCodes.InvalidOperation,
                "Visible-layer merge only applies to the active scene.",
                "Call agas.scene.mutate with op=setActive before merging layers.");
        }

        if (!string.Equals(scene.ActiveLayerId, input.LayerId, StringComparison.Ordinal))
        {
            AgasMcpToolResponse setCurrent = LayerMutate(new AgasLayerMutateInput("setCurrent", input.SceneId, input.LayerId));
            if (!setCurrent.Ok)
            {
                return setCurrent;
            }
        }

        ICanvasLayerMergeBridge? merge = context.LayerMergeWorkspace;
        if (merge == null
            || !string.Equals(merge.SceneId, input.SceneId, StringComparison.Ordinal))
        {
            return Failure(
                AgasMcpErrorCodes.InvalidOperation,
                "The live canvas is unavailable for layer merge.",
                "Wait for the AGAS workspace to finish opening the scene, then retry.");
        }

        if (!merge.TryMergeVisibleLayersIntoCurrent(input.LayerId, out CanvasVisibleLayerMergeResult merged)
            || !merged.Success)
        {
            string message = string.IsNullOrWhiteSpace(merged.Message)
                ? "Visible layers could not be merged into the current layer."
                : merged.Message;
            return Failure(AgasMcpErrorCodes.InvalidOperation, message, "Show other layers, unlock the current layer, then retry.");
        }

        _dirtyState.MarkSceneDirty(scene.Id);
        AgasMcpDirtySnapshot dirty = _dirtyState.Snapshot();
        context.NotifyDirtyState(dirty);
        context.NotifyStatus(merged.Message);
        ImageInstanceResource? image = layer.ImagesSnapshot.FirstOrDefault(item =>
            string.Equals(item.Id, merged.ImageId, StringComparison.Ordinal))
            ?? layer.ImagesSnapshot.LastOrDefault();
        if (image == null)
        {
            return Failure(
                AgasMcpErrorCodes.InternalError,
                "The merged image was written but could not be read back.",
                "Call agas.project.snapshot to inspect the current layer.");
        }

        var data = new AgasMcpLayerMergeVisibleDataDto(
            scene.Id,
            layer.Id,
            new AgasMcpImageSummaryDto(
                scene.Id,
                layer.Id,
                image.Id,
                SafeOutputRelativePath(image.ImagePath),
                image.Order,
                image.X,
                image.Y,
                image.ScaleX,
                image.ScaleY,
                image.RotationDegrees,
                image.Opacity),
            merged.SourceLayerIds.ToImmutableArray(),
            dirty.DirtySceneIds.ToImmutableArray());
        return SuccessDirty(data);
    }

    public AgasMcpToolResponse ImageMutate(AgasImageMutateInput input)
    {
        if (!TryValidateInput(input, out input, out AgasMcpToolResponse? validationError)) return validationError!;
        if (string.Equals(input.Op, "add", StringComparison.Ordinal)
            && string.IsNullOrWhiteSpace(input.ImagePath))
        {
            return Invalid("Image add requires a non-blank imagePath.");
        }
        if (string.Equals(input.Op, "update", StringComparison.Ordinal)
            && string.IsNullOrWhiteSpace(input.ImagePath)
            && !input.X.HasValue
            && !input.Y.HasValue
            && !input.ScaleX.HasValue
            && !input.ScaleY.HasValue
            && !input.RotationDegrees.HasValue
            && !input.Opacity.HasValue)
        {
            return Invalid("Image update requires at least one non-blank changed field.");
        }
        if (!TryPrepareMutationContext(out AgasMcpProjectContext context, out AgasMcpToolResponse? prepareError)) return prepareError!;
        if (!TryGetScene(context, input.SceneId, out AgasSceneResource? scene, out AgasMcpToolResponse? error)) return error!;
        if (!TryGetEditableLayer(scene, input.LayerId, out AgasLayerResource? layer, out error)) return error!;

        string? validatedImagePath = input.ImagePath;
        if (string.Equals(input.Op, "add", StringComparison.Ordinal) || HasNonBlankImagePath(input))
        {
            AgasMcpPathValidationResult imagePath = _pathValidator.ValidateImageFile(context.ProjectRoot, input.ImagePath ?? string.Empty, mustExist: true);
            if (!imagePath.Success) return PathFailure(imagePath);
            validatedImagePath = imagePath.RelativePath;
        }
        ProjectMutationResult mutation = _mutationService.MutateImage(context, new ImageMutationCommand(
            ParseImageMutationKind(input.Op),
            scene.Id,
            layer.Id,
            input.ImageId,
            validatedImagePath,
            input.X,
            input.Y,
            input.ScaleX,
            input.ScaleY,
            input.RotationDegrees,
            input.Opacity));
        if (!mutation.Success) return MutationFailure(mutation);
        ProjectReadModelSnapshot readModel = _projectQuery.GetSnapshot(new ProjectReadModelRequest(
            IncludeLayers: true,
            IncludeImages: true,
            SceneId: scene.Id,
            LayerId: layer.Id));
        ProjectImageReadModel? image = string.Equals(input.Op, "delete", StringComparison.Ordinal)
            ? null
            : readModel.Images.FirstOrDefault(item => string.Equals(item.Id, mutation.ItemId, StringComparison.Ordinal));
        ProjectLayerReadModel layerReadModel = readModel.Layers.Single(item => string.Equals(item.Id, layer.Id, StringComparison.Ordinal));
        AgasMcpDirtySnapshot dirty = _dirtyState.Snapshot();
        var data = new AgasMcpImageMutationDataDto(
            scene.Id,
            layer.Id,
            LayerSummary(layerReadModel),
            dirty.DirtySceneIds.ToImmutableArray(),
            image == null ? null : ImageSummary(image));
        return SuccessDirty(data);
    }

    internal static bool HasNonBlankImagePath(AgasImageMutateInput input) =>
        !string.IsNullOrWhiteSpace(input.ImagePath);

    public AgasMcpToolResponse PromptMutate(AgasPromptMutateInput input)
    {
        if (!TryValidateInput(input, out input, out AgasMcpToolResponse? validationError)) return validationError!;
        if (input.Op != "add" && input.Op != "update" && input.Role != null)
            return Invalid("Prompt role is only valid for add or update.");
        if (input.Op == "update" && input.Text == null && input.Role == null)
            return Invalid("Prompt update requires text or role.");
        if (!TryPrepareMutationContext(out AgasMcpProjectContext context, out AgasMcpToolResponse? prepareError)) return prepareError!;
        if (!TryGetScene(context, input.SceneId, out AgasSceneResource? scene, out AgasMcpToolResponse? error)) return error!;
        if (!TryGetEditableLayer(scene, input.LayerId, out AgasLayerResource? layer, out error)) return error!;

        int? role = ParsePromptRole(input.Role);
        ProjectMutationResult mutation = _mutationService.MutatePrompt(context, new PromptMutationCommand(
            ParsePromptMutationKind(input.Op),
            scene.Id,
            layer.Id,
            input.FragmentId,
            input.Text,
            input.Enabled ?? true,
            input.Direction ?? 0,
            Role: role));
        if (!mutation.Success) return MutationFailure(mutation);
        AgasMcpDirtySnapshot dirty = _dirtyState.Snapshot();
        ProjectReadModelSnapshot readModel = _projectQuery.GetSnapshot(new ProjectReadModelRequest(
            IncludePrompts: true,
            SceneId: scene.Id,
            LayerId: layer.Id));
        var data = new AgasMcpPromptMutationDataDto(
            scene.Id,
            layer.Id,
            readModel.Prompts.Select(PromptSummary).ToImmutableArray(),
            PromptComposer.LimitPreservingFinalTask(PromptComposer.Compose(
                layer.PromptFragments,
                null,
                PromptCompositionMode.ReferenceEdit), 4000),
            mutation.ItemId!,
            dirty.DirtySceneIds.ToImmutableArray());
        return SuccessDirty(data);
    }

    public AgasMcpToolResponse ViewSet(AgasViewSetInput input)
    {
        if (!TryValidateInput(input, out input, out AgasMcpToolResponse? validationError)) return validationError!;
        if (!TryPrepareMutationContext(out AgasMcpProjectContext context, out AgasMcpToolResponse? prepareError)) return prepareError!;
        if (!TryGetScene(context, input.SceneId, out AgasSceneResource? scene, out AgasMcpToolResponse? error)) return error!;
        ProjectMutationResult mutation = _mutationService.MutateView(context,
            new ViewMutationCommand(scene.Id, input.CameraX, input.CameraY, input.Zoom));
        if (!mutation.Success) return MutationFailure(mutation);
        AgasMcpDirtySnapshot dirty = _dirtyState.Snapshot();
        var data = new AgasMcpViewSetDataDto(
            scene.Id,
            scene.ViewState.CameraX,
            scene.ViewState.CameraY,
            scene.ViewState.Zoom,
            dirty.DirtyProject,
            dirty.DirtyLayout,
            dirty.DirtySceneIds.ToImmutableArray());
        return SuccessDirty(data);
    }

    public AgasMcpToolResponse SelectionSet(AgasSelectionSetInput input)
    {
        if (!TryValidateInput(input, out input, out AgasMcpToolResponse? validationError)) return validationError!;
        if (TryRejectReadOnly(out AgasMcpToolResponse? readOnly)) return readOnly!;
        AgasMcpProjectContext? context = _contextProvider.GetCurrent();
        if (context == null) return NoOpenProject();
        if (!TryGetActiveSelectionContext(context, input.SceneId, out AgasSceneResource? scene, out ICanvasSelectionWorkspaceBridge? workspace, out AgasMcpToolResponse? error))
        {
            return error!;
        }

        switch (input.Op)
        {
            case "setRegions":
            {
                if (!AgasMcpSelectionValidation.TryCreateRegion(input.Selection!, out CanvasSelectionRegion selection)
                    || (input.Replacement != null && !AgasMcpSelectionValidation.TryCreateRegion(input.Replacement, out _)))
                {
                    return SelectionInvalid();
                }

                CanvasSelectionRegion? replacement = input.Replacement == null
                    ? null
                    : new CanvasSelectionRegion(input.Replacement.Column, input.Replacement.Row, input.Replacement.Width, input.Replacement.Height);
                if (!workspace.TrySetRegions(selection, replacement))
                {
                    return SelectionInvalid();
                }
                break;
            }
            case "clearRegions":
                workspace.ClearRegions();
                break;
            case "selectImage":
            {
                if (!TryGetLayer(scene, input.LayerId, out AgasLayerResource? layer, out error))
                {
                    return error!;
                }
                if (!string.Equals(scene.ActiveLayerId, layer.Id, StringComparison.Ordinal))
                {
                    return Failure(
                        AgasMcpErrorCodes.InvalidOperation,
                        "Target layer is not the current canvas layer.",
                        "Call agas.layer.mutate with op=setCurrent before selecting an image.");
                }
                if (!AgasMcpSelectionValidation.IsLayerEditable(layer.Visible, layer.Locked))
                {
                    return Failure(
                        AgasMcpErrorCodes.LayerNotEditable,
                        "Target layer must be visible and unlocked before an image can be selected.",
                        "Show and unlock the current layer before selecting its image.");
                }

                string imageId = RequiredId(input.ImageId, nameof(input.ImageId));
                if (FindImage(layer, imageId) == null)
                {
                    return ImageNotFound(imageId);
                }
                if (!workspace.TrySelectImage(imageId))
                {
                    return Failure(
                        AgasMcpErrorCodes.InvalidOperation,
                        "Canvas rejected the image selection because its live context changed.",
                        "Read agas.selection.get and the project snapshot, then retry.");
                }
                break;
            }
            case "clearImageSelection":
                workspace.ClearImageSelection();
                break;
            default:
                throw new InvalidOperationException("Validated selection operation was not handled.");
        }

        return Success(SelectionData(workspace));
    }

    public AgasMcpToolResponse SelectionGet(AgasSelectionGetInput input)
    {
        if (!TryValidateInput(input, out input, out AgasMcpToolResponse? validationError)) return validationError!;
        AgasMcpProjectContext? context = _contextProvider.GetCurrent();
        if (context == null) return NoOpenProject();
        string sceneId = context.Project.ActiveSceneId;
        if (!TryGetActiveSelectionContext(context, sceneId, out _, out ICanvasSelectionWorkspaceBridge? workspace, out AgasMcpToolResponse? error))
        {
            return error!;
        }

        return Success(SelectionGetData(workspace));
    }

    public AgasMcpToolResponse ProjectSave(AgasProjectSaveInput input) =>
        ProjectSaveCore(input, execution: null, receiptTracker: null, CancellationToken.None);

    public AgasMcpToolResponse ProjectSave(
        AgasProjectSaveInput input,
        AgasMcpCommitExecution execution,
        AgasMcpProjectSaveReceiptTracker receiptTracker,
        CancellationToken cancellationToken) =>
        ProjectSaveCore(
            input,
            execution ?? throw new ArgumentNullException(nameof(execution)),
            receiptTracker ?? throw new ArgumentNullException(nameof(receiptTracker)),
            cancellationToken);

    private AgasMcpToolResponse ProjectSaveCore(
        AgasProjectSaveInput input,
        AgasMcpCommitExecution? execution,
        AgasMcpProjectSaveReceiptTracker? receiptTracker,
        CancellationToken cancellationToken)
    {
        if (!TryValidateInput(input, out input, out AgasMcpToolResponse? validationError)) return validationError!;
        if (!TryPrepareMutationContext(out AgasMcpProjectContext context, out AgasMcpToolResponse? prepareError)) return prepareError!;
        bool needsMigration = context.Project.NeedsMigration;
        AgasPersistenceResult migration = AgasProjectMigration.PrepareForControlledSave(context.Project);
        if (!migration.Success)
        {
            return Failure(AgasMcpErrorCodes.SaveFailed, migration.ErrorMessage ?? "Project migration validation failed.", "Open the project in a compatible AGAS version before saving.");
        }
        if (needsMigration)
        {
            _dirtyState.MarkProjectDirty();
        }

        List<string> savedFiles = new();
        List<AgasMcpSaveFailureDto> failedFiles = new();
        AgasMcpDirtySnapshot before = _dirtyState.Snapshot();
        AgasMcpProjectSaveDataDto BuildReceipt()
        {
            string[] pending = _dirtyState.PendingDirtyFiles(context.Project).ToArray();
            string[] saved = savedFiles.Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToArray();
            string saveState = pending.Length == 0
                ? "complete"
                : saved.Length > 0 ? "partial" : "none";
            return new AgasMcpProjectSaveDataDto(
                saveState,
                saved.ToImmutableArray(),
                failedFiles.OrderBy(item => item.Path, StringComparer.Ordinal).ToImmutableArray(),
                pending.ToImmutableArray());
        }
        receiptTracker?.Reset(BuildReceipt());
        using ProjectSaveCommitLease? commitLease = execution == null
            ? null
            : ProjectSaveCommitLease.TryBegin(execution, cancellationToken);
        if (execution != null && commitLease == null)
        {
            return Failure(
                cancellationToken.IsCancellationRequested ? AgasMcpErrorCodes.Stopped : AgasMcpErrorCodes.Timeout,
                "Project save was canceled before durable completion started.",
                "Retry the save after the workspace becomes idle.");
        }

        foreach (string sceneId in before.DirtySceneIds.ToArray())
        {
            AgasSceneResource? scene = context.TryGetCachedScene(sceneId);
            if (scene == null)
            {
                failedFiles.Add(new AgasMcpSaveFailureDto(AgasMcpDirtyState.SceneFile(sceneId), AgasMcpErrorCodes.SceneNotFound, "Dirty scene is not loaded in memory."));
                receiptTracker?.Publish(BuildReceipt());
                continue;
            }
            string logical = AgasMcpDirtyState.SceneFile(sceneId);
            AgasMcpPathValidationResult scenePath = _pathValidator.ValidateAgasSceneFile(context.ProjectRoot, sceneId);
            if (!scenePath.Success)
            {
                failedFiles.Add(new AgasMcpSaveFailureDto(logical, scenePath.Code, scenePath.Message));
                receiptTracker?.Publish(BuildReceipt());
                continue;
            }
            AgasPersistenceResult result = context.SaveResource(scene, scenePath.AbsolutePath);
            if (result.Success)
            {
                savedFiles.Add(logical);
                _dirtyState.ClearSceneDirty(sceneId);
            }
            else
            {
                failedFiles.Add(_errorMapper.ToFailure(logical, result));
            }
            receiptTracker?.Publish(BuildReceipt());
        }

        bool projectSaved = true;
        if (_dirtyState.Snapshot().DirtyProject || before.PendingDeletedSceneIds.Count > 0)
        {
            string logical = $"{AgasPaths.AgasDirectoryName}/{AgasPaths.ProjectFileName}";
            AgasMcpPathValidationResult projectPath = _pathValidator.ValidateAgasProjectFile(context.ProjectRoot);
            AgasPersistenceResult result;
            if (projectPath.Success)
            {
                result = context.SaveResource(context.Project, projectPath.AbsolutePath);
            }
            else
            {
                result = AgasPersistenceResult.Fail(projectPath.Message);
                projectSaved = false;
                failedFiles.Add(new AgasMcpSaveFailureDto(logical, projectPath.Code, projectPath.Message));
            }
            projectSaved = result.Success;
            if (result.Success)
            {
                savedFiles.Add(logical);
                _dirtyState.ClearProjectDirty();
            }
            else if (projectPath.Success)
            {
                failedFiles.Add(_errorMapper.ToFailure(logical, result));
            }
            receiptTracker?.Publish(BuildReceipt());
        }

        if (_dirtyState.Snapshot().DirtyLayout)
        {
            string logical = $"{AgasPaths.AgasDirectoryName}/{AgasPaths.WorkspaceLayoutFileName}";
            AgasMcpPathValidationResult layoutPath = _pathValidator.ValidateAgasLayoutFile(context.ProjectRoot);
            AgasPersistenceResult result;
            if (layoutPath.Success)
            {
                result = context.SaveResource(context.Layout, layoutPath.AbsolutePath);
            }
            else
            {
                result = AgasPersistenceResult.Fail(layoutPath.Message);
                failedFiles.Add(new AgasMcpSaveFailureDto(logical, layoutPath.Code, layoutPath.Message));
            }
            if (result.Success)
            {
                savedFiles.Add(logical);
                _dirtyState.ClearLayoutDirty();
            }
            else if (layoutPath.Success)
            {
                failedFiles.Add(_errorMapper.ToFailure(logical, result));
            }
            receiptTracker?.Publish(BuildReceipt());
        }

        if (projectSaved)
        {
            foreach (string sceneId in before.PendingDeletedSceneIds.ToArray())
            {
                string logical = AgasMcpDirtyState.SceneFile(sceneId);
                try
                {
                    AgasMcpPathValidationResult scenePath = _pathValidator.ValidateAgasSceneFile(context.ProjectRoot, sceneId);
                    if (!scenePath.Success)
                    {
                        failedFiles.Add(new AgasMcpSaveFailureDto(logical, scenePath.Code, scenePath.Message));
                        receiptTracker?.Publish(BuildReceipt());
                        continue;
                    }
                    string absolute = scenePath.AbsolutePath;
                    if (File.Exists(absolute)) File.Delete(absolute);
                    savedFiles.Add(logical);
                    _dirtyState.ClearPendingDelete(sceneId);
                }
                catch
                {
                    failedFiles.Add(new AgasMcpSaveFailureDto(logical, AgasMcpErrorCodes.SaveFailed, "Deleted scene file could not be removed."));
                }
                receiptTracker?.Publish(BuildReceipt());
            }
        }

        AgasMcpProjectSaveDataDto data = BuildReceipt();
        receiptTracker?.Publish(data);

        AgasMcpDirtySnapshot after = _dirtyState.Snapshot();
        context.NotifyDirtyState(after);
        context.NotifyStatus(_dirtyState.IsDirty ? "MCP project save partially completed." : "MCP project save completed.", _dirtyState.IsDirty);
        return AgasMcpToolResponse.Success(saved: !_dirtyState.IsDirty, dirty: _dirtyState.IsDirty, data);
    }

    public AgasMcpToolResponse Execute(string toolName, IReadOnlyDictionary<string, JsonElement>? arguments)
    {
        try
        {
            return _handlers.TryGetValue(toolName, out var handler)
                ? handler(arguments)
                : Invalid("Unknown MCP tool.");
        }
        catch (ArgumentException ex)
        {
            return Invalid(ex.Message);
        }
        catch
        {
            return Failure(AgasMcpErrorCodes.InternalError, "MCP tool failed internally.", "Check AGAS logs and retry.");
        }
    }

    private AgasMcpToolResponse BindAndExecute<T>(
        IReadOnlyDictionary<string, JsonElement>? arguments,
        Func<T, AgasMcpToolResponse> execute)
        where T : class
    {
        AgasMcpContractBindResult<T> bound = AgasMcpContractRegistry.Bind<T>(arguments);
        return bound.Success && bound.Value != null
            ? execute(bound.Value)
            : ContractValidationFailure<T>(bound.Error);
    }

    private bool TryValidateInput<T>(
        T input,
        out T validated,
        out AgasMcpToolResponse? error)
        where T : class
    {
        AgasMcpContractBindResult<T> result = AgasMcpContractRegistry.ValidateInstance(input);
        if (!result.Success || result.Value == null)
        {
            validated = null!;
            error = ContractValidationFailure<T>(result.Error);
            return false;
        }

        validated = result.Value;
        error = null;
        return true;
    }

    private AgasMcpToolResponse ContractValidationFailure<T>(AgasMcpContractValidationError? error)
        where T : class =>
        typeof(T) == typeof(AgasSelectionSetInput) && IsSelectionRegionNumericRangeError(error)
            ? SelectionInvalid()
            : Invalid(InvalidToolArgumentsMessage);

    private static bool IsSelectionRegionNumericRangeError(AgasMcpContractValidationError? error)
    {
        if (error == null
            || error.Code is not (AgasMcpContractValidationCodes.Minimum or AgasMcpContractValidationCodes.Maximum))
        {
            return false;
        }

        return IsRegionProperty(error.Path, "$.selection.")
            || IsRegionProperty(error.Path, "$.replacement.");
    }

    private static bool IsRegionProperty(string path, string prefix)
    {
        if (!path.StartsWith(prefix, StringComparison.Ordinal)) return false;
        string property = path[prefix.Length..];
        return property is "column" or "row" or "width" or "height";
    }

    private bool TryPrepareMutationContext(
        out AgasMcpProjectContext context,
        out AgasMcpToolResponse? error)
    {
        context = null!;
        if (TryRejectReadOnly(out error)) return false;

        AgasMcpProjectContext? current = _contextProvider.GetCurrent();
        if (current == null)
        {
            error = NoOpenProject();
            return false;
        }

        AgasMcpPathValidationResult root = _pathValidator.ValidateProjectRoot(current.ProjectRoot);
        if (!root.Success)
        {
            error = PathFailure(root);
            return false;
        }

        if (!TryBindDirtyStateToContext(current, out error)) return false;
        context = current;
        return true;
    }

    private bool TryBindDirtyStateToContext(AgasMcpProjectContext context, out AgasMcpToolResponse? error)
    {
        error = null;
        if (!AgasMcpPathValidator.IsSafeIdentifier(context.Project.ProjectId))
        {
            error = Invalid("Current projectId is missing or invalid.");
            return false;
        }

        if (!_dirtyState.TryBindProject(context.ProjectRoot, context.Project.ProjectId, out string errorMessage))
        {
            error = Failure(AgasMcpErrorCodes.InvalidOperation, errorMessage, "Save the dirty project or reopen the intended project before retrying.");
            return false;
        }

        return true;
    }

    private bool TryRejectReadOnly(out AgasMcpToolResponse? error)
    {
        AgasMcpProjectAccessState access = _contextProvider.GetCurrentAccessState();
        if (!access.HasOpenProject || !access.RequiresReadOnly)
        {
            error = null;
            return false;
        }

        string reason = string.IsNullOrWhiteSpace(access.ReadOnlyReasonCode)
            ? AgasProjectResource.FutureProjectReadOnlyCode
            : access.ReadOnlyReasonCode;
        error = AgasMcpToolResponse.Failure(
            saved: false,
            dirty: _dirtyState.IsDirty,
            AgasMcpErrorCodes.ProjectReadOnly,
            $"Project is read-only ({reason}).",
            "Open the project in a compatible AGAS version; read-only projects cannot be mutated or saved.");
        return true;
    }

    private bool TryGetScene(AgasMcpProjectContext context, string sceneId, out AgasSceneResource scene, out AgasMcpToolResponse? error)
    {
        scene = null!;
        error = null;
        sceneId = RequiredId(sceneId, nameof(sceneId));
        if (FindSceneIndex(context.Project, sceneId) == null)
        {
            error = SceneNotFound(sceneId);
            return false;
        }
        AgasSceneResource? loaded = context.TryGetLoadedScene(sceneId);
        if (loaded == null)
        {
            error = SceneNotFound(sceneId);
            return false;
        }
        scene = loaded;
        return true;
    }

    private bool TryGetActiveSelectionContext(
        AgasMcpProjectContext context,
        string? requestedSceneId,
        out AgasSceneResource scene,
        out ICanvasSelectionWorkspaceBridge workspace,
        out AgasMcpToolResponse? error)
    {
        scene = null!;
        workspace = null!;
        error = null;
        string sceneId = RequiredId(requestedSceneId, nameof(requestedSceneId));
        if (FindSceneIndex(context.Project, sceneId) == null)
        {
            error = SceneNotFound(sceneId);
            return false;
        }
        if (!AgasMcpSelectionValidation.IsActiveScene(context.Project.ActiveSceneId, context.CurrentScene?.Id, sceneId))
        {
            error = Failure(
                AgasMcpErrorCodes.InvalidOperation,
                "Selection operations only apply to the active scene.",
                "Call agas.scene.mutate with op=setActive before changing or reading the canvas selection.");
            return false;
        }
        if (context.SelectionWorkspace == null
            || !string.Equals(context.SelectionWorkspace.SceneId, sceneId, StringComparison.Ordinal))
        {
            error = Failure(
                AgasMcpErrorCodes.InvalidOperation,
                "The live canvas selection workspace is unavailable for the active scene.",
                "Wait for the AGAS workspace to finish opening the scene, then retry.");
            return false;
        }

        scene = context.CurrentScene!;
        workspace = context.SelectionWorkspace;
        return true;
    }

    private static AgasMcpSelectionDataDto SelectionData(ICanvasSelectionWorkspaceBridge workspace) => new(
        workspace.SceneId,
        workspace.ActiveRegionTool.ToString(),
        workspace.GridSizePx,
        workspace.CanEditCurrentLayer,
        RegionDto(workspace.SelectionRegion),
        RegionDto(workspace.ReplacementRegion),
        RegionDto(workspace.EffectiveGenerationRegion),
        workspace.SelectedImageId,
        workspace.SelectedLayerId);

    private static AgasMcpSelectionGetDataDto SelectionGetData(ICanvasSelectionWorkspaceBridge workspace) => new(
        workspace.SceneId,
        workspace.ActiveRegionTool.ToString(),
        workspace.GridSizePx,
        workspace.CanEditCurrentLayer,
        RegionDto(workspace.SelectionRegion),
        RegionDto(workspace.ReplacementRegion),
        RegionDto(workspace.EffectiveGenerationRegion),
        workspace.SelectedImageId,
        workspace.SelectedLayerId);

    private static AgasMcpGridRegionDto? RegionDto(CanvasSelectionRegion? region) => region.HasValue
        ? new AgasMcpGridRegionDto(region.Value.Column, region.Value.Row, region.Value.Width, region.Value.Height)
        : null;

    private bool TryGetLayer(AgasSceneResource scene, string? layerId, out AgasLayerResource layer, out AgasMcpToolResponse? error)
    {
        layer = null!;
        error = null;
        string id = RequiredId(layerId, nameof(layerId));
        AgasLayerResource? found = scene.Layers.FirstOrDefault(item => item.Id == id);
        if (found == null)
        {
            error = Failure(AgasMcpErrorCodes.LayerNotFound, "Layer was not found.", "Use agas.project.snapshot first and pass a known layerId.");
            return false;
        }

        layer = found;
        return true;
    }

    private bool TryGetEditableLayer(AgasSceneResource scene, string? layerId, out AgasLayerResource layer, out AgasMcpToolResponse? error)
    {
        layer = null!;
        error = null;
        if (!TryGetLayer(scene, layerId, out AgasLayerResource? found, out error))
        {
            return false;
        }
        if (!found.Visible)
        {
            error = Failure(AgasMcpErrorCodes.LayerHidden, "Target layer is hidden and cannot be modified by MCP.", "Choose a visible unlocked layer.");
            return false;
        }
        if (found.Locked)
        {
            error = Failure(AgasMcpErrorCodes.LayerLocked, "Target layer is locked and cannot be modified by MCP.", "Choose a visible unlocked layer.");
            return false;
        }
        layer = found;
        return true;
    }

    private static SceneIndexResource? FindSceneIndex(AgasProjectResource project, string sceneId) =>
        project.Scenes.FirstOrDefault(scene => string.Equals(scene.Id, sceneId, StringComparison.Ordinal));

    private static ImageInstanceResource? FindImage(AgasLayerResource layer, string imageId) =>
        layer.Images.FirstOrDefault(image => string.Equals(image.Id, imageId, StringComparison.Ordinal));

    private static string RequiredId(string? value, string fieldName = "id")
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            throw new ArgumentException($"Required {fieldName} is missing.");
        }

        string id = value.Trim();
        if (!AgasMcpPathValidator.IsSafeIdentifier(id))
        {
            throw new ArgumentException($"{fieldName} must be 1-120 characters and contain only ASCII letters, digits, underscore, or hyphen.");
        }

        return id;
    }
    private static string SafeText(string? value, int maxLength) => string.IsNullOrEmpty(value) ? string.Empty : value.Length <= maxLength ? value : value[..maxLength];

    private static SceneMutationKind ParseSceneMutationKind(string op) => op switch
    {
        "add" => SceneMutationKind.Add,
        "rename" => SceneMutationKind.Rename,
        "delete" => SceneMutationKind.Delete,
        "setActive" => SceneMutationKind.SetActive,
        "setReadme" => SceneMutationKind.SetReadme,
        _ => throw new InvalidOperationException("Validated scene operation was not handled."),
    };

    private static LayerMutationKind ParseLayerMutationKind(string op) => op switch
    {
        "add" => LayerMutationKind.Add,
        "rename" => LayerMutationKind.Rename,
        "delete" => LayerMutationKind.Delete,
        "move" => LayerMutationKind.Move,
        "setVisible" => LayerMutationKind.SetVisible,
        "setLocked" => LayerMutationKind.SetLocked,
        "setShared" => LayerMutationKind.SetShared,
        "setCurrent" => LayerMutationKind.SetCurrent,
        _ => throw new InvalidOperationException("Validated layer operation was not handled."),
    };

    private static ImageMutationKind ParseImageMutationKind(string op) => op switch
    {
        "add" => ImageMutationKind.Add,
        "delete" => ImageMutationKind.Delete,
        "move" or "update" => ImageMutationKind.Update,
        _ => throw new InvalidOperationException("Validated image operation was not handled."),
    };

    private static PromptMutationKind ParsePromptMutationKind(string op) => op switch
    {
        "add" => PromptMutationKind.Add,
        "delete" => PromptMutationKind.Delete,
        "update" => PromptMutationKind.Update,
        "move" => PromptMutationKind.Move,
        "setEnabled" => PromptMutationKind.SetEnabled,
        _ => throw new InvalidOperationException("Validated prompt operation was not handled."),
    };

    private static int? ParsePromptRole(string? role) => role switch
    {
        null => null,
        "general" => (int)PromptFragmentRole.General,
        "environment" => (int)PromptFragmentRole.Environment,
        "negativeConstraint" => (int)PromptFragmentRole.NegativeConstraint,
        _ => throw new InvalidOperationException("Validated prompt role was not handled."),
    };

    private static AgasMcpSceneSummaryDto SceneSummary(ProjectSceneReadModel scene) =>
        new(scene.Id, SafeText(scene.Name, 120), scene.Order, scene.Loaded);

    private static AgasMcpLayerSummaryDto LayerSummary(ProjectLayerReadModel layer) =>
        new(layer.SceneId, layer.Id, SafeText(layer.Name, 120), layer.Order, layer.Visible, layer.Locked, layer.Shared, layer.ImageCount, layer.PromptCount);

    private static AgasMcpImageSummaryDto ImageSummary(ProjectImageReadModel image) =>
        new(image.SceneId, image.LayerId, image.Id, SafeOutputRelativePath(image.ImagePath), image.Order,
            image.X, image.Y, image.ScaleX, image.ScaleY, image.RotationDegrees, image.Opacity);

    private static AgasMcpPromptSummaryDto PromptSummary(ProjectPromptReadModel prompt) =>
        new(prompt.SceneId, prompt.LayerId, prompt.Id, SafeText(prompt.Text, 4000), prompt.Role, prompt.Enabled, prompt.Order);

    private static AgasMcpSceneItemDto SceneItem(AgasSceneDetailItem item) => item switch
    {
        AgasSceneDetailLayerItem layer => new AgasMcpSceneLayerItemDto(
            "layer", layer.SceneId, layer.LayerId, SafeText(layer.Name, 120), layer.Order,
            layer.Visible, layer.Locked, layer.Shared, layer.ImageCount, layer.PromptCount),
        AgasSceneDetailImageItem image => new AgasMcpSceneImageItemDto(
            "image", image.SceneId, image.LayerId, image.ImageId, SafeOutputRelativePath(image.ImagePath),
            image.Order, image.X, image.Y, image.ScaleX, image.ScaleY, image.RotationDegrees, image.Opacity),
        AgasSceneDetailPromptItem prompt => new AgasMcpScenePromptItemDto(
            "prompt", prompt.SceneId, prompt.LayerId, prompt.FragmentId, SafeText(prompt.Text, 4000),
            prompt.Role, prompt.Enabled, prompt.Order),
        _ => throw new InvalidOperationException("Unsupported scene detail item."),
    };


    private static string SafeOutputRelativePath(string? path)
    {
        if (string.IsNullOrWhiteSpace(path)) return string.Empty;
        string value = path.Trim().Replace('\\', '/');
        if (Path.IsPathRooted(value)
            || value.Contains("://", StringComparison.Ordinal)
            || value.StartsWith("data:", StringComparison.OrdinalIgnoreCase)
            || value.StartsWith("file:", StringComparison.OrdinalIgnoreCase)
            || value.Contains(";base64", StringComparison.OrdinalIgnoreCase)
            || value.Contains("base64,", StringComparison.OrdinalIgnoreCase)
            || value.Split('/', StringSplitOptions.RemoveEmptyEntries).Any(part => part == ".."))
        {
            return string.Empty;
        }
        return value;
    }

    private AgasMcpToolResponse Success(object data) => AgasMcpToolResponse.Success(saved: !_dirtyState.IsDirty, dirty: _dirtyState.IsDirty, data);
    private AgasMcpToolResponse SuccessDirty(object data) => AgasMcpToolResponse.Success(saved: false, dirty: true, data);
    private AgasMcpToolResponse NoOpenProject() => Failure(AgasMcpErrorCodes.NoOpenProject, "No AGAS project is currently open.", "Open a project in AGAS before calling this tool.");
    private AgasMcpToolResponse SceneNotFound(string sceneId) => Failure(AgasMcpErrorCodes.SceneNotFound, "Scene was not found or is not loaded in memory.", "Use agas.project.snapshot first and pass an active loaded sceneId.");
    private AgasMcpToolResponse SceneDetailFailure(string code) => code switch
    {
        AgasSceneDetailErrorCodes.NotFound => Failure(code, "Scene was not found.", "Use agas.project.snapshot and pass a known sceneId."),
        AgasSceneDetailErrorCodes.CursorInvalid => Failure(code, "Scene cursor is invalid or expired.", "Restart pagination by calling agas.scene.get without cursor."),
        AgasSceneDetailErrorCodes.CursorCapacity => Failure(code, "Scene cursor capacity is unavailable.", "Retry after existing scene cursors expire."),
        AgasSceneDetailErrorCodes.SnapshotTooLarge => Failure(code, "Scene snapshot exceeds the safe response budget.", "Reduce scene complexity before requesting details."),
        _ => Failure(AgasSceneDetailErrorCodes.Invalid, "Scene data is invalid.", "Repair or reopen the scene before retrying."),
    };
    private AgasMcpToolResponse ImageNotFound(string imageId) => Failure(AgasMcpErrorCodes.ImageNotFound, "Image instance was not found.", "Use agas.project.snapshot with includeImages=true first.");
    private AgasMcpToolResponse PromptNotFound(string promptId) => Failure(AgasMcpErrorCodes.PromptNotFound, "Prompt fragment was not found.", "Use agas.project.snapshot with includePrompts=true first.");
    private AgasMcpToolResponse SelectionInvalid() => Failure(AgasMcpErrorCodes.SelectionInvalid, "Canvas grid region is invalid or exceeds the supported integer range.", "Pass positive width/height values whose grid endpoints fit a signed 32-bit integer.");
    private AgasMcpToolResponse Invalid(string message) => Failure(AgasMcpErrorCodes.InvalidOperation, message, "Check the documented tool input schema and current project snapshot.");
    private AgasMcpToolResponse MutationFailure(ProjectMutationResult result) => result.Error switch
    {
        ProjectMutationError.SceneNotFound => SceneNotFound(result.SceneId ?? string.Empty),
        ProjectMutationError.LayerNotFound => Failure(AgasMcpErrorCodes.LayerNotFound, result.Message, "Use agas.project.snapshot first and pass a known layerId."),
        ProjectMutationError.LayerHidden => Failure(AgasMcpErrorCodes.LayerHidden, result.Message, "Choose a visible unlocked layer."),
        ProjectMutationError.LayerLocked => Failure(AgasMcpErrorCodes.LayerLocked, result.Message, "Choose a visible unlocked layer."),
        ProjectMutationError.ImageNotFound => ImageNotFound(result.ItemId ?? string.Empty),
        ProjectMutationError.PromptNotFound => PromptNotFound(result.ItemId ?? string.Empty),
        _ => Invalid(result.Message),
    };
    private AgasMcpToolResponse Failure(string code, string message, string suggestion) => AgasMcpToolResponse.Failure(saved: !_dirtyState.IsDirty, dirty: _dirtyState.IsDirty, code, message, suggestion);
    private AgasMcpToolResponse PathFailure(AgasMcpPathValidationResult validation) => Failure(validation.Code, validation.Message, "Use agas.assets.list first and pass a returned project-relative path.");

    private sealed class ProjectSaveCommitLease : IDisposable
    {
        private readonly AgasMcpCommitExecution _execution;
        private readonly CancellationTokenRegistration _registration;
        private int _disposed;

        private ProjectSaveCommitLease(
            AgasMcpCommitExecution execution,
            CancellationToken cancellationToken)
        {
            _execution = execution;
            _registration = cancellationToken.UnsafeRegister(
                static state => ((AgasMcpCommitExecution)state!).TryCancel(), execution);
        }

        public static ProjectSaveCommitLease? TryBegin(
            AgasMcpCommitExecution execution,
            CancellationToken cancellationToken)
        {
            if (execution.Policy != AgasMcpCommitPolicy.StartedMainThreadCompletion)
                throw new ArgumentException("Project save requires StartedMainThreadCompletion.", nameof(execution));
            var lease = new ProjectSaveCommitLease(execution, cancellationToken);
            if (cancellationToken.IsCancellationRequested) execution.TryCancel();
            if (execution.TryBeginMainThreadCompletion()) return lease;
            lease._registration.Dispose();
            return null;
        }

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
            _registration.Dispose();
            if (_execution.State == AgasMcpCommitExecutionState.MainThreadCompleting)
                _execution.CompleteCommit();
        }
    }
}
