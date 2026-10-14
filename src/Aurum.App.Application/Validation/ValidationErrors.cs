using FluentValidation;

namespace Aurum.App.Application.Validation;

public static class ValidationErrors
{
    /// <summary>Failures grouped by property, the shape <c>ApiResponse.Errors</c> carries.</summary>
    public static IReadOnlyDictionary<string, string[]> ToErrors(this ValidationException exception) =>
        exception.Errors
            .GroupBy(failure => failure.PropertyName)
            .ToDictionary(group => group.Key, group => group.Select(failure => failure.ErrorMessage).ToArray());
}
