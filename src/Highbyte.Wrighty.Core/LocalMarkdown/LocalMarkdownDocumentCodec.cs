using System.Globalization;
using Highbyte.Wrighty.Errors;
using YamlDotNet.RepresentationModel;

namespace Highbyte.Wrighty.LocalMarkdown;

internal sealed record LocalClaimMetadata(
    string WorkerIdentity,
    string AgentType,
    string? SessionId,
    string ClaimAttemptId,
    DateTimeOffset ClaimedAt,
    DateTimeOffset ExpiresAt);

internal sealed record LocalCreationMetadata(
    int Version,
    string AttemptId,
    string RequestHash);

internal sealed class LocalMarkdownDocument(
    int id,
    string path,
    bool archived,
    YamlMappingNode metadata,
    string body)
{
    public int Id { get; } = id;
    public string Path { get; set; } = path;
    public bool Archived { get; set; } = archived;
    public YamlMappingNode Metadata { get; } = metadata;
    public string Body { get; set; } = body;

    public string Title { get => Required("title"); set => Set("title", value); }
    public string Status { get => Required("status"); set => Set("status", value); }
    public string? Priority { get => Optional("priority"); set => SetOptional("priority", value); }
    public DateTimeOffset CreatedAt { get => RequiredDate("createdAt"); set => SetDate("createdAt", value); }
    public DateTimeOffset UpdatedAt { get => RequiredDate("updatedAt"); set => SetDate("updatedAt", value); }
    public int ClaimEpoch
    {
        get => int.TryParse(Required("claimEpoch"), NumberStyles.None,
            CultureInfo.InvariantCulture, out var value) && value >= 0
                ? value
                : throw Invalid("claimEpoch must be a non-negative integer.");
        set => Set("claimEpoch", value.ToString(CultureInfo.InvariantCulture));
    }

    public LocalClaimMetadata? Claim
    {
        get
        {
            if (!TryGet("claim", out var node))
            {
                return null;
            }

            if (node is not YamlMappingNode claim)
            {
                throw Invalid("claim must be a mapping.");
            }

            return new LocalClaimMetadata(
                Required(claim, "workerIdentity"),
                Required(claim, "agentType"),
                Optional(claim, "sessionId"),
                Required(claim, "claimAttemptId"),
                RequiredDate(claim, "claimedAt"),
                RequiredDate(claim, "expiresAt"));
        }
        set
        {
            Remove("claim");
            if (value is null)
            {
                return;
            }

            var claim = new YamlMappingNode();
            Set(claim, "workerIdentity", value.WorkerIdentity);
            Set(claim, "agentType", value.AgentType);
            SetOptional(claim, "sessionId", value.SessionId);
            Set(claim, "claimAttemptId", value.ClaimAttemptId);
            SetDate(claim, "claimedAt", value.ClaimedAt);
            SetDate(claim, "expiresAt", value.ExpiresAt);
            Metadata.Add("claim", claim);
        }
    }

    public LocalCreationMetadata? Creation
    {
        get
        {
            if (!TryGet("creation", out var node))
            {
                return null;
            }

            if (node is not YamlMappingNode creation)
            {
                throw Invalid("creation must be a mapping.");
            }

            if (!int.TryParse(Required(creation, "version"), NumberStyles.None,
                    CultureInfo.InvariantCulture, out var version) || version != 1)
            {
                throw Invalid("creation.version must be 1.");
            }

            return new LocalCreationMetadata(
                version,
                Required(creation, "attemptId"),
                Required(creation, "requestHash"));
        }
        set
        {
            Remove("creation");
            if (value is null)
            {
                return;
            }

            var creation = new YamlMappingNode();
            Set(creation, "version", value.Version.ToString(CultureInfo.InvariantCulture));
            Set(creation, "attemptId", value.AttemptId);
            Set(creation, "requestHash", value.RequestHash);
            Metadata.Add("creation", creation);
        }
    }

    private string Required(string key) => Required(Metadata, key);
    private string? Optional(string key) => Optional(Metadata, key);
    private DateTimeOffset RequiredDate(string key) => RequiredDate(Metadata, key);
    private void Set(string key, string value) => Set(Metadata, key, value);
    private void SetOptional(string key, string? value) => SetOptional(Metadata, key, value);
    private void SetDate(string key, DateTimeOffset value) => SetDate(Metadata, key, value);

    private static string Required(YamlMappingNode mapping, string key) =>
        Optional(mapping, key) is { } value && !string.IsNullOrWhiteSpace(value)
            ? value
            : throw new TrackerException("WORK_ITEM_DOCUMENT_INVALID", $"Required frontmatter field '{key}' is missing or empty.", 5);

    private static string? Optional(YamlMappingNode mapping, string key) =>
        TryGet(mapping, key, out var node)
            ? node is YamlScalarNode scalar
                ? scalar.Value
                : throw new TrackerException("WORK_ITEM_DOCUMENT_INVALID", $"Frontmatter field '{key}' must be a scalar.", 5)
            : null;

    private static DateTimeOffset RequiredDate(YamlMappingNode mapping, string key) =>
        DateTimeOffset.TryParse(Required(mapping, key), CultureInfo.InvariantCulture,
            DateTimeStyles.AssumeUniversal, out var value)
            ? value.ToUniversalTime()
            : throw new TrackerException("WORK_ITEM_DOCUMENT_INVALID", $"Frontmatter field '{key}' is not a valid timestamp.", 5);

    private static void Set(YamlMappingNode mapping, string key, string value)
    {
        Remove(mapping, key);
        mapping.Add(key, value);
    }

    private static void SetOptional(YamlMappingNode mapping, string key, string? value)
    {
        Remove(mapping, key);
        if (value is not null)
        {
            mapping.Add(key, value);
        }
    }

    private static void SetDate(YamlMappingNode mapping, string key, DateTimeOffset value) =>
        Set(mapping, key, value.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture));

    private bool TryGet(string key, out YamlNode node) => TryGet(Metadata, key, out node);

    private static bool TryGet(YamlMappingNode mapping, string key, out YamlNode node)
    {
        foreach (var pair in mapping.Children)
        {
            if (pair.Key is YamlScalarNode scalar && string.Equals(scalar.Value, key, StringComparison.Ordinal))
            {
                node = pair.Value;
                return true;
            }
        }

        node = null!;
        return false;
    }

    private void Remove(string key) => Remove(Metadata, key);

    private static void Remove(YamlMappingNode mapping, string key)
    {
        var found = mapping.Children.Keys.OfType<YamlScalarNode>()
            .FirstOrDefault(node => string.Equals(node.Value, key, StringComparison.Ordinal));
        if (found is not null)
        {
            mapping.Children.Remove(found);
        }
    }

    private TrackerException Invalid(string message) => new(
        "WORK_ITEM_DOCUMENT_INVALID",
        $"Invalid work item '{Path}': {message}",
        5,
        new Dictionary<string, object?> { ["path"] = Path, ["id"] = Id });
}

internal static class LocalMarkdownDocumentCodec
{
    public static LocalMarkdownDocument Parse(int id, string path, bool archived, string content)
    {
        try
        {
            return ParseDocument(id, path, archived, content);
        }
        catch (TrackerException exception) when (!exception.Details.ContainsKey("path"))
        {
            throw new TrackerException(
                "WORK_ITEM_DOCUMENT_INVALID",
                $"Invalid work item '{path}': {exception.Message}",
                5,
                new Dictionary<string, object?> { ["path"] = path, ["id"] = id },
                exception);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            throw new TrackerException(
                "WORK_ITEM_DOCUMENT_INVALID",
                $"Invalid work item '{path}': {exception.Message}",
                5,
                new Dictionary<string, object?> { ["path"] = path, ["id"] = id },
                exception);
        }
    }

    private static LocalMarkdownDocument ParseDocument(
        int id,
        string path,
        bool archived,
        string content)
    {
        var bounds = FindFrontmatterBounds(content, path);
        var yaml = content[bounds.YamlStart..bounds.YamlEnd];
        var mapping = ParseFrontmatter(yaml, path);
        var document = new LocalMarkdownDocument(
            id,
            path,
            archived,
            mapping,
            content[bounds.BodyStart..]);
        ValidateDocument(document);
        return document;
    }

    private static FrontmatterBounds FindFrontmatterBounds(string content, string path)
    {
        var firstBreak = content.IndexOf('\n');
        if (firstBreak < 0 || content[..firstBreak].TrimEnd('\r') != "---")
        {
            throw Invalid(path, "The document must begin with a YAML frontmatter delimiter.");
        }

        var cursor = firstBreak + 1;
        while (cursor <= content.Length)
        {
            var nextBreak = content.IndexOf('\n', cursor);
            var lineEnd = nextBreak < 0 ? content.Length : nextBreak;
            if (content[cursor..lineEnd].TrimEnd('\r') == "---")
            {
                var bodyStart = nextBreak < 0 ? content.Length : nextBreak + 1;
                return new FrontmatterBounds(firstBreak + 1, cursor, bodyStart);
            }

            if (nextBreak < 0)
            {
                break;
            }

            cursor = nextBreak + 1;
        }

        throw Invalid(path, "The YAML frontmatter closing delimiter is missing.");
    }

    private static YamlMappingNode ParseFrontmatter(string yaml, string path)
    {
        var stream = new YamlStream();
        stream.Load(new StringReader(yaml));
        if (stream.Documents.Count != 1 || stream.Documents[0].RootNode is not YamlMappingNode mapping)
        {
            throw Invalid(path, "Frontmatter must contain one YAML mapping.");
        }

        EnsureUniqueKeys(mapping, path);
        return mapping;
    }

    private static void ValidateDocument(LocalMarkdownDocument document)
    {
        _ = document.Title;
        _ = document.Status;
        _ = document.CreatedAt;
        _ = document.UpdatedAt;
        _ = document.ClaimEpoch;
        _ = document.Claim;
        _ = document.Creation;
    }

    public static LocalMarkdownDocument Create(
        int id,
        string path,
        bool archived,
        string title,
        string body,
        string status,
        string? priority,
        LocalCreationMetadata creation,
        DateTimeOffset now)
    {
        var metadata = new YamlMappingNode();
        var document = new LocalMarkdownDocument(id, path, archived, metadata, body)
        {
            Title = title,
            Status = status,
            Priority = priority,
            CreatedAt = now,
            UpdatedAt = now,
            ClaimEpoch = 0
        };
        document.Creation = creation;
        return document;
    }

    public static string Serialize(LocalMarkdownDocument document)
    {
        var yaml = new YamlStream(new YamlDocument(document.Metadata));
        using var writer = new StringWriter(CultureInfo.InvariantCulture);
        yaml.Save(writer, assignAnchors: false);
        var text = writer.ToString().Replace("\r\n", "\n", StringComparison.Ordinal);
        if (text.StartsWith("---\n", StringComparison.Ordinal))
        {
            text = text[4..];
        }

        if (text.EndsWith("...\n", StringComparison.Ordinal))
        {
            text = text[..^4];
        }

        return $"---\n{text}---\n{document.Body}";
    }

    private static void EnsureUniqueKeys(YamlMappingNode mapping, string path)
    {
        var keys = new HashSet<string>(StringComparer.Ordinal);
        foreach (var key in mapping.Children.Keys)
        {
            if (key is not YamlScalarNode scalar || scalar.Value is null || !keys.Add(scalar.Value))
            {
                throw Invalid(path, "Frontmatter contains a non-scalar or duplicate key.");
            }
        }
    }

    private static TrackerException Invalid(string path, string message) => new(
        "WORK_ITEM_DOCUMENT_INVALID",
        $"Invalid work item '{path}': {message}",
        5,
        new Dictionary<string, object?> { ["path"] = path });

    private sealed record FrontmatterBounds(int YamlStart, int YamlEnd, int BodyStart);
}
