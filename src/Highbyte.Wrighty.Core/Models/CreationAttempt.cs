using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Highbyte.Wrighty.Errors;

namespace Highbyte.Wrighty.Models;

public static class CreationAttempt
{
    public static string NormalizeOrCreate(string? value)
    {
        if (value is null)
        {
            return Guid.NewGuid().ToString("N");
        }

        if (string.IsNullOrWhiteSpace(value) ||
            !Guid.TryParseExact(value, value.Contains('-') ? "D" : "N", out var parsed))
        {
            throw new TrackerException(
                "ARGUMENT_INVALID",
                "Creation attempt ID must be a UUID in N or D format.",
                2);
        }

        return parsed.ToString("N");
    }

    public static string ComputeIntentHash(
        CreateWorkItemRequest request,
        bool archiveAfterCreate)
    {
        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream))
        {
            writer.WriteStartObject();
            writer.WriteString("title", request.Title);
            writer.WriteString("body", request.Body);
            writer.WriteString("status", request.Status);
            if (request.Priority is null)
            {
                writer.WriteNull("priority");
            }
            else
            {
                writer.WriteString("priority", request.Priority);
            }

            if (request.Fields is { Count: > 0 })
            {
                writer.WriteStartObject("fields");
                foreach (var field in request.Fields.OrderBy(pair => pair.Key, StringComparer.Ordinal))
                {
                    if (field.Value is null) writer.WriteNull(field.Key);
                    else writer.WriteString(field.Key, field.Value);
                }
                writer.WriteEndObject();
            }
            writer.WriteBoolean("automationEligible", request.AutomationEligible);
            if (request.PreferredAgent is null) writer.WriteNull("preferredAgent");
            else writer.WriteString("preferredAgent", request.PreferredAgent);

            writer.WriteBoolean("archiveAfterCreate", archiveAfterCreate);
            writer.WriteEndObject();
        }

        return Convert.ToHexStringLower(SHA256.HashData(stream.ToArray()));
    }
}
