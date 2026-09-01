using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;

namespace Highbyte.Wrighty.Web;

internal sealed class WebDiagnostics(ILogger<WebDiagnostics> logger)
{
    private const string FailureKey = "wrighty.web.failure";

    public static void RetainFailure(HttpContext context, string code, Exception exception) =>
        context.Items[FailureKey] = new WebFailure(code, exception);

    public Task LogFailureAsync(HttpContext context)
    {
        if (context.Response.StatusCode < StatusCodes.Status400BadRequest)
            return Task.CompletedTask;

        var failure = context.Items.TryGetValue(FailureKey, out var value)
            ? value as WebFailure
            : null;
        var code = failure?.Code ?? $"HTTP_{context.Response.StatusCode}";
        var target = RequestTarget(context.Request);
        if (context.Response.StatusCode >= StatusCodes.Status500InternalServerError)
        {
            WebDiagnosticEvents.ServerRequestFailed(
                logger,
                context.Request.Method,
                target,
                context.Response.StatusCode,
                code,
                failure?.Exception);
        }
        else
        {
            WebDiagnosticEvents.ClientRequestFailed(
                logger,
                context.Request.Method,
                target,
                context.Response.StatusCode,
                code,
                failure?.Exception);
        }

        return Task.CompletedTask;
    }

    private static string RequestTarget(HttpRequest request)
    {
        var values = new List<string>();
        foreach (var name in new[] { "handler", "id", "scope" })
        {
            if (request.Query.TryGetValue(name, out var value) &&
                !string.IsNullOrWhiteSpace(value))
            {
                values.Add($"{name}={Uri.EscapeDataString(value.ToString())}");
            }
        }

        return (request.Path.Value ?? "/") +
               (values.Count == 0 ? string.Empty : $"?{string.Join("&", values)}");
    }

    private sealed record WebFailure(string Code, Exception Exception);
}

internal static partial class WebDiagnosticEvents
{
    [LoggerMessage(
        EventId = 2001,
        Level = LogLevel.Warning,
        Message = "Web request {RequestMethod} {RequestTarget} returned {StatusCode} {ErrorCode}.")]
    public static partial void ClientRequestFailed(
        ILogger logger,
        string requestMethod,
        string requestTarget,
        int statusCode,
        string errorCode,
        Exception? exception);

    [LoggerMessage(
        EventId = 2002,
        Level = LogLevel.Error,
        Message = "Web request {RequestMethod} {RequestTarget} returned {StatusCode} {ErrorCode}.")]
    public static partial void ServerRequestFailed(
        ILogger logger,
        string requestMethod,
        string requestTarget,
        int statusCode,
        string errorCode,
        Exception? exception);

    [LoggerMessage(
        EventId = 2003,
        Level = LogLevel.Warning,
        Message = "Could not open the default browser.")]
    public static partial void BrowserLaunchFailed(
        ILogger logger,
        Exception exception);
}
