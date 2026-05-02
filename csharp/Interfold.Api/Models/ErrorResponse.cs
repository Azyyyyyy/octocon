using System.Diagnostics.CodeAnalysis;
using System.Net;
using System.Text.Json.Serialization;

namespace Interfold.Api.Models;

public class ErrorResponse
{
    [SetsRequiredMembers]
    public ErrorResponse(string error, string code, HttpStatusCode? statusCode = null, string? entityRef = null)
    {
        Error = error;
        Code = code;
        StatusCode = statusCode ?? HttpStatusCode.InternalServerError;
        EntityRef = entityRef;
    }

    public required string Error { get; init; }
    public required string Code { get; init; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public required string? EntityRef { get; init; }
    [JsonIgnore]
    internal HttpStatusCode StatusCode { get; init; }
}