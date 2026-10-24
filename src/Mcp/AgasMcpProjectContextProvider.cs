using System;
namespace AppAgasCs.Mcp;

public sealed class AgasMcpProjectContextProvider
{
    private readonly object _gate = new();
    private IAgasMcpProjectContextSource? _source;
    private AgasMcpProjectContext? _context;

    public void RegisterSource(IAgasMcpProjectContextSource source)
    {
        ArgumentNullException.ThrowIfNull(source);
        lock (_gate)
        {
            _source = source;
            _context = null;
        }
    }

    public void RegisterContext(AgasMcpProjectContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        lock (_gate)
        {
            _context = context;
            _source = null;
        }
    }

    public void Clear()
    {
        lock (_gate)
        {
            _context = null;
            _source = null;
        }
    }

    public AgasMcpProjectContext? GetCurrent()
    {
        lock (_gate)
        {
            return _source?.GetCurrentMcpProjectContext() ?? _context;
        }
    }

    public AgasMcpProjectAccessState GetCurrentAccessState()
    {
        lock (_gate)
        {
            if (_source != null)
            {
                AgasMcpProjectAccessState sourceState = _source.GetCurrentMcpProjectAccessState();
                AgasMcpProjectAccessState contextState = _source.GetCurrentMcpProjectContext()?.AccessState
                    ?? AgasMcpProjectAccessState.None;
                return MergeAccessStates(sourceState, contextState);
            }

            return _context?.AccessState ?? AgasMcpProjectAccessState.None;
        }
    }

    public static AgasMcpProjectAccessState MergeAccessStates(
        AgasMcpProjectAccessState sourceState,
        AgasMcpProjectAccessState contextState)
    {
        bool requiresReadOnly = sourceState.RequiresReadOnly || contextState.RequiresReadOnly;
        string reason = string.Empty;
        if (requiresReadOnly)
        {
            reason = contextState.RequiresReadOnly && !string.IsNullOrWhiteSpace(contextState.ReadOnlyReasonCode)
                ? contextState.ReadOnlyReasonCode
                : sourceState.RequiresReadOnly && !string.IsNullOrWhiteSpace(sourceState.ReadOnlyReasonCode)
                    ? sourceState.ReadOnlyReasonCode
                    : !string.IsNullOrWhiteSpace(contextState.ReadOnlyReasonCode)
                        ? contextState.ReadOnlyReasonCode
                        : sourceState.ReadOnlyReasonCode ?? string.Empty;
        }

        return new AgasMcpProjectAccessState(
            sourceState.HasOpenProject || contextState.HasOpenProject,
            requiresReadOnly,
            reason);
    }
}
