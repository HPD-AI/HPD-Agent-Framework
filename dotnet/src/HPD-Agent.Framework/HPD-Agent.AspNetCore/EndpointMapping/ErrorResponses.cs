using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.AspNetCore.Http;

namespace HPD.Agent.AspNetCore.EndpointMapping;

/// <summary>
/// Consistent response helpers that avoid PipeWriter.UnflushedBytes issues with TestHost.
/// These helpers use Results.Content for JSON serialization instead of WriteAsJsonAsync.
/// </summary>
internal static class ErrorResponses
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };

    /// <summary>
    /// Returns a JSON result with the specified status code and data.
    /// Uses manual serialization to avoid PipeWriter.UnflushedBytes issues with TestHost.
    /// </summary>
    public static IResult Json<T>(T data, int statusCode = 200)
    {
        return Results.Content(
            JsonSerializer.Serialize(data, JsonOptions),
            contentType: "application/json",
            statusCode: statusCode);
    }

    /// <summary>
    /// Returns a 201 Created response with JSON body and Location header.
    /// </summary>
    public static IResult Created<T>(string location, T data)
    {
        return new CreatedResult(location, data);
    }

    /// <summary>
    /// Returns a 404 Not Found response (no body).
    /// </summary>
    public static IResult NotFound()
    {
        return Results.NotFound();
    }

    /// <summary>
    /// Returns a 404 Not Found with ValidationProblem shape.
    /// </summary>
    public static IResult NotFound(string errorKey, string errorMessage)
    {
        var errors = new Dictionary<string, string[]>
        {
            [errorKey] = [errorMessage]
        };
        return Json(new { errors }, 404);
    }

    /// <summary>
    /// Returns a 409 Conflict response (no body).
    /// </summary>
    public static IResult Conflict()
    {
        return Results.Conflict();
    }

    /// <summary>
    /// Returns a 409 Conflict with ValidationProblem shape.
    /// </summary>
    public static IResult Conflict(string errorKey, string errorMessage)
    {
        var errors = new Dictionary<string, string[]>
        {
            [errorKey] = [errorMessage]
        };
        return Json(new { errors }, 409);
    }

    /// <summary>
    /// Returns a 400 Bad Request response (no body).
    /// </summary>
    public static IResult BadRequest()
    {
        return Results.BadRequest();
    }

    /// <summary>
    /// Returns a 400 Bad Request with ValidationProblem shape.
    /// </summary>
    public static IResult BadRequest(string errorKey, string errorMessage)
    {
        var errors = new Dictionary<string, string[]>
        {
            [errorKey] = [errorMessage]
        };
        return Json(new { errors }, 400);
    }

    /// <summary>
    /// Returns a 204 No Content response.
    /// </summary>
    public static IResult NoContent()
    {
        return Results.NoContent();
    }

    /// <summary>
    /// Returns a ValidationProblem response with the specified errors.
    /// </summary>
    public static IResult ValidationProblem(Dictionary<string, string[]> errors)
    {
        return Json(errors, 400);
    }

    /// <summary>
    /// Returns a 500 Internal Server Error with ValidationProblem shape.
    /// </summary>
    public static IResult InternalServerError(string errorKey, string errorMessage)
    {
        var errors = new Dictionary<string, string[]>
        {
            [errorKey] = [errorMessage]
        };
        return Json(new { errors }, 500);
    }

    private class CreatedResult : IResult
    {
        private readonly string _location;
        private readonly object _value;

        public CreatedResult(string location, object value)
        {
            _location = location;
            _value = value;
        }

        public Task ExecuteAsync(HttpContext httpContext)
        {
            httpContext.Response.StatusCode = 201;
            httpContext.Response.Headers.Location = _location;
            httpContext.Response.ContentType = "application/json";
            var json = JsonSerializer.Serialize(_value, JsonOptions);
            return httpContext.Response.WriteAsync(json);
        }
    }
}
