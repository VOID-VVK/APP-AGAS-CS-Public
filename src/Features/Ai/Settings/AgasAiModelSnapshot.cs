using System;
using System.Collections.Generic;
using System.Linq;

namespace AppAgasCs.Features.Ai.Settings;

/// <summary>One immutable, credential-free view shared by menus, MCP and execution.</summary>
public sealed record AgasAiModelSnapshot
{
    public AgasAiModelSnapshot(IEnumerable<AgasAiUnifiedModel> models, string source, string message, string connectionId, bool isReady)
    {
        Models = Array.AsReadOnly(models.ToArray());
        Source = source;
        Message = message;
        ConnectionId = connectionId;
        IsReady = isReady;
    }

    public IReadOnlyList<AgasAiUnifiedModel> Models { get; }
    public string Source { get; }
    public string Message { get; }
    public string ConnectionId { get; }
    public bool IsReady { get; }

    public bool TryGet(string? modelName, out AgasAiUnifiedModel model)
    {
        model = IsReady ? Models.FirstOrDefault(item => item.IsAvailable
            && string.Equals(item.ModelName, modelName?.Trim(), StringComparison.OrdinalIgnoreCase))! : null!;
        return model != null;
    }
}
