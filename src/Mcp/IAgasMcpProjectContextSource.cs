namespace AppAgasCs.Mcp;

public readonly record struct AgasMcpProjectAccessState(
    bool HasOpenProject,
    bool RequiresReadOnly,
    string ReadOnlyReasonCode)
{
    public static AgasMcpProjectAccessState None { get; } = new(false, false, string.Empty);
}

public interface IAgasMcpProjectContextSource
{
    AgasMcpProjectContext? GetCurrentMcpProjectContext();
    AgasMcpProjectAccessState GetCurrentMcpProjectAccessState();
}
