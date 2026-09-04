using Microsoft.AspNetCore.Mvc;

namespace AccountantApp.Api.Shared.Errors;

public sealed class AppExceptionMiddleware
{
    private readonly RequestDelegate _next;
    private readonly ILogger<AppExceptionMiddleware> _logger;

    public AppExceptionMiddleware(RequestDelegate next, ILogger<AppExceptionMiddleware> logger)
    {
        _next = next;
        _logger = logger;
    }

    public async Task InvokeAsync(HttpContext context)
    {
        try
        {
            await _next(context);
        }
        catch (AppException exception)
        {
            // Deliberate failure: the status and message are meant for the caller.
            await WriteProblemDetails(context, exception.StatusCode, exception.Message);
        }
        catch (OperationCanceledException) when (context.RequestAborted.IsCancellationRequested)
        {
            // The client disconnected. Not a server error, and there is nobody left to answer.
        }
        catch (Microsoft.AspNetCore.Http.BadHttpRequestException exception)
        {
            // A malformed body or a missing required parameter is client-triggerable, so it is
            // always a 4xx, never the 500 minimal APIs would otherwise let it become.
            await WriteProblemDetails(context, exception.StatusCode, exception.Message);
        }
        catch (Exception exception)
        {
            // Unexpected: log everything, tell the caller nothing.
            _logger.LogError(exception, "Unhandled exception for {Method} {Path}",
                context.Request.Method, context.Request.Path);
            await WriteProblemDetails(context, 500, "An unexpected error occurred.");
        }
    }

    private static async Task WriteProblemDetails(HttpContext context, int statusCode, string title)
    {
        if (context.Response.HasStarted)
            return;

        context.Response.Clear();
        context.Response.StatusCode = statusCode;
        await context.Response.WriteAsJsonAsync(new ProblemDetails
        {
            Status = statusCode,
            Title = title,
            Extensions = { ["traceId"] = context.TraceIdentifier }
        });
    }
}
