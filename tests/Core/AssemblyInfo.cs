using Xunit;

[assembly: CollectionBehavior(DisableTestParallelization = true)]

namespace AppAgasCs.Tests.Core;

public static class AgasMcpTestCollections
{
    public const string FixedPort = "AGAS MCP fixed port tests";
}

[CollectionDefinition(AgasMcpTestCollections.FixedPort, DisableParallelization = true)]
public sealed class AgasMcpFixedPortCollection
{
}
