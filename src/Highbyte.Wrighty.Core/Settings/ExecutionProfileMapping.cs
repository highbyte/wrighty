using System.Text.Json;
using System.Text.Json.Serialization;
using Highbyte.Wrighty.Workers;

namespace Highbyte.Wrighty.Settings;

/// <summary>
/// Writes <see cref="ExecutionEffort"/> using the same lowercase token the vendor command line
/// uses. Hand-written rather than <c>JsonStringEnumConverter</c> because no built-in naming policy
/// produces <c>xhigh</c> — camel case gives <c>xHigh</c> — and the settings file should show the
/// operator exactly what the CLI will receive.
/// </summary>
public sealed class ExecutionEffortJsonConverter : JsonConverter<ExecutionEffort>
{
    public override ExecutionEffort Read(
        ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        // Records written before this converter existed hold the enum's ordinal. Reading those is
        // not optional: refusing them makes an entire machine-local runtime file unreadable, which
        // takes every session address on it down with the effort value.
        if (reader.TokenType == JsonTokenType.Number)
        {
            var ordinal = reader.GetInt32();
            return Enum.IsDefined(typeof(ExecutionEffort), ordinal)
                ? (ExecutionEffort)ordinal
                : throw new JsonException($"'{ordinal}' is not a known effort level.");
        }

        var value = reader.GetString();
        if (!ExecutionEfforts.TryParse(value, out var effort))
        {
            throw new JsonException(
                $"'{value}' is not a supported effort level. Expected one of: " +
                string.Join(", ", ExecutionEfforts.All) + ".");
        }

        return effort;
    }

    public override void Write(
        Utf8JsonWriter writer, ExecutionEffort value, JsonSerializerOptions options) =>
        writer.WriteStringValue(value.ToToken());
}

/// <summary>
/// One user's choice of model and effort for a single (profile, agent) pair.
///
/// A null <see cref="Model"/> means "pass no model argument", deliberately letting the vendor CLI's
/// own configured default win — which is a real choice, not an absence of one. Empty or whitespace
/// is not the same thing and is rejected on the way in, because it would otherwise reach a command
/// line as an empty argument.
/// </summary>
public sealed record ExecutionProfileMapping
{
    [JsonPropertyName("model")]
    public string? Model { get; init; }

    [JsonPropertyName("effort")]
    public ExecutionEffort? Effort { get; init; }

    /// <summary>Computed, never persisted: the settings file is hand-editable and should carry
    /// only what an operator can meaningfully change.</summary>
    [JsonIgnore]
    public bool IsEmpty => Model is null && Effort is null;
}
