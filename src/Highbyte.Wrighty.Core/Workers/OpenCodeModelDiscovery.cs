using System.Text.Json;
using Highbyte.Wrighty.Processes;

namespace Highbyte.Wrighty.Workers;

/// <summary>Reads OpenCode's enabled provider-qualified model and variant catalog.</summary>
public sealed class OpenCodeModelDiscovery : IAgentModelDiscovery
{
    private const int MaximumOutputBytes = 8 * 1024 * 1024;
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(20);
    private readonly IBoundedAgentCommand command;
    private readonly Func<DateTimeOffset> now;

    public OpenCodeModelDiscovery(IExecutableResolver executables)
        : this(new BoundedAgentCommand(executables))
    {
    }

    internal OpenCodeModelDiscovery(
        IBoundedAgentCommand command,
        Func<DateTimeOffset>? clock = null)
    {
        this.command = command;
        now = clock ?? (() => DateTimeOffset.UtcNow);
    }

    public string Agent => "opencode";

    public async Task<AgentModelCatalog> DiscoverAsync(CancellationToken cancellationToken)
    {
        var observedAt = now();
        var result = await command.RunAsync(
            "opencode",
            ["models", "--verbose"],
            MaximumOutputBytes,
            Timeout,
            cancellationToken);
        var failure = result.Status switch
        {
            BoundedAgentCommandStatus.NotInstalled => ModelDiscoveryFailure.NotInstalled,
            BoundedAgentCommandStatus.TimedOut => ModelDiscoveryFailure.TimedOut,
            BoundedAgentCommandStatus.OutputTooLarge => ModelDiscoveryFailure.Unrecognized,
            BoundedAgentCommandStatus.Unavailable => ModelDiscoveryFailure.Unavailable,
            _ => ModelDiscoveryFailure.None
        };
        if (failure != ModelDiscoveryFailure.None)
            return AgentModelCatalog.Unavailable(Agent, failure, observedAt);
        if (result.ExitCode != 0)
        {
            return AgentModelCatalog.Unavailable(
                Agent,
                MentionsAuthentication(result.StandardError) ||
                MentionsAuthentication(result.StandardOutput)
                    ? ModelDiscoveryFailure.NotAuthenticated
                    : ModelDiscoveryFailure.Unavailable,
                observedAt);
        }

        var models = Parse(result.StandardOutput);
        if (models is null)
        {
            return AgentModelCatalog.Unavailable(
                Agent,
                ModelDiscoveryFailure.Unrecognized,
                observedAt);
        }
        return new AgentModelCatalog(Agent, models, DiscoveredAt: observedAt);
    }

    /// <summary>
    /// <c>opencode models --verbose</c> interleaves one provider/model line with one pretty-printed
    /// JSON object. Extracting balanced objects tolerates model IDs containing additional slashes
    /// and avoids treating the human-readable identifier lines as JSON.
    /// </summary>
    internal static IReadOnlyList<AgentModel>? Parse(string output)
    {
        var objects = JsonObjects(output);
        if (objects.Count == 0)
            return string.IsNullOrWhiteSpace(output) ? [] : null;

        var models = new List<AgentModel>();
        foreach (var json in objects)
        {
            try
            {
                using var document = JsonDocument.Parse(json);
                if (Read(document.RootElement) is { } model)
                    models.Add(model);
            }
            catch (JsonException)
            {
                return null;
            }
        }
        if (models.Count == 0)
            return null;
        return models
            .GroupBy(model => model.Id, StringComparer.Ordinal)
            .Select(group => group.First())
            .OrderBy(model => model.Id, StringComparer.Ordinal)
            .ToArray();
    }

    private static AgentModel? Read(JsonElement entry)
    {
        if (entry.ValueKind != JsonValueKind.Object ||
            Text(entry, "providerID") is not { } provider ||
            Text(entry, "id") is not { } id)
        {
            return null;
        }

        var efforts = entry.TryGetProperty("variants", out var variants) &&
                      variants.ValueKind == JsonValueKind.Object
            ? variants.EnumerateObject()
                .Select(variant => variant.Name)
                .Where(name => ExecutionEfforts.TryParse(name, out _))
                .ToArray()
            : [];

        var reasoning = entry.TryGetProperty("capabilities", out var capabilities) &&
            capabilities.ValueKind == JsonValueKind.Object &&
            capabilities.TryGetProperty("reasoning", out var reasoningValue)
                ? reasoningValue.ValueKind
                : JsonValueKind.Undefined;
        var support = EffortSupportFor(efforts, reasoning);
        return new AgentModel(
            $"{provider}/{id}",
            Text(entry, "name"),
            Effort: support,
            SupportedEfforts: efforts);
    }

    private static EffortSupport EffortSupportFor(
        IReadOnlyCollection<string> efforts,
        JsonValueKind reasoning)
    {
        if (efforts.Count > 0)
            return EffortSupport.Yes;
        return reasoning == JsonValueKind.False
            ? EffortSupport.No
            : EffortSupport.Unknown;
    }

    private static List<string> JsonObjects(string output)
    {
        var values = new List<string>();
        var start = -1;
        var depth = 0;
        var quoted = false;
        for (var index = 0; index < output.Length; index++)
        {
            var character = output[index];
            if (start < 0)
            {
                if (character == '{')
                {
                    start = index;
                    depth = 1;
                }
                continue;
            }

            if (quoted)
            {
                if (character == '\\')
                    index++;
                else if (character == '"')
                    quoted = false;
                continue;
            }

            if (character == '"')
            {
                quoted = true;
            }
            else if (character == '{')
            {
                depth++;
            }
            else if (character == '}' && --depth == 0)
            {
                values.Add(output[start..(index + 1)]);
                start = -1;
            }
        }
        return start < 0 ? values : [];
    }

    private static bool MentionsAuthentication(string message) =>
        message.Contains("log in", StringComparison.OrdinalIgnoreCase) ||
        message.Contains("login", StringComparison.OrdinalIgnoreCase) ||
        message.Contains("authenticat", StringComparison.OrdinalIgnoreCase);

    private static string? Text(JsonElement element, string property) =>
        element.TryGetProperty(property, out var value) &&
        value.ValueKind == JsonValueKind.String &&
        value.GetString() is { Length: > 0 } text
            ? text
            : null;
}
