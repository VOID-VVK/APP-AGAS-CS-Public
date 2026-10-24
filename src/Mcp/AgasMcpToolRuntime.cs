#nullable enable

using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using AppAgasCs.Mcp.Contracts;
using AppAgasCs.Mcp.Contracts.Runtime;
using AppAgasCs.Mcp.Contracts.Output;
using AppAgasCs.Mcp.Runtime;

namespace AppAgasCs.Mcp;

public sealed class AgasMcpToolRuntime
{
    private readonly IAgasMcpToolExecutor _serialExecutor;
    private readonly AgasMcpMainThreadDispatcher? _dispatcher;
    private readonly IReadOnlyDictionary<string, AgasMcpPerToolHandler> _handlers;

    public AgasMcpToolRuntime(
        IAgasMcpToolExecutor serialExecutor,
        AgasMcpMainThreadDispatcher dispatcher,
        IReadOnlyDictionary<string, AgasMcpPerToolHandler> handlers)
    {
        _serialExecutor = serialExecutor ?? throw new ArgumentNullException(nameof(serialExecutor));
        _dispatcher = dispatcher;
        _handlers = handlers ?? throw new ArgumentNullException(nameof(handlers));
        ValidateHandlerParity(_handlers);
    }

    // Compatibility constructor retained for existing unit fixtures. Production composition must
    // use the explicit per-tool registry constructor above.
    public AgasMcpToolRuntime(
        IAgasMcpToolExecutor serialExecutor,
        AgasMcpMainThreadDispatcher dispatcher,
        AgasMcpProjectFacade? facade,
        AgasMcpAiFacade? aiFacade = null)
        : this(serialExecutor, dispatcher, CreateLegacyCompatibilityHandlers(facade, aiFacade))
    {
    }

    public async Task<AgasMcpToolResponse> ExecuteToolAsync(
        string? sessionId,
        string toolName,
        IReadOnlyDictionary<string, JsonElement>? arguments,
        CancellationToken cancellationToken)
    {
        AgasMcpToolDescriptor descriptor;
        try
        {
            descriptor = AgasMcpToolDescriptorRegistry.Get(toolName);
        }
        catch (KeyNotFoundException)
        {
            return InvalidTool();
        }

        AgasMcpToolExecutionContext? context = null;
        try
        {
            AgasMcpToolResponse response = await _serialExecutor.ExecuteAsync(
                sessionId,
                descriptor.Timeout,
                async token =>
                {
                    var execution = new AgasMcpCommitExecution(descriptor.CommitPolicy);
                    context = new AgasMcpToolExecutionContext(
                        descriptor.Name,
                        execution,
                        (callback, dispatchToken) => RequireDispatcher().InvokeAsync(callback, dispatchToken),
                        (callback, dispatchToken) => RequireDispatcher().InvokeAsync(callback, dispatchToken));
                    using CancellationTokenRegistration registration = token.UnsafeRegister(
                        static state => ((AgasMcpCommitExecution)state!).TryCancel(),
                        execution);
                    AgasMcpToolResponse handled = await DispatchAsync(
                        descriptor,
                        arguments,
                        context,
                        token).ConfigureAwait(false);
                    if (execution.State == AgasMcpCommitExecutionState.Canceled)
                        throw new OperationCanceledException(
                            "MCP operation was canceled before its commit point.", token);
                    if (descriptor.CommitPolicy == AgasMcpCommitPolicy.None
                        && !execution.TryCompleteWithoutCommit())
                    {
                        throw new OperationCanceledException(
                            "MCP operation was canceled before completion.", token);
                    }
                    return handled;
                },
                cancellationToken).ConfigureAwait(false);
            return ValidateOrFailClosed(descriptor, response, context);
        }
        catch (AgasMcpToolExecutionRejectedException exception)
        {
            return ValidateOrFailClosed(descriptor, Failure(exception.Reason), context);
        }
        catch (TimeoutException)
        {
            return ValidateOrFailClosed(descriptor, Failure(AgasMcpToolExecutionRejectReason.Timeout), context);
        }
        catch (OperationCanceledException)
        {
            return ValidateOrFailClosed(descriptor, Failure(AgasMcpToolExecutionRejectReason.Stopped), context);
        }
        catch
        {
            return FailClosed(descriptor, context);
        }
    }

    private async Task<AgasMcpToolResponse> DispatchAsync(
        AgasMcpToolDescriptor descriptor,
        IReadOnlyDictionary<string, JsonElement>? arguments,
        AgasMcpToolExecutionContext context,
        CancellationToken cancellationToken)
    {
        AgasMcpPerToolHandler handler = _handlers[descriptor.Name];
        switch (descriptor.DispatchMode)
        {
            case AgasMcpToolDispatchMode.MainThread:
                return await RequireDispatcher().InvokeAsync(_ =>
                {
                    ValueTask<AgasMcpToolResponse> pending = handler(arguments, context, cancellationToken);
                    if (!pending.IsCompletedSuccessfully)
                        throw new InvalidOperationException("A MainThread MCP handler must complete synchronously.");
                    return pending.Result;
                }, cancellationToken).ConfigureAwait(false);

            case AgasMcpToolDispatchMode.AsyncMainThread:
                Task<AgasMcpToolResponse> operation = await RequireDispatcher().InvokeAsync(
                    _ => handler(arguments, context, cancellationToken).AsTask(),
                    cancellationToken).ConfigureAwait(false);
                return await operation.ConfigureAwait(false);

            case AgasMcpToolDispatchMode.AsyncOrchestrated:
                return await handler(arguments, context, cancellationToken).ConfigureAwait(false);

            default:
                throw new InvalidOperationException($"Unknown MCP dispatch mode '{descriptor.DispatchMode}'.");
        }
    }

    private AgasMcpMainThreadDispatcher RequireDispatcher() =>
        _dispatcher ?? throw new InvalidOperationException("AGAS MCP main-thread dispatcher is unavailable.");

    private static AgasMcpToolResponse ValidateOrFailClosed(
        AgasMcpToolDescriptor descriptor,
        AgasMcpToolResponse response,
        AgasMcpToolExecutionContext? context)
    {
        AgasMcpOutputValidationResult validation = AgasMcpOutputContractRegistry.Validate(
            descriptor.OutputContract!, response);
        return validation.Success ? response : FailClosed(descriptor, context);
    }

    private static AgasMcpToolResponse FailClosed(
        AgasMcpToolDescriptor descriptor,
        AgasMcpToolExecutionContext? context)
    {
        var error = new AgasMcpToolError(
            AgasMcpErrorCodes.InternalError,
            "AGAS MCP output contract validation failed.",
            "Retry after restarting AGAS. If the problem persists, report the tool name and correlation id.");
        object? failureData = context?.CreateFailureData();
        bool allowed = failureData != null
            && descriptor.OutputContract!.FailureDataTypes.Contains(failureData.GetType());
        bool saved = allowed && failureData is AgasMcpProjectCloseDataDto close && close.Saved;
        bool dirty = allowed && failureData is AgasMcpProjectCloseDataDto closeReceipt
            && closeReceipt.ProjectStillOpen && !closeReceipt.Saved;
        return new AgasMcpToolResponse(
            false,
            saved,
            dirty,
            allowed ? failureData : null,
            Array.Empty<string>(),
            error);
    }

    private static void ValidateHandlerParity(IReadOnlyDictionary<string, AgasMcpPerToolHandler> handlers)
    {
        if (handlers.Count != AgasMcpToolNames.All.Count)
            throw new InvalidOperationException("AGAS MCP handler registry must contain exactly 30 handlers.");
        foreach (string name in AgasMcpToolNames.All)
        {
            if (!handlers.TryGetValue(name, out AgasMcpPerToolHandler? handler) || handler == null)
                throw new InvalidOperationException($"AGAS MCP handler '{name}' is missing.");
        }
        foreach (string name in handlers.Keys)
        {
            if (!AgasMcpToolNames.All.Contains(name, StringComparer.Ordinal))
                throw new InvalidOperationException($"Unknown AGAS MCP handler '{name}'.");
        }
    }

    internal static IReadOnlyDictionary<string, AgasMcpPerToolHandler> CreateLegacyCompatibilityHandlers(
        AgasMcpProjectFacade? facade,
        AgasMcpAiFacade? aiFacade = null)
    {
        var registry = new AgasMcpPerToolHandlerRegistry(AgasMcpToolNames.All);
        foreach (string name in AgasMcpToolNames.All)
        {
            registry.Register(name, CreateLegacyCompatibilityHandler(name, facade, aiFacade));
        }
        return registry.FreezeAndValidateComplete();
    }

    private static AgasMcpPerToolHandler CreateLegacyCompatibilityHandler(
        string toolName,
        AgasMcpProjectFacade? facade,
        AgasMcpAiFacade? aiFacade)
    {
        if (IsLegacyProjectTool(toolName) && facade != null)
        {
            return (arguments, _, _) => new ValueTask<AgasMcpToolResponse>(
                facade.Execute(toolName, arguments));
        }

        if (IsLegacyAiTool(toolName) && aiFacade != null)
        {
            return (arguments, _, _) => new ValueTask<AgasMcpToolResponse>(
                aiFacade.Execute(toolName, arguments));
        }

        return (_, _, _) => new ValueTask<AgasMcpToolResponse>(
            AgasMcpToolResponse.Failure(
                false,
                false,
                AgasMcpErrorCodes.Stopped,
                "AGAS MCP runtime handler is unavailable.",
                "Restart AGAS and retry."));
    }

    private static bool IsLegacyProjectTool(string toolName) => toolName switch
    {
        AgasMcpToolNames.ProjectSnapshot
            or AgasMcpToolNames.AssetsList
            or AgasMcpToolNames.SceneMutate
            or AgasMcpToolNames.LayerMutate
            or AgasMcpToolNames.ImageMutate
            or AgasMcpToolNames.PromptMutate
            or AgasMcpToolNames.ViewSet
            or AgasMcpToolNames.ProjectSave
            or AgasMcpToolNames.SelectionSet
            or AgasMcpToolNames.SelectionGet => true,
        _ => false,
    };

    private static bool IsLegacyAiTool(string toolName) => toolName switch
    {
        AgasMcpToolNames.AiGenerate
            or AgasMcpToolNames.AiStatus
            or AgasMcpToolNames.AiCancel
            or AgasMcpToolNames.AiApply => true,
        _ => false,
    };

    private static AgasMcpToolResponse InvalidTool() => AgasMcpToolResponse.Failure(
        false,
        false,
        AgasMcpErrorCodes.InvalidOperation,
        "Unknown AGAS MCP tool.",
        "Refresh the MCP tool list and call a registered tool name.");

    private static AgasMcpToolResponse Failure(AgasMcpToolExecutionRejectReason reason) => reason switch
    {
        AgasMcpToolExecutionRejectReason.Busy => AgasMcpToolResponse.Failure(false, false, AgasMcpErrorCodes.Busy,
            "AGAS MCP tool executor is busy.", "Retry after queued MCP calls complete."),
        AgasMcpToolExecutionRejectReason.RateLimited => AgasMcpToolResponse.Failure(false, false, AgasMcpErrorCodes.RateLimited,
            "AGAS MCP per-session rate limit was exceeded.", "Slow down tool calls and retry later."),
        AgasMcpToolExecutionRejectReason.Timeout => AgasMcpToolResponse.Failure(false, false, AgasMcpErrorCodes.Timeout,
            "AGAS MCP tool execution timed out.", "Retry the tool call or split the operation."),
        _ => AgasMcpToolResponse.Failure(false, false, AgasMcpErrorCodes.Stopped,
            "AGAS MCP tool execution was stopped.", "Restart or re-enable the MCP runtime before retrying."),
    };
}
