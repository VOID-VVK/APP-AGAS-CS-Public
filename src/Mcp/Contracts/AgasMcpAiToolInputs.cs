using System.ComponentModel.DataAnnotations;
using AppAgasCs.Mcp.Contracts.Metadata;

namespace AppAgasCs.Mcp.Contracts;

[McpClosedObject]
public sealed record AgasAiProfilesListInput;

[McpClosedObject]
public sealed record AgasAiModelsListInput;

[McpClosedObject]
public sealed record AgasAiProfileActivateInput(
    [property: Required]
    [property: StringLength(120, MinimumLength = 1)]
    [property: RegularExpression(AgasMcpContractPatterns.SafeIdentifier)]
    string ProfileId);

[McpClosedObject]
public sealed record AgasAiModelSetInput(
    [property: Required]
    [property: StringLength(200, MinimumLength = 1)]
    string ModelName);

[McpClosedObject]
public sealed record AgasAiDiagnosticsInput(
    [property: StringLength(120, MinimumLength = 1)]
    [property: RegularExpression(AgasMcpContractPatterns.SafeIdentifier)]
    string? ProfileId = null);
