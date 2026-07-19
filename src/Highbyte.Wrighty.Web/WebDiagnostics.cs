using Microsoft.AspNetCore.Http;

namespace Highbyte.Wrighty.Web;

internal sealed class WebDiagnostics(TextWriter output)
{
    private const string FailureKey = "wrighty.web.failure";
    private readonly SemaphoreSlim writeLock = new(1, 1);

    public static void RetainFailure(HttpContext context, string code, Exception exception) =>
        context.Items[FailureKey] = new WebFailure(code, exception);

    public async Task LogFailureAsync(HttpContext context)
    {
        if (context.Response.StatusCode < StatusCodes.Status400BadRequest)
            return;

        var failure = context.Items.TryGetValue(FailureKey, out var value)
            ? value as WebFailure
            : null;
        var code = failure?.Code ?? $"HTTP_{context.Response.StatusCode}";
        var target = RequestTarget(context.Request);
        var message =
            $"web error: {DateTimeOffset.UtcNow:O} {context.Request.Method} {target} -> " +
            $"{context.Response.StatusCode} {code}";
        if (failure is not null)
            message += $"{Environment.NewLine}{failure.Exception}";

        await writeLock.WaitAsync();
        try
        {
            await output.WriteLineAsync(message);
            await output.FlushAsync();
        }
        finally
        {
            writeLock.Release();
        }
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
