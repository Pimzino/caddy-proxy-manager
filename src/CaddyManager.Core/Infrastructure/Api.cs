using Microsoft.AspNetCore.Http;

namespace CaddyManager.Core;

/// <summary>Authorization policy names. Registered by the Ops module.</summary>
public static class Policies
{
    /// <summary>Any signed-in user.</summary>
    public const string Viewer = "viewer";
    /// <summary>Operator or Admin.</summary>
    public const string Operator = "operator";
    /// <summary>Admin only.</summary>
    public const string Admin = "admin";
}

/// <summary>Consistent RFC 7807 error responses. The UI shows `title` and `detail`; `errors` maps field -> messages.</summary>
public static class ApiResults
{
    public static IResult BadRequest(string detail, IDictionary<string, string[]>? errors = null) =>
        errors is null
            ? Results.Problem(title: "Invalid request", detail: detail, statusCode: StatusCodes.Status400BadRequest)
            : Results.ValidationProblem(errors, detail: detail, title: "Invalid request");

    public static IResult NotFound(string what) =>
        Results.Problem(title: "Not found", detail: $"{what} was not found.", statusCode: StatusCodes.Status404NotFound);

    public static IResult Conflict(string detail) =>
        Results.Problem(title: "Conflict", detail: detail, statusCode: StatusCodes.Status409Conflict);

    public static IResult Failed(string title, string detail) =>
        Results.Problem(title: title, detail: detail, statusCode: StatusCodes.Status422UnprocessableEntity);
}

/// <summary>Collects field validation errors.</summary>
public sealed class Validator
{
    private readonly Dictionary<string, List<string>> _errors = new();
    public bool IsValid => _errors.Count == 0;

    public Validator Require(bool condition, string field, string message)
    {
        if (!condition) Add(field, message);
        return this;
    }

    public void Add(string field, string message)
    {
        if (!_errors.TryGetValue(field, out var list)) _errors[field] = list = new();
        list.Add(message);
    }

    public IResult ToResult(string detail = "One or more fields are invalid.") =>
        ApiResults.BadRequest(detail, _errors.ToDictionary(k => k.Key, v => v.Value.ToArray()));
}
