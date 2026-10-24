namespace AppAgasCs.Core.Persistence;

public readonly record struct AgasPersistenceResult(bool Success, string? ErrorMessage)
{
    public static AgasPersistenceResult Ok() => new(true, null);
    public static AgasPersistenceResult Fail(string errorMessage) => new(false, errorMessage);
}

public readonly record struct AgasLoadResult<T>(T? Resource, string? ErrorMessage)
{
    public bool Success => Resource is not null && ErrorMessage is null;
    public static AgasLoadResult<T> Ok(T resource) => new(resource, null);
    public static AgasLoadResult<T> Fail(string errorMessage) => new(default, errorMessage);
}
