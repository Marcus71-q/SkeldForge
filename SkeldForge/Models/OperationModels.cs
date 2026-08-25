namespace SkeldForge.Models;

public sealed record OperationProgress(string StageKey, long Completed, long Total, string? CurrentItem = null)
{
    public double Fraction => Total <= 0 ? 0 : Math.Clamp((double)Completed / Total, 0, 1);
}

public sealed record DiskSpaceInfo(long RequiredBytes, long AvailableBytes)
{
    public bool HasEnoughSpace => AvailableBytes >= RequiredBytes;
}

public class OperationResult
{
    public bool Success { get; init; }
    public string ErrorKey { get; init; } = string.Empty;
    public string TechnicalMessage { get; init; } = string.Empty;
    public static OperationResult Ok() => new() { Success = true };
    public static OperationResult Fail(string errorKey, string technicalMessage = "") => new() { ErrorKey = errorKey, TechnicalMessage = technicalMessage };
}

public sealed class OperationResult<T> : OperationResult
{
    public T? Value { get; init; }
    public static OperationResult<T> Ok(T value) => new() { Success = true, Value = value };
    public new static OperationResult<T> Fail(string errorKey, string technicalMessage = "") => new() { ErrorKey = errorKey, TechnicalMessage = technicalMessage };
}
