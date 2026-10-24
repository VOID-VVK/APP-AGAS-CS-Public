using System;
using AppAgasCs.Core.Persistence;
using AppAgasCs.Mcp.Contracts;

namespace AppAgasCs.Mcp;

public sealed class AgasMcpRepositoryErrorMapper
{
    public AgasMcpSaveFailureDto ToFailure(string logicalPath, AgasPersistenceResult result)
    {
        string message = string.IsNullOrWhiteSpace(result.ErrorMessage)
            ? "Resource could not be saved."
            : Sanitize(result.ErrorMessage);
        return new AgasMcpSaveFailureDto(logicalPath, AgasMcpErrorCodes.SaveFailed, message);
    }

    public string Sanitize(string message)
    {
        string sanitized = message.Replace('\\', '/');
        sanitized = System.Text.RegularExpressions.Regex.Replace(sanitized, @"/[^\s:]+", "<path>");
        sanitized = sanitized.Replace(".tmp.tres", ".tres", StringComparison.OrdinalIgnoreCase);
        return sanitized.Length <= 500 ? sanitized : sanitized[..500];
    }
}
