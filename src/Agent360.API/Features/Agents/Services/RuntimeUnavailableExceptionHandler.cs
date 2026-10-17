using Agent360.API.Features.Agents.SubAgents;
using Microsoft.AspNetCore.Diagnostics;

namespace Agent360.API.Features.Agents.Services;

/// <summary>
/// Maps transient agent-runtime failures (QwenPaw unreachable, upstream timeouts) to a clean 503 problem
/// with Retry-After instead of an unhandled 500 + ERR stack trace. Client disconnects are swallowed.
/// Registered ahead of the shared global handler so every other exception keeps its existing behavior.
/// </summary>
public sealed class RuntimeUnavailableExceptionHandler(ILogger<RuntimeUnavailableExceptionHandler> logger) : IExceptionHandler
{
    public async ValueTask<bool> TryHandleAsync(HttpContext http, Exception exception, CancellationToken ct)
    {
        if (http.RequestAborted.IsCancellationRequested && exception is OperationCanceledException)
        {
            // The caller went away; nothing useful to return and nothing to alert on.
            http.Response.StatusCode = 499;
            return true;
        }

        if (!IsRuntimeUnavailable(exception) || http.Response.HasStarted)
        {
            return false;
        }

        logger.LogWarning("Agent runtime unavailable for {Method} {Path}: {Reason}", http.Request.Method, http.Request.Path, Root(exception).Message);
        http.Response.StatusCode = StatusCodes.Status503ServiceUnavailable;
        http.Response.Headers.RetryAfter = "5";
        await http.Response.WriteAsJsonAsync(new
        {
            type = "https://tools.ietf.org/html/rfc9110#section-15.6.4",
            title = "Agent runtime temporarily unavailable",
            status = 503,
            detail = exception.Message,
            instance = http.Request.Path.Value,
            retryable = true,
        }, ct);
        return true;
    }

    internal static bool IsRuntimeUnavailable(Exception ex) => ex switch
    {
        QwenPawUnavailableException => true,
        HttpRequestException => true,
        TaskCanceledException { InnerException: TimeoutException } => true,
        OperationCanceledException oce when oce.InnerException is HttpRequestException or IOException => true,
        _ => false,
    };

    private static Exception Root(Exception ex)
    {
        while (ex.InnerException is not null) ex = ex.InnerException;
        return ex;
    }
}
