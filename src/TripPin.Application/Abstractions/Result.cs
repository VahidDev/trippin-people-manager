using System.Diagnostics.CodeAnalysis;

namespace TripPin.Application.Abstractions;

/// <summary>
/// Why an operation ended the way it did.
/// </summary>
/// <remarks>
/// These map directly onto the service's observed behaviour:
/// <see cref="NotFound"/> covers the 204-instead-of-404 quirk,
/// <see cref="ConcurrencyConflict"/> covers a 412 from a stale ETag.
/// Neither is exceptional, so neither is an exception.
/// </remarks>
public enum ResultStatus
{
    Success = 0,
    NotFound = 1,
    ValidationFailed = 2,
    ConcurrencyConflict = 3,
    TransportFailure = 4,
    Cancelled = 5,
}

/// <summary>
/// Outcome of a use case. Expected failures are values; only genuine bugs
/// and invariant violations throw.
/// </summary>
/// <remarks>
/// Carries a list of errors rather than one string, because validation
/// reports every problem at once instead of making the user fix them one
/// round trip at a time.
/// <para>
/// Compare the individual members in assertions, not whole instances: the
/// compiler-generated equality treats <see cref="Errors"/> by reference.
/// </para>
/// </remarks>
public sealed record Result<T>
{
    private static readonly IReadOnlyList<string> NoErrors = [];

    private Result(ResultStatus status, T? value, IReadOnlyList<string> errors)
    {
        Status = status;
        Value = value;
        Errors = errors;
    }

    public ResultStatus Status { get; }

    public T? Value { get; }

    public IReadOnlyList<string> Errors { get; }

    /// <summary>
    /// True when the operation succeeded, in which case <see cref="Value"/> is
    /// non-null.
    /// </summary>
    /// <remarks>
    /// The <c>MemberNotNullWhen</c> annotation is what lets callers write
    /// <c>result.Value.Name</c> after checking this, instead of reaching for
    /// the null-forgiving operator at every call site and losing the
    /// compiler's help along with it.
    /// </remarks>
    [MemberNotNullWhen(true, nameof(Value))]
    public bool IsSuccess => Status == ResultStatus.Success;

    /// <summary>The first error, or null on success. Convenience for single-error paths.</summary>
    public string? Error => Errors.Count > 0 ? Errors[0] : null;

    public static Result<T> Success(T value) => new(ResultStatus.Success, value, NoErrors);

    public static Result<T> Failure(ResultStatus status, string error) =>
        new(status, default, [error]);

    public static Result<T> Failure(ResultStatus status, IReadOnlyList<string> errors) =>
        new(status, default, [.. errors]);

    public static Result<T> NotFound(string error) =>
        Failure(ResultStatus.NotFound, error);

    public static Result<T> ValidationFailed(IReadOnlyList<string> errors) =>
        Failure(ResultStatus.ValidationFailed, errors);

    public static Result<T> Conflict(string error) =>
        Failure(ResultStatus.ConcurrencyConflict, error);
}
