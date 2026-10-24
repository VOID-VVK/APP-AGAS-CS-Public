namespace AppAgasCs.Mcp;

public enum AgasMcpRuntimeState
{
    Disabled,
    Starting,
    Running,
    Stopping,
    Stopped,
    Error
}

public sealed record AgasMcpRuntimeStatus(
    AgasMcpRuntimeState State,
    string Endpoint,
    string? LastError = null)
{
    public static AgasMcpRuntimeStatus Disabled { get; } =
        new(AgasMcpRuntimeState.Disabled, AgasMcpSettingsResource.FixedEndpoint);

    public static AgasMcpRuntimeStatus Stopped { get; } =
        new(AgasMcpRuntimeState.Stopped, AgasMcpSettingsResource.FixedEndpoint);
}
