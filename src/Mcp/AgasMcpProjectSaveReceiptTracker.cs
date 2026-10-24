#nullable enable

using System;
using System.Collections.Immutable;
using System.Threading;
using AppAgasCs.Mcp.Contracts;
using AppAgasCs.Mcp.Contracts.Output;

namespace AppAgasCs.Mcp;

public sealed class AgasMcpProjectSaveReceiptTracker
{
    private AgasMcpProjectSaveDataDto _current = new(
        "none",
        ImmutableArray<string>.Empty,
        ImmutableArray<AgasMcpSaveFailureDto>.Empty,
        ImmutableArray<string>.Empty);

    public AgasMcpProjectSaveDataDto Current => Volatile.Read(ref _current);

    public void Reset(AgasMcpProjectSaveDataDto initial) => Publish(initial);

    public void Publish(AgasMcpProjectSaveDataDto receipt)
    {
        ArgumentNullException.ThrowIfNull(receipt);
        Interlocked.Exchange(ref _current, receipt);
    }
}
