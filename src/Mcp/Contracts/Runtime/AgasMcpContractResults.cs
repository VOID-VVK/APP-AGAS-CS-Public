namespace AppAgasCs.Mcp.Contracts.Runtime;

public sealed record AgasMcpContractValidationError(
    string Code,
    string Path,
    string Message);

public sealed record AgasMcpContractBindResult<T>(
    bool Success,
    T? Value,
    AgasMcpContractValidationError? Error)
    where T : class;

public static class AgasMcpContractValidationCodes
{
    public const string Type = "type";
    public const string Required = "required";
    public const string AdditionalProperties = "additionalProperties";
    public const string Enum = "enum";
    public const string Const = "const";
    public const string Pattern = "pattern";
    public const string MinLength = "minLength";
    public const string MaxLength = "maxLength";
    public const string Minimum = "minimum";
    public const string Maximum = "maximum";
    public const string MinItems = "minItems";
    public const string MaxItems = "maxItems";
    public const string UniqueItems = "uniqueItems";
    public const string AnyRequired = "anyOf";
    public const string Deserialize = "deserialize";
}
