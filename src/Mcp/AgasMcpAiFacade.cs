#nullable enable

using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using AppAgasCs.Features.Ai.Generation;
using AppAgasCs.Features.Ai.Settings;
using AppAgasCs.Features.Ai.Workspace;
using AppAgasCs.Mcp.Contracts;
using AppAgasCs.Mcp.Contracts.Runtime;
using Agas.Features.Canvas;

namespace AppAgasCs.Mcp;

public sealed class AgasMcpAiFacade
{
    private const string InvalidToolArgumentsMessage = "Tool arguments did not match the expected JSON shape.";

    private readonly AgasMcpProjectContextProvider _contextProvider;
    private readonly AgasMcpDirtyState _dirtyState;
    private readonly AgasMcpAiJobStore _store;
    private readonly AgasMcpAiJobScheduler _scheduler;
    private readonly IAiGenerationRunner _runner;
    private readonly IReadOnlyDictionary<string, Func<IReadOnlyDictionary<string, JsonElement>?, AgasMcpToolResponse>> _handlers;

    public AgasMcpAiFacade(
        AgasMcpProjectContextProvider contextProvider,
        AgasMcpDirtyState dirtyState,
        AgasMcpAiJobStore store,
        AgasMcpAiJobScheduler scheduler,
        IAiGenerationRunner runner)
    {
        _contextProvider = contextProvider ?? throw new ArgumentNullException(nameof(contextProvider));
        _dirtyState = dirtyState ?? throw new ArgumentNullException(nameof(dirtyState));
        _store = store ?? throw new ArgumentNullException(nameof(store));
        _scheduler = scheduler ?? throw new ArgumentNullException(nameof(scheduler));
        _runner = runner ?? throw new ArgumentNullException(nameof(runner));
        _handlers = new Dictionary<string, Func<IReadOnlyDictionary<string, JsonElement>?, AgasMcpToolResponse>>(StringComparer.Ordinal)
        {
            [AgasMcpToolNames.AiGenerate] = arguments => BindAndExecute<AgasAiGenerateInput>(arguments, Generate),
            [AgasMcpToolNames.AiStatus] = arguments => BindAndExecute<AgasAiStatusInput>(arguments, Status),
            [AgasMcpToolNames.AiCancel] = arguments => BindAndExecute<AgasAiCancelInput>(arguments, Cancel),
            [AgasMcpToolNames.AiApply] = arguments => BindAndExecute<AgasAiApplyInput>(arguments, Apply),
        };
    }

    public AgasMcpToolResponse Execute(
        string toolName,
        IReadOnlyDictionary<string, JsonElement>? arguments)
    {
        try
        {
            return _handlers.TryGetValue(toolName, out var handler)
                ? handler(arguments)
                : Invalid("Unknown AI MCP tool.");
        }
        catch (ArgumentException exception)
        {
            return Invalid(exception.Message);
        }
        catch
        {
            return Failure(AgasMcpErrorCodes.InternalError, "AI MCP tool failed internally.", "Check AGAS logs and retry.");
        }
    }

    public AgasMcpToolResponse Generate(AgasAiGenerateInput input)
    {
        if (!TryValidateInput(input, out input, out AgasMcpToolResponse? validationError)) return validationError!;
        if (input.Prompt != null && string.IsNullOrWhiteSpace(input.Prompt))
        {
            return Invalid("prompt must not be blank when explicitly provided.");
        }
        CanvasPatchLayerScope layerScope = Enum.Parse<CanvasPatchLayerScope>(input.LayerScope);

        AgasMcpProjectContext? project = _contextProvider.GetCurrent();
        if (project == null) return NoOpenProject();
        if (project.RequiresReadOnly)
        {
            return Failure(AgasMcpErrorCodes.ProjectReadOnly, "The current AGAS project is read-only.", "Open a writable project before generating an image.");
        }
        if (project.AiGenerationWorkspace == null)
        {
            return Failure(AgasMcpErrorCodes.InvalidOperation, "The live AI canvas workspace is unavailable.", "Wait for the AGAS workspace to finish opening, then retry.");
        }

        IAgasAiGenerationWorkspaceBridge bridge = project.AiGenerationWorkspace;
        AiGenerationCanvasContext? context = bridge.CaptureCurrentContext(layerScope);
        if (context == null)
        {
            return NoOpenProject();
        }
        if (!string.Equals(project.Project.ActiveSceneId, input.SceneId, StringComparison.Ordinal)
            || !string.Equals(context.SceneId, input.SceneId, StringComparison.Ordinal)
            || !string.Equals(context.LayerId, input.LayerId, StringComparison.Ordinal))
        {
            return Failure(AgasMcpErrorCodes.InvalidOperation, "AI generation only applies to the active scene and current layer.", "Activate the requested scene and layer before generating.");
        }
        if (!context.CanEditCurrentLayer)
        {
            return Failure(AgasMcpErrorCodes.LayerNotEditable, "The current layer is hidden or locked.", "Show and unlock the current layer before generating.");
        }

        AiGenerationTarget target = input.Target switch
        {
            "NewImage" => AiGenerationTarget.AddToCurrentLayer,
            "ReplaceSelected" => AiGenerationTarget.ReplaceSelected,
            _ => throw new InvalidOperationException("Validated AI target was not handled."),
        };
        if (target == AiGenerationTarget.ReplaceSelected
            && (!context.HasSelectedImage || string.IsNullOrWhiteSpace(context.SelectedImageId)))
        {
            return Failure(AgasMcpErrorCodes.ImageNotSelected, "ReplaceSelected requires a selected image.", "Select an image with agas.selection.set before generating.");
        }
        if (context.RegionMetadata?.SelectionRegion == null)
        {
            return Failure(AgasMcpErrorCodes.SelectionInvalid, "AI generation requires a Q selection region.", "Set the canvas selection with agas.selection.set before generating.");
        }

        // Resolve first so MCP override is composed once. Reflect after so UI shows the agent
        // text without doubling it into ResolvePrompt (fragment + custom would concatenate twice).
        string prompt = bridge.ResolvePrompt(input.Prompt);
        if (string.IsNullOrWhiteSpace(prompt) || prompt.EnumerateRunes().Count() > 4000)
        {
            return Invalid("prompt must contain 1-4000 characters after defaults are resolved.");
        }

        if (!string.IsNullOrWhiteSpace(input.Prompt))
        {
            bridge.ReflectMcpPrompt(input.Prompt);
        }
        AiGenerationProfileResolution profileResolution = bridge.ResolveProfile(input.ProfileId);
        if (!profileResolution.Success || profileResolution.Profile == null)
        {
            return Failure(AgasMcpErrorCodes.AiNoProfile, "No usable AI profile is configured.", "Select and configure an AI profile in AGAS settings.");
        }

        AiGenerationQuality? quality = input.Quality switch
        {
            null => null,
            "low" => AiGenerationQuality.Low,
            "medium" => AiGenerationQuality.Medium,
            _ => throw new InvalidOperationException("Validated AI quality was not handled."),
        };
        if (quality.HasValue && !AiGenerationQualityPolicy.Supports(profileResolution.Profile.ModelName))
        {
            return Invalid("quality is supported only by grok-imagine-image-2.0.");
        }
        bool modelIsTextOnly = AiGenerationQualityPolicy.Supports(profileResolution.Profile.ModelName);
        bool declaredWithoutRegionEdit = profileResolution.Profile.DeclaredCapabilities
            is AiGenerationAdapterCapabilities capabilities
            && !capabilities.HasFlag(AiGenerationAdapterCapabilities.RegionEdit);
        if (context.RegionMetadata.ReplacementRegion.HasValue
            && (modelIsTextOnly || declaredWithoutRegionEdit))
        {
            return Failure(
                AgasMcpErrorCodes.AiUnsupportedOperation,
                "The selected image model does not support W region editing.",
                "Clear W to generate the complete Q region, or select a model with regionEdit capability.");
        }

        AiGenerationPreparationResult prepared = bridge.Prepare(new AiGenerationPreparationInput(
            profileResolution.Profile,
            context,
            target,
            prompt,
            quality));
        if (!prepared.Success || prepared.Request == null || prepared.PatchCapture == null)
        {
            return Failure(AgasMcpErrorCodes.SelectionInvalid, "The current Q/W canvas input could not be prepared.", "Check the current Q/W regions and selected image, then retry.");
        }

        AiGenerationRequest request = prepared.Request;
        CanvasPatchCapture capture = prepared.PatchCapture;
        AiGenerationProviderProfile profile = profileResolution.Profile;
        AgasMcpAiExecutionIdentity execution = AgasMcpAiSafeProjector.ProjectExecutionIdentity(
            profile.Id,
            profile.ProviderId,
            profile.AdapterKind,
            profile.ModelName,
            new[] { profile.ApiKey });
        AgasMcpAiProjectScope projectScope = AgasMcpAiProjectScope.Create(project.ProjectRoot, project.ProjectId);
        var submission = new AgasMcpAiJobSubmission(
            projectScope,
            ResolveEffectiveOperation(request.Operation, profile.AdapterKind, profile.DeclaredCapabilities, profile.ModelName),
            execution,
            cancellationToken => RunGenerationAsync(
                context,
                target,
                profile,
                request,
                capture,
                cancellationToken));
        AgasMcpAiJobSubmitResult submitted = _scheduler.Submit(submission);
        if (!submitted.Accepted || submitted.Job == null)
        {
            return submitted.RejectReason == AgasMcpAiJobSubmitRejectReason.Busy
                ? Failure(AgasMcpErrorCodes.AiBusy, "The MCP AI generation queue is full.", "Wait for an existing AI job to finish, then retry.")
                : Failure(AgasMcpErrorCodes.Stopped, "AI generation is stopped.", "Enable or restart the AGAS MCP runtime before retrying.");
        }

        ReportJobStatus(bridge, submitted.Job);

        return Success(new AgasMcpAiGenerateDataDto(
            submitted.Job.JobId,
            submitted.Job.State.ToString(),
            submitted.Job.EffectiveOperation,
            ExecutionData(submitted.Job.Execution),
            submitted.Job.CreatedAtUtc));
    }

    public AgasMcpToolResponse Status(AgasAiStatusInput input)
    {
        if (!TryValidateInput(input, out input, out AgasMcpToolResponse? validationError)) return validationError!;
        if (!TryGetCurrentProjectScope(out AgasMcpProjectContext? project, out AgasMcpAiProjectScope projectScope, out AgasMcpToolResponse? scopeError))
        {
            return scopeError!;
        }
        if (!string.IsNullOrWhiteSpace(input.JobId))
        {
            if (!_store.TryGet(projectScope, input.JobId, out AgasMcpAiJobSnapshot? job) || job == null)
            {
                return JobNotFound();
            }
            ReportJobStatus(project!.AiGenerationWorkspace, job);
            return Success(JobData(job));
        }
        IReadOnlyList<AgasMcpAiJobSnapshot> jobs = _store.GetRecent(projectScope, input.Limit);
        if (jobs.Count > 0)
        {
            ReportJobStatus(project!.AiGenerationWorkspace, jobs[0]);
        }
        return Success(new AgasMcpAiStatusListDataDto(jobs.Select(JobData).ToArray()));
    }

    public AgasMcpToolResponse Cancel(AgasAiCancelInput input)
    {
        if (!TryValidateInput(input, out input, out AgasMcpToolResponse? validationError)) return validationError!;
        if (!TryGetCurrentProjectScope(out AgasMcpProjectContext? project, out AgasMcpAiProjectScope projectScope, out AgasMcpToolResponse? scopeError))
        {
            return scopeError!;
        }
        AgasMcpAiJobCancelResult result = _scheduler.Cancel(projectScope, input.JobId);
        if (!result.Found || result.Job == null)
        {
            return JobNotFound();
        }
        ReportJobStatus(project!.AiGenerationWorkspace, result.Job);
        return Success(new AgasMcpAiCancelDataDto(
            result.Job.JobId,
            result.Job.State.ToString(),
            result.Canceled));
    }

    public AgasMcpToolResponse Apply(AgasAiApplyInput input)
    {
        if (!TryValidateInput(input, out input, out AgasMcpToolResponse? validationError)) return validationError!;
        if (!TryGetCurrentProjectScope(out AgasMcpProjectContext? project, out AgasMcpAiProjectScope projectScope, out AgasMcpToolResponse? scopeError))
        {
            return scopeError!;
        }
        if (!_store.TryGet(projectScope, input.JobId, out AgasMcpAiJobSnapshot? job) || job == null)
        {
            return JobNotFound();
        }
        if (job.State != AgasMcpAiJobState.Succeeded)
        {
            return Failure(AgasMcpErrorCodes.AiJobNotSucceeded, "The AI job has not succeeded.", "Wait for a Succeeded status before applying the job.");
        }
        if (job.Applied != null)
        {
            ReportJobStatus(project!.AiGenerationWorkspace, job);
            return AppliedSuccess(job, alreadyApplied: true);
        }
        if (!_store.TryBeginApply<AgasMcpAiApplicationState>(projectScope, input.JobId, out AgasMcpAiJobApplyLease<AgasMcpAiApplicationState>? lease)
            || lease == null)
        {
            // A concurrent caller may have completed apply between the first
            // snapshot and the application-state lookup.
            if (_store.TryGet(projectScope, input.JobId, out AgasMcpAiJobSnapshot? latest)
                && latest?.Applied != null)
            {
                ReportJobStatus(project!.AiGenerationWorkspace, latest);
                return AppliedSuccess(latest, alreadyApplied: true);
            }
            return Failure(AgasMcpErrorCodes.AiJobNotSucceeded, "The AI job result is no longer available for apply.", "Generate a new AI job and apply it after success.");
        }

        using (lease)
        {
            AgasMcpAiApplicationState state = lease.ApplicationState;
            if (_store.TryGet(projectScope, input.JobId, out AgasMcpAiJobSnapshot? latest)
                && latest?.Applied != null)
            {
                ReportJobStatus(project!.AiGenerationWorkspace, latest);
                return AppliedSuccess(latest, alreadyApplied: true);
            }

            if (project!.RequiresReadOnly)
            {
                return Failure(AgasMcpErrorCodes.ProjectReadOnly, "The current AGAS project is read-only.", "Open a writable project before applying the job.");
            }
            if (project.AiGenerationWorkspace == null)
            {
                return Failure(AgasMcpErrorCodes.AiContextStale, "The AI job belongs to another or closed project.", "Generate a new job in the current project.");
            }
            if (!_dirtyState.TryBindProject(project.ProjectRoot, project.ProjectId, out string bindError))
            {
                return Failure(AgasMcpErrorCodes.InvalidOperation, bindError, "Save the current MCP dirty project before applying another project job.");
            }

            AiGeneratedImageApplyResult applied = project.AiGenerationWorkspace.Apply(
                state.Application,
                AiGenerationApplyPersistence.MarkDirtyOnly,
                AiGenerationApplyContextPolicy.RejectStaleContent);
            if (!applied.Success || !applied.Applied)
            {
                return applied.FailureReason == AiGeneratedImageApplyFailureReason.ContextStale
                    ? Failure(AgasMcpErrorCodes.AiContextStale, "The canvas changed after this AI job was submitted.", "Generate a new job from the current canvas state.")
                    : Failure(AgasMcpErrorCodes.InvalidOperation, "The generated image could not be applied to its frozen target.", "Check the active layer and generate a new job.");
            }

            _dirtyState.MarkSceneDirty(state.Application.ExpectedSceneId);
            AgasMcpDirtySnapshot dirty = _dirtyState.Snapshot();
            project.NotifyDirtyState(dirty);
            project.NotifyStatus("MCP AI result applied; project save is required.");
            if (!lease.MarkApplied(
                    applied.AppliedRelativePath ?? state.Application.Result.RelativePath,
                    applied.Message,
                    out AgasMcpAiJobSnapshot? appliedJob)
                || appliedJob?.Applied == null)
            {
                return Failure(AgasMcpErrorCodes.InternalError, "The applied AI result could not be recorded.", "Check AGAS logs before retrying.");
            }
            try
            {
                project.AiGenerationWorkspace.Discard(state.Application);
            }
            catch
            {
                // Candidate cleanup is best effort after the immutable result was applied.
            }

            ReportJobStatus(project.AiGenerationWorkspace, appliedJob);
            return AppliedSuccess(appliedJob, alreadyApplied: false);
        }
    }

    public static AgasMcpAiJobFailure MapRunnerFailure(Exception exception) =>
        MapRunnerFailure(exception, Array.Empty<string>());

    private static AgasMcpAiJobFailure MapRunnerFailure(
        Exception exception,
        IReadOnlyList<string> credentials)
    {
        if (exception is AgasMcpAiJobExecutionException known)
        {
            return known.Failure;
        }
        if (exception is AiGenerationException ai)
        {
            AgasMcpAiJobDiagnostic diagnostic = ProjectDiagnostic(ai.Diagnostic, credentials);
            return ai.Code switch
            {
                AiGenerationErrorCode.UnsupportedOperation => new AgasMcpAiJobFailure(
                    AgasMcpErrorCodes.AiUnsupportedOperation,
                    "The configured provider does not support the prepared operation.",
                    Diagnostic: diagnostic),
                AiGenerationErrorCode.RequestCanceled => new AgasMcpAiJobFailure(
                    AgasMcpErrorCodes.AiCanceled,
                    "The AI generation request was canceled.",
                    Diagnostic: diagnostic),
                AiGenerationErrorCode.InvalidConfiguration or AiGenerationErrorCode.AdapterNotFound => new AgasMcpAiJobFailure(
                    AgasMcpErrorCodes.AiNoProfile,
                    "The configured AI profile is unavailable.",
                    Diagnostic: diagnostic),
                _ => new AgasMcpAiJobFailure(
                    AgasMcpErrorCodes.AiProviderFailed,
                    "The image provider request failed.",
                    Diagnostic: diagnostic),
            };
        }
        return new AgasMcpAiJobFailure(
            AgasMcpErrorCodes.AiProviderFailed,
            "The image provider request failed.",
            Diagnostic: ProjectDiagnostic(null, credentials));
    }

    private async Task<AgasMcpAiJobRunResult> RunGenerationAsync(
        AiGenerationCanvasContext context,
        AiGenerationTarget target,
        AiGenerationProviderProfile profile,
        AiGenerationRequest request,
        CanvasPatchCapture capture,
        CancellationToken cancellationToken)
    {
        AiGenerationResult result;
        try
        {
            result = await _runner.GenerateAsync(profile, request, cancellationToken).ConfigureAwait(false);
        }
        catch (AiGenerationException exception)
        {
            throw new AgasMcpAiJobExecutionException(MapRunnerFailure(exception, new[] { profile.ApiKey }));
        }
        if (!AgasGeneratedImagePathPolicy.TryResolveGeneratedFile(
                context.ProjectRoot,
                result.RelativePath,
                requireFile: true,
                out _))
        {
            throw new AgasMcpAiJobExecutionException(new AgasMcpAiJobFailure(
                AgasMcpErrorCodes.AiProviderFailed,
                "The image provider returned an unsafe result path."));
        }

        var application = new AiGeneratedImageApplication(
            result,
            target,
            context.ProjectRoot,
            context.ProjectId,
            context.SceneId,
            context.LayerId,
            target == AiGenerationTarget.ReplaceSelected ? context.SelectedImageId : null,
            capture,
            request.Operation,
            context.LayerScope,
            request.SceneName,
            request.LayerName);
        var applicationState = new AgasMcpAiApplicationState(application);
        return new AgasMcpAiJobRunResult(
            new AgasMcpAiJobResultMetadata(
                result.RelativePath.Replace('\\', '/'),
                null,
                "AI generation completed."),
            applicationState,
            () =>
            {
                CanvasPatchAssetStore.DeleteGeneratedCandidate(context.ProjectRoot, result.RelativePath);
                return ValueTask.CompletedTask;
            },
            result.EffectiveOperation.ToString());
    }

    internal static string ResolveEffectiveOperation(
        AiGenerationOperation operation,
        string adapterKind,
        AiGenerationAdapterCapabilities? capabilities,
        string modelName = "") => operation switch
    {
        AiGenerationOperation.TextToImage => AiGenerationEffectiveOperation.TextToImage.ToString(),
        AiGenerationOperation.ReferenceEdit => AiGenerationEffectiveOperation.ReferenceEdit.ToString(),
        AiGenerationOperation.RegionEdit when adapterKind == "newapi-compatible" =>
            (NewApiImageAdapter.SupportsNativeMask(modelName)
                ? AiGenerationEffectiveOperation.MaskedEdit
                : AiGenerationEffectiveOperation.GuidedRegionEdit).ToString(),
        AiGenerationOperation.RegionEdit when capabilities.HasValue
            && capabilities.Value.HasFlag(AiGenerationAdapterCapabilities.NativeAlphaMask) =>
            AiGenerationEffectiveOperation.MaskedEdit.ToString(),
        AiGenerationOperation.RegionEdit when string.Equals(
            adapterKind,
            AgasAiProviderIds.OpenAiCompatible,
            StringComparison.Ordinal) => AiGenerationEffectiveOperation.MaskedEdit.ToString(),
        AiGenerationOperation.RegionEdit => AiGenerationEffectiveOperation.GuidedRegionEdit.ToString(),
        _ => throw new InvalidOperationException("Unsupported AI generation operation."),
    };

    public static AgasMcpAiJobDto JobData(AgasMcpAiJobSnapshot job)
    {
        ArgumentNullException.ThrowIfNull(job);
        AgasMcpAiExecutionIdentityDto execution = ExecutionData(job.Execution);
        return job.State switch
        {
            AgasMcpAiJobState.Queued => new AgasMcpAiJobQueuedDto(
                job.JobId, "Queued", job.EffectiveOperation, execution, job.CreatedAtUtc, false),
            AgasMcpAiJobState.Running => new AgasMcpAiJobRunningDto(
                job.JobId, "Running", job.EffectiveOperation, execution, job.CreatedAtUtc, false,
                Required(job.StartedAtUtc, nameof(job.StartedAtUtc))),
            AgasMcpAiJobState.Succeeded => new AgasMcpAiJobSucceededDto(
                job.JobId, "Succeeded", job.EffectiveOperation, execution, job.CreatedAtUtc,
                job.Applied != null,
                Required(job.StartedAtUtc, nameof(job.StartedAtUtc)),
                Required(job.CompletedAtUtc, nameof(job.CompletedAtUtc)),
                job.Result?.ResultRelativePath ?? throw new InvalidOperationException("Succeeded AI job result is unavailable."),
                job.Result.Message,
                job.Result.PreviewRelativePath,
                job.Applied?.AppliedRelativePath,
                job.Applied?.AppliedAtUtc),
            AgasMcpAiJobState.Failed => new AgasMcpAiJobFailedDto(
                job.JobId, "Failed", job.EffectiveOperation, execution, job.CreatedAtUtc, false,
                Required(job.CompletedAtUtc, nameof(job.CompletedAtUtc)),
                job.Failure?.ErrorCode ?? AgasMcpErrorCodes.AiProviderFailed,
                job.Failure?.Message ?? "The image provider request failed.",
                DiagnosticData(job.Failure?.Diagnostic),
                job.StartedAtUtc,
                job.Failure?.Hint),
            AgasMcpAiJobState.Canceled => new AgasMcpAiJobCanceledDto(
                job.JobId, "Canceled", job.EffectiveOperation, execution, job.CreatedAtUtc, false,
                Required(job.CompletedAtUtc, nameof(job.CompletedAtUtc)),
                job.StartedAtUtc),
            _ => throw new InvalidOperationException("Unknown AI job state."),
        };
    }

    private static DateTimeOffset Required(DateTimeOffset? value, string name) =>
        value ?? throw new InvalidOperationException($"AI job field '{name}' is unavailable.");

    private static AgasMcpAiExecutionIdentityDto ExecutionData(AgasMcpAiExecutionIdentity execution) => new(
        execution.ProfileId,
        execution.ProviderId,
        execution.AdapterKind,
        execution.ModelName);

    private static AgasMcpAiDiagnosticDto DiagnosticData(AgasMcpAiJobDiagnostic? diagnostic)
    {
        AgasMcpAiJobDiagnostic safe = diagnostic ?? ProjectDiagnostic(null, Array.Empty<string>());
        return new AgasMcpAiDiagnosticDto(
            safe.DiagnosticId,
            safe.ErrorCode,
            safe.Phase,
            safe.Category,
            safe.HttpStatus,
            safe.UpstreamCode);
    }

    private static AgasMcpAiJobDiagnostic ProjectDiagnostic(
        AiGenerationSafeDiagnostic? diagnostic,
        IReadOnlyList<string> credentials)
    {
        if (diagnostic == null)
        {
            return new AgasMcpAiJobDiagnostic(
                "unavailable",
                AiGenerationErrorCode.ProviderRequestFailed.ToString(),
                AiGenerationFailurePhase.PanelRunner.ToString(),
                AiGenerationFailureCategory.Unexpected.ToString());
        }

        string diagnosticId = SafeDiagnosticToken(diagnostic.DiagnosticId, "unavailable", credentials)!;
        string? upstreamCode = string.IsNullOrWhiteSpace(diagnostic.UpstreamCode)
            ? null
            : SafeDiagnosticToken(diagnostic.UpstreamCode, null, credentials);
        return new AgasMcpAiJobDiagnostic(
            diagnosticId,
            diagnostic.ErrorCode.ToString(),
            diagnostic.Phase.ToString(),
            diagnostic.Category.ToString(),
            diagnostic.HttpStatus,
            upstreamCode);
    }

    private static string? SafeDiagnosticToken(
        string? value,
        string? fallback,
        IReadOnlyList<string> credentials)
    {
        string candidate = value?.Trim() ?? string.Empty;
        if (candidate.Length is < 1 or > 120
            || AgasMcpAiSafeProjector.ContainsCredential(candidate, credentials)
            || candidate.Any(character => !(char.IsAsciiLetterOrDigit(character)
                || character is '-' or '_' or '.' or ':')))
        {
            return fallback;
        }
        return candidate;
    }

    private static void ReportJobStatus(
        IAgasAiGenerationWorkspaceBridge? bridge,
        AgasMcpAiJobSnapshot job)
    {
        if (bridge == null)
        {
            return;
        }

        bridge.ReportExternalJobStatus(CreateExternalJobStatus(job));
    }

    internal static AiGenerationExternalJobStatus CreateExternalJobStatus(AgasMcpAiJobSnapshot job)
    {
        ArgumentNullException.ThrowIfNull(job);
        AiGenerationExternalJobState state = job.Applied != null
            ? AiGenerationExternalJobState.Applied
            : job.State switch
            {
                AgasMcpAiJobState.Queued => AiGenerationExternalJobState.Queued,
                AgasMcpAiJobState.Running => AiGenerationExternalJobState.Running,
                AgasMcpAiJobState.Succeeded => AiGenerationExternalJobState.Succeeded,
                AgasMcpAiJobState.Failed => AiGenerationExternalJobState.Failed,
                AgasMcpAiJobState.Canceled => AiGenerationExternalJobState.Canceled,
                _ => AiGenerationExternalJobState.Failed,
            };
        return new AiGenerationExternalJobStatus(
            job.JobId,
            state,
            job.Failure?.Message ?? string.Empty);
    }

    private AgasMcpToolResponse AppliedSuccess(AgasMcpAiJobSnapshot job, bool alreadyApplied) =>
        Success(new AgasMcpAiApplyDataDto(
            job.JobId,
            "Succeeded",
            alreadyApplied,
            job.Applied?.AppliedRelativePath
                ?? throw new InvalidOperationException("Applied AI job path is unavailable."),
            job.Applied.Message));

    private AgasMcpToolResponse BindAndExecute<T>(
        IReadOnlyDictionary<string, JsonElement>? arguments,
        Func<T, AgasMcpToolResponse> execute)
        where T : class
    {
        AgasMcpContractBindResult<T> bound = AgasMcpContractRegistry.Bind<T>(arguments);
        return bound.Success && bound.Value != null
            ? execute(bound.Value)
            : Invalid(InvalidToolArgumentsMessage);
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
            error = Invalid(InvalidToolArgumentsMessage);
            return false;
        }

        validated = result.Value;
        error = null;
        return true;
    }

    private bool TryGetCurrentProjectScope(
        out AgasMcpProjectContext? project,
        out AgasMcpAiProjectScope projectScope,
        out AgasMcpToolResponse? error)
    {
        project = _contextProvider.GetCurrent();
        if (project == null)
        {
            projectScope = default;
            error = NoOpenProject();
            return false;
        }

        try
        {
            projectScope = AgasMcpAiProjectScope.Create(project.ProjectRoot, project.ProjectId);
            error = null;
            return true;
        }
        catch (ArgumentException)
        {
            projectScope = default;
            error = Invalid("Current project identity is unavailable.");
            return false;
        }
    }

    private AgasMcpToolResponse Success(object data) =>
        AgasMcpToolResponse.Success(!_dirtyState.IsDirty, _dirtyState.IsDirty, data);

    private AgasMcpToolResponse NoOpenProject() =>
        Failure(AgasMcpErrorCodes.NoOpenProject, "No AGAS project is currently open.", "Open a project in AGAS before calling this tool.");

    private AgasMcpToolResponse JobNotFound() =>
        Failure(AgasMcpErrorCodes.AiJobNotFound, "The AI job was not found or has expired.", "Use agas.ai.status without jobId to list recent jobs.");

    private AgasMcpToolResponse Invalid(string message) =>
        Failure(AgasMcpErrorCodes.InvalidOperation, message, "Check the documented AI tool input schema and current canvas state.");

    private AgasMcpToolResponse Failure(string code, string message, string suggestion) =>
        AgasMcpToolResponse.Failure(!_dirtyState.IsDirty, _dirtyState.IsDirty, code, message, suggestion);
}

public sealed class AgasMcpAiApplicationState
{
    public AgasMcpAiApplicationState(AiGeneratedImageApplication application)
    {
        Application = application ?? throw new ArgumentNullException(nameof(application));
    }

    public AiGeneratedImageApplication Application { get; }
}
