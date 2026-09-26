namespace Aurum.App.SharedKernel.Common;

/// <summary>
/// The single response envelope for every controller action.
/// </summary>
/// <remarks>
/// The rule this type exists to enforce is "never return an anonymous type". An anonymous type has
/// no compile-time contract, so renaming a property is a silent breaking change for every consumer
/// — and the frontend DTOs that mirror these shapes have no way to notice.
/// </remarks>
public sealed class ApiResponse<T>
{
    public bool Success { get; init; }

    public T? Data { get; init; }

    public string? Message { get; init; }

    public int StatusCode { get; init; }

    /// <summary>Field-level validation failures, keyed by property name.</summary>
    public IReadOnlyDictionary<string, string[]>? Errors { get; init; }

    public static ApiResponse<T> CreateSuccess(T data, int statusCode = 200) =>
        new() { Success = true, Data = data, StatusCode = statusCode };

    public static ApiResponse<T> CreateError(string message, int statusCode = 500) =>
        new() { Success = false, Message = message, StatusCode = statusCode };

    public static ApiResponse<T> CreateValidationError(
        IReadOnlyDictionary<string, string[]> errors, string message = "Validation failed.") =>
        new() { Success = false, Message = message, StatusCode = 400, Errors = errors };
}
