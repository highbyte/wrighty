using System.Text.Json;
using Highbyte.Wrighty.Errors;

namespace Highbyte.Wrighty.GitHub;

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
            throw new TrackerException(
                "GH_RESPONSE_INVALID",
                "GitHub CLI returned malformed JSON.",
                innerException: exception);
        }
    }

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
}
