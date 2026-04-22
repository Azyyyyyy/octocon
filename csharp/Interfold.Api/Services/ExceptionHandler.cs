using Interfold.Contracts;
using Microsoft.AspNetCore.Diagnostics;

namespace Interfold.Api.Services;

public class ExceptionHandler : IExceptionHandler
{
    public ValueTask<bool> TryHandleAsync(HttpContext httpContext, Exception exception, CancellationToken cancellationToken)
    {
        // Log the exception and return a generic error response.
        httpContext.Response.StatusCode = StatusCodes.Status500InternalServerError;
        httpContext.Response.ContentType = "application/json";
        httpContext.Response.WriteAsJsonAsync(CreateError(httpContext, exception), cancellationToken);
        return new ValueTask<bool>(true);
    }

    public object CreateError(HttpContext httpContext, Exception exception)
    {
        if (exception is BadHttpRequestException badRequestException)
        {
            return new { error = badRequestException.Message, code = "bad_request" };
        }

        if (exception is InterfoldException interfoldException)
        {
            return new { error = interfoldException.Message, code = interfoldException.Code };
        }

        return new { error = $"Unhandled exception: {exception}", code = "unknown_error" };
    }
}