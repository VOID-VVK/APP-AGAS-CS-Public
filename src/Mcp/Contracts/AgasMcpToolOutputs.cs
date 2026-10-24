using System;
using System.Collections.Generic;
using System.Linq;
namespace AppAgasCs.Mcp.Contracts;

public sealed record AgasMcpToolError(string Code, string Message, string Suggestion);

public sealed record AgasMcpToolResponse
{
    public AgasMcpToolResponse(
        bool ok,
        bool saved,
        bool dirty,
        object? data,
        IReadOnlyList<string> warnings,
        AgasMcpToolError? error = null)
    {
        ArgumentNullException.ThrowIfNull(warnings);
        if (warnings.Any(warning => warning is null))
        {
            throw new ArgumentException("MCP warnings cannot contain null items.", nameof(warnings));
        }

        Ok = ok;
        Saved = saved;
        Dirty = dirty;
        Data = data;
        Warnings = Array.AsReadOnly(warnings.ToArray());
        Error = error;
    }

    public bool Ok { get; }
    public bool Saved { get; }
    public bool Dirty { get; }
    public object? Data { get; }
    public IReadOnlyList<string> Warnings { get; }
    public AgasMcpToolError? Error { get; }

    public static AgasMcpToolResponse Success(bool saved, bool dirty, object data, IReadOnlyList<string>? warnings = null) =>
        new(true, saved, dirty, data, warnings ?? Array.Empty<string>());

    public static AgasMcpToolResponse Failure(bool saved, bool dirty, string code, string message, string suggestion, IReadOnlyList<string>? warnings = null) =>
        new(false, saved, dirty, null, warnings ?? Array.Empty<string>(), new AgasMcpToolError(code, message, suggestion));
}
