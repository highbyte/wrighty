using System.Text.Json;
using Highbyte.Wrighty.Processes;

namespace Highbyte.Wrighty.Workers;

/// <summary>
/// Reads OpenCode's supported CLI export. The raw local export is bounded before parsing; Wrighty's
/// handoff packet builder remains responsible for selecting, redacting, and bounding conversation
/// text consistently with the other vendors.
/// </summary>
public sealed class OpenCodeSessionExporter : IAgentSessionExporter
{
    private const int MaximumOutputBytes = 64 * 1024 * 1024;
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(30);
    private readonly IBoundedAgentCommand command;

    public OpenCodeSessionExporter(IExecutableResolver executables)
        : this(new BoundedAgentCommand(executables))
    {
    }

    internal OpenCodeSessionExporter(IBoundedAgentCommand command)
    {
        this.command = command;
    }

    public string Agent => "opencode";

    public async Task<SessionExportResult> ExportAsync(
        string sessionId,
        CancellationToken cancellationToken)
    {
        if (!SessionTranscripts.IsSafeSessionId(sessionId))
        {
            return SessionExportResult.NotAvailable(
                "The recorded OpenCode session ID is not valid.");
        }

        var result = await command.RunAsync(
            "opencode",
            ["export", sessionId],
            MaximumOutputBytes,
            Timeout,
            cancellationToken);
        if (result.Status != BoundedAgentCommandStatus.Completed)
            return SessionExportResult.NotAvailable(UnavailableReason(result.Status));
        if (result.ExitCode != 0)
        {
            return SessionExportResult.NotAvailable(
                $"OpenCode could not export session '{sessionId}'.");
        }

        return Parse(result.StandardOutput) is { } messages
            ? SessionExportResult.From("opencode-cli-export", messages)
            : SessionExportResult.NotAvailable(
                $"OpenCode returned an unrecognized export for session '{sessionId}'.");
    }

    internal static IReadOnlyList<ExportedSessionMessage>? Parse(string output)
    {
        var jsonStart = output.IndexOf('{');
        if (jsonStart < 0)
            return null;

        try
        {
            using var document = JsonDocument.Parse(output[jsonStart..]);
            if (!document.RootElement.TryGetProperty("messages", out var messages) ||
                messages.ValueKind != JsonValueKind.Array)
            {
                return null;
            }

            var exported = new List<ExportedSessionMessage>();
            foreach (var message in messages.EnumerateArray())
            {
                if (!message.TryGetProperty("info", out var info) ||
                    info.ValueKind != JsonValueKind.Object ||
                    !info.TryGetProperty("role", out var roleValue) ||
                    roleValue.GetString() is not ("user" or "assistant") ||
                    !message.TryGetProperty("parts", out var parts) ||
                    parts.ValueKind != JsonValueKind.Array)
                {
                    continue;
                }

                var text = string.Join("\n\n", parts.EnumerateArray()
                    .Where(part => part.ValueKind == JsonValueKind.Object &&
                        part.TryGetProperty("type", out var type) &&
                        type.GetString() == "text" &&
                        part.TryGetProperty("text", out var textValue) &&
                        textValue.ValueKind == JsonValueKind.String)
                    .Select(part => part.GetProperty("text").GetString())
                    .Where(value => !string.IsNullOrWhiteSpace(value)));
                if (!string.IsNullOrWhiteSpace(text))
                    exported.Add(new ExportedSessionMessage(roleValue.GetString()!, text));
            }
            return exported;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static string UnavailableReason(BoundedAgentCommandStatus status) => status switch
    {
        BoundedAgentCommandStatus.NotInstalled =>
            "OpenCode is not installed on this host.",
        BoundedAgentCommandStatus.TimedOut =>
            "OpenCode session export exceeded its time limit.",
        BoundedAgentCommandStatus.OutputTooLarge =>
            "The OpenCode session export is larger than the export limit.",
        _ => "OpenCode session export is unavailable on this host."
    };
}
