using System.Text.Json;
using Highbyte.Wrighty.Errors;

namespace Highbyte.Wrighty.GitHub;

public sealed record GhConditionalJsonResponse(
    bool NotModified,
    string? ETag,
    string? Link,
    JsonElement? Body);

public sealed class GhApi(IGhProcess process)
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    public async Task<JsonDocument> GraphQlAsync(
        string host,
        string query,
        object variables,
        CancellationToken cancellationToken)
    {
        var input = JsonSerializer.Serialize(new { query, variables }, JsonOptions);
        return await ExecuteJsonAsync(
            ["api", "graphql", "--hostname", host, "--input", "-"],
            input,
            cancellationToken);
    }

    public async Task<JsonDocument> GetPaginatedAsync(
        string host,
        string endpoint,
        CancellationToken cancellationToken)
    {
        return await ExecuteJsonAsync(
            ["api", "--hostname", host, "--paginate", "--slurp", endpoint],
            null,
            cancellationToken);
    }

    public async Task<JsonDocument> GetAsync(
        string host,
        string endpoint,
        CancellationToken cancellationToken)
    {
        return await ExecuteJsonAsync(
            ["api", "--hostname", host, endpoint],
            null,
            cancellationToken);
    }

    public async Task<JsonDocument> GetVersionedAsync(
        string host,
        string endpoint,
        string apiVersion,
        CancellationToken cancellationToken)
    {
        return await ExecuteJsonAsync(
            [
                "api", "--hostname", host,
                "--header", $"X-GitHub-Api-Version: {apiVersion}",
                endpoint
            ],
            null,
            cancellationToken);
    }

    /// <summary>
    /// Performs an authenticated conditional GET and retains the response metadata that
    /// <c>gh api</c> normally hides. GitHub does not charge a correctly authorized
    /// <c>304 Not Modified</c> response against the primary REST rate limit.
    /// </summary>
    public async Task<GhConditionalJsonResponse> GetVersionedConditionalAsync(
        string host,
        string endpoint,
        string apiVersion,
        string? etag,
        CancellationToken cancellationToken)
    {
        var arguments = new List<string>
        {
            "api", "--hostname", host, "--include",
            "--header", $"X-GitHub-Api-Version: {apiVersion}"
        };
        if (!string.IsNullOrWhiteSpace(etag))
        {
            arguments.Add("--header");
            arguments.Add($"If-None-Match: {etag}");
        }
        arguments.Add(endpoint);

        var result = await process.RunAsync(arguments, null, cancellationToken);
        IncludedResponse response;
        try
        {
            response = ParseIncludedResponse(result.StandardOutput);
        }
        catch (TrackerException) when (result.ExitCode != 0)
        {
            // Preserve authentication, rate-limit, and ordinary API diagnostics when gh did not
            // produce an included HTTP response at all. A 304 does produce one and is handled
            // below even though gh reports that status on stderr.
            EnsureSuccess(result);
            throw;
        }
        if (response.StatusCode == 304)
        {
            return new GhConditionalJsonResponse(
                true,
                response.ETag ?? etag,
                response.Link,
                null);
        }

        EnsureSuccess(result);
        if (response.StatusCode is < 200 or >= 300)
        {
            throw new TrackerException(
                "GH_API_ERROR",
                $"GitHub API returned HTTP {response.StatusCode}.",
                10);
        }

        try
        {
            using var document = JsonDocument.Parse(
                string.IsNullOrWhiteSpace(response.Body) ? "{}" : response.Body);
            return new GhConditionalJsonResponse(
                false,
                response.ETag,
                response.Link,
                document.RootElement.Clone());
        }
        catch (JsonException exception)
        {
            throw InvalidResponse(exception);
        }
    }

    public async Task<JsonDocument> GetVersionedPaginatedAsync(
        string host,
        string endpoint,
        string apiVersion,
        CancellationToken cancellationToken)
    {
        return await ExecuteJsonAsync(
            [
                "api", "--hostname", host, "--paginate", "--slurp",
                "--header", $"X-GitHub-Api-Version: {apiVersion}",
                endpoint
            ],
            null,
            cancellationToken);
    }

    public async Task<JsonDocument> SendJsonAsync(
        string host,
        string method,
        string endpoint,
        object body,
        CancellationToken cancellationToken)
    {
        return await ExecuteJsonAsync(
            ["api", "--hostname", host, "--method", method, "--input", "-", endpoint],
            JsonSerializer.Serialize(body, JsonOptions),
            cancellationToken);
    }

    public async Task<JsonDocument> SendVersionedJsonAsync(
        string host,
        string method,
        string endpoint,
        string apiVersion,
        object body,
        CancellationToken cancellationToken)
    {
        return await ExecuteJsonAsync(
            [
                "api", "--hostname", host, "--method", method,
                "--header", $"X-GitHub-Api-Version: {apiVersion}",
                "--input", "-", endpoint
            ],
            JsonSerializer.Serialize(body, JsonOptions),
            cancellationToken);
    }

    public async Task DeleteAsync(
        string host,
        string endpoint,
        CancellationToken cancellationToken)
    {
        var result = await process.RunAsync(
            ["api", "--hostname", host, "--method", "DELETE", endpoint],
            null,
            cancellationToken);

        EnsureSuccess(result);
    }

    private async Task<JsonDocument> ExecuteJsonAsync(
        IReadOnlyList<string> arguments,
        string? input,
        CancellationToken cancellationToken)
    {
        var result = await process.RunAsync(arguments, input, cancellationToken);
        EnsureSuccess(result);

        try
        {
            return JsonDocument.Parse(string.IsNullOrWhiteSpace(result.StandardOutput)
                ? "{}"
                : result.StandardOutput);
        }
        catch (JsonException exception)
        {
            throw InvalidResponse(exception);
        }
    }

    private static IncludedResponse ParseIncludedResponse(string output)
    {
        var separator = output.IndexOf("\r\n\r\n", StringComparison.Ordinal);
        var separatorLength = 4;
        if (separator < 0)
        {
            separator = output.IndexOf("\n\n", StringComparison.Ordinal);
            separatorLength = 2;
        }
        if (separator < 0)
        {
            throw InvalidResponse();
        }

        var lines = output[..separator]
            .Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries);
        if (lines.Length == 0)
        {
            throw InvalidResponse();
        }

        var statusParts = lines[0].Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (statusParts.Length < 2 ||
            !statusParts[0].StartsWith("HTTP/", StringComparison.OrdinalIgnoreCase) ||
            !int.TryParse(statusParts[1], out var statusCode))
        {
            throw InvalidResponse();
        }

        return new IncludedResponse(
            statusCode,
            Header(lines, "ETag"),
            Header(lines, "Link"),
            output[(separator + separatorLength)..]);
    }

    private static string? Header(IEnumerable<string> lines, string name)
    {
        var prefix = name + ":";
        var line = lines.FirstOrDefault(value =>
            value.StartsWith(prefix, StringComparison.OrdinalIgnoreCase));
        return line?[prefix.Length..].Trim();
    }

    private static TrackerException InvalidResponse(Exception? exception = null) =>
        new(
            "GH_RESPONSE_INVALID",
            "GitHub CLI returned a malformed HTTP or JSON response.",
            innerException: exception);

    private static void EnsureSuccess(GhProcessResult result)
    {
        if (result.ExitCode == 0)
        {
            return;
        }

        var error = result.StandardError.Trim();
        var code = error.Contains("authentication", StringComparison.OrdinalIgnoreCase) ||
                   error.Contains("auth login", StringComparison.OrdinalIgnoreCase)
            ? "GH_AUTH_REQUIRED"
            : error.Contains("rate limit", StringComparison.OrdinalIgnoreCase)
                ? "GH_RATE_LIMITED"
                : "GH_API_ERROR";

        var exitCode = code == "GH_AUTH_REQUIRED" ? 4 : 10;
        throw new TrackerException(
            code,
            string.IsNullOrEmpty(error) ? "GitHub CLI request failed." : error,
            exitCode);
    }

    private sealed record IncludedResponse(
        int StatusCode,
        string? ETag,
        string? Link,
        string Body);
}
