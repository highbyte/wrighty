using System.Globalization;
using System.Text.Json;
using Highbyte.Wrighty.Errors;
using Highbyte.Wrighty.Models;
using YamlDotNet.Core;
using YamlDotNet.RepresentationModel;

namespace Highbyte.Wrighty.LocalMarkdown;

internal sealed record LocalCreationMetadata(
    int Version,
    string AttemptId,
    string RequestHash);

internal sealed record LocalMarkdownImportSource(
    YamlMappingNode Metadata,
    string Body);

internal sealed class LocalMarkdownDocument(
    int id,
    string path,
    bool archived,
    YamlMappingNode metadata,
    string body,
    string revision,
    string rawFrontmatter)
{
    public int Id { get; } = id;
    public string Path { get; set; } = path;
    public bool Archived { get; set; } = archived;
    public YamlMappingNode Metadata { get; } = metadata;
    public string Body { get; set; } = body;
    public string Revision { get; set; } = revision;
    public string RawFrontmatter { get; set; } = rawFrontmatter;

    public IReadOnlyDictionary<string, JsonElement> CustomFields => Metadata.Children
        .Where(pair => pair.Key is YamlScalarNode scalar &&
                       scalar.Value is { } name &&
                       !LocalMarkdownReservedFields.IsReserved(name))
        .ToDictionary(
            pair => ((YamlScalarNode)pair.Key).Value!,
            pair => ToJsonElement(pair.Value),
            StringComparer.Ordinal);

    public string? CustomFieldScalar(string name) =>
        TryGet(name, out var node) && node is YamlScalarNode scalar ? scalar.Value : null;

    public void SetCustomField(string name, string? value)
    {
        LocalMarkdownReservedFields.ValidateCustomFieldName(name);
        if (value is null)
        {
            Remove(name);
        }
        else
        {
            SetNode(Metadata, name, new YamlScalarNode(value) { Style = ScalarStyle.DoubleQuoted },
                canonicalManaged: false);
        }
    }

    public void SetCustomFieldNode(string name, YamlNode value)
    {
        LocalMarkdownReservedFields.ValidateCustomFieldName(name);
        SetNode(Metadata, name, Clone(value), canonicalManaged: false);
    }

    public string Title { get => Required("title"); set => Set("title", value); }
    public string Status { get => Required("status"); set => Set("status", value); }
    public string? Priority { get => Optional("priority"); set => SetOptional("priority", value); }
    public bool AutomaticExecutionAllowed
    {
        get => string.Equals(
            WrightyOptional("policy", "execution"),
            "automatic",
            StringComparison.OrdinalIgnoreCase);
        set => SetWrightyOptional("policy", "execution", value ? "automatic" : null);
    }
    public string? AgentPolicy
    {
        get => WrightyOptional("policy", "agent");
        set => SetWrightyOptional("policy", "agent", value);
    }
    public string? DispatchState
    {
        get => WrightyOptional("dispatch", "state");
        set
        {
            DispatchStates.Validate(value);
            SetWrightyOptional("dispatch", "state", value);
        }
    }
    public DateTimeOffset CreatedAt { get => RequiredDate("createdAt"); set => SetDate("createdAt", value); }
    public DateTimeOffset UpdatedAt { get => RequiredDate("updatedAt"); set => SetDate("updatedAt", value); }

    public LocalCreationMetadata? Creation
    {
        get
        {
            var creation = WrightySection("creation", create: false);
            if (creation is null)
            {
                return null;
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
            if (value is null)
            {
                RemoveWrightySection("creation");
                return;
            }

            var creation = WrightySection("creation", create: true)!;
            creation.Children.Clear();
            Set(creation, "version", value.Version.ToString(CultureInfo.InvariantCulture));
            Set(creation, "attemptId", value.AttemptId);
            Set(creation, "requestHash", value.RequestHash);
        }
    }

    public bool HasLegacySchema =>
        new[] { "claim", "claimEpoch", "creation", "wrighty-auto", "wrighty-agent", "wrighty-worker-state" }
            .Any(key => TryGet(key, out _));

    private string? WrightyOptional(string sectionName, string key) =>
        WrightySection(sectionName, create: false) is { } section
            ? Optional(section, key)
            : null;

    private void SetWrightyOptional(string sectionName, string key, string? value)
    {
        var section = WrightySection(sectionName, create: value is not null);
        if (section is null)
        {
            return;
        }

        SetOptional(section, key, value);
        if (section.Children.Count == 0)
        {
            RemoveWrightySection(sectionName);
        }
    }

    private YamlMappingNode? WrightySection(string name, bool create)
    {
        YamlMappingNode root;
        if (!TryGet("wrighty", out var rootNode))
        {
            if (!create)
            {
                return null;
            }

            root = new YamlMappingNode();
            SetNode(Metadata, "wrighty", root, canonicalManaged: true);
        }
        else if (rootNode is YamlMappingNode mapping)
        {
            root = mapping;
        }
        else
        {
            throw Invalid("Reserved frontmatter field 'wrighty' must be a mapping.");
        }

        if (!TryGet(root, name, out var sectionNode))
        {
            if (!create)
            {
                return null;
            }

            var section = new YamlMappingNode();
            SetNode(root, name, section, canonicalManaged: false);
            return section;
        }

        return sectionNode as YamlMappingNode
            ?? throw Invalid($"Reserved frontmatter field 'wrighty.{name}' must be a mapping.");
    }

    private void RemoveWrightySection(string name)
    {
        if (!TryGet("wrighty", out var rootNode) || rootNode is not YamlMappingNode root)
        {
            return;
        }

        Remove(root, name);
        if (root.Children.Count == 0)
        {
            Remove("wrighty");
        }
    }

    private string Required(string key) => Required(Metadata, key);
    private string? Optional(string key) => Optional(Metadata, key);
    private DateTimeOffset RequiredDate(string key) => RequiredDate(Metadata, key);
    private void Set(string key, string value) =>
        SetNode(Metadata, key, new YamlScalarNode(value), canonicalManaged: true);
    private void SetOptional(string key, string? value)
    {
        if (value is null) Remove(key);
        else Set(key, value);
    }
    private void SetDate(string key, DateTimeOffset value) =>
        Set(key, value.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture));

    private static string Required(YamlMappingNode mapping, string key) =>
        Optional(mapping, key) is { } value && !string.IsNullOrWhiteSpace(value)
            ? value
            : throw new TrackerException("WORK_ITEM_DOCUMENT_INVALID", $"Required frontmatter field '{key}' is missing or empty.", 5);

    private static string? Optional(YamlMappingNode mapping, string key) =>
        TryGet(mapping, key, out var node)
            ? node is YamlScalarNode scalar
                ? scalar.Value
                : throw new TrackerException(
                    "WORK_ITEM_DOCUMENT_INVALID",
                    $"Reserved frontmatter field '{key}' collides with Wrighty metadata and must be a scalar.",
                    5)
            : null;

    private static DateTimeOffset RequiredDate(YamlMappingNode mapping, string key) =>
        DateTimeOffset.TryParse(Required(mapping, key), CultureInfo.InvariantCulture,
            DateTimeStyles.AssumeUniversal, out var value)
            ? value.ToUniversalTime()
            : throw new TrackerException("WORK_ITEM_DOCUMENT_INVALID", $"Frontmatter field '{key}' is not a valid timestamp.", 5);

    private static void Set(YamlMappingNode mapping, string key, string value)
    {
        SetNode(mapping, key, new YamlScalarNode(value), canonicalManaged: false);
    }

    private static void SetOptional(YamlMappingNode mapping, string key, string? value)
    {
        if (value is null)
        {
            Remove(mapping, key);
            return;
        }

        Set(mapping, key, value);
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

    private static void SetNode(
        YamlMappingNode mapping,
        string key,
        YamlNode value,
        bool canonicalManaged)
    {
        var existing = mapping.Children.Keys.OfType<YamlScalarNode>()
            .FirstOrDefault(node => string.Equals(node.Value, key, StringComparison.Ordinal));
        if (existing is not null)
        {
            mapping.Children[existing] = value;
            return;
        }

        if (!canonicalManaged)
        {
            mapping.Add(key, value);
            return;
        }

        var pairs = mapping.Children.ToArray();
        var newRank = ManagedRank(new YamlScalarNode(key));
        var insertion = pairs
            .Select((pair, index) => (Rank: ManagedRank(pair.Key), Index: index))
            .Where(item => item.Rank < newRank)
            .Select(item => item.Index + 1)
            .DefaultIfEmpty(0)
            .Max();
        if (insertion == 0)
        {
            insertion = pairs
                .Select((pair, index) => (Rank: ManagedRank(pair.Key), Index: index))
                .Where(item => item.Rank > newRank && item.Rank != int.MaxValue)
                .Select(item => item.Index)
                .DefaultIfEmpty(pairs.Length)
                .Min();
        }

        mapping.Children.Clear();
        for (var index = 0; index <= pairs.Length; index++)
        {
            if (index == insertion) mapping.Add(key, value);
            if (index < pairs.Length) mapping.Add(pairs[index].Key, pairs[index].Value);
        }
    }

    private static int ManagedRank(YamlNode key)
    {
        if (key is not YamlScalarNode { Value: { } name }) return int.MaxValue;
        for (var index = 0; index < LocalMarkdownReservedFields.ManagedKeys.Count; index++)
        {
            if (string.Equals(LocalMarkdownReservedFields.ManagedKeys[index], name, StringComparison.Ordinal))
            {
                return index;
            }
        }

        return int.MaxValue;
    }

    private static JsonElement ToJsonElement(YamlNode node)
    {
        object? value = node switch
        {
            YamlMappingNode mapping => mapping.Children.ToDictionary(
                pair => (pair.Key as YamlScalarNode)?.Value ?? string.Empty,
                pair => JsonElementToObject(ToJsonElement(pair.Value)),
                StringComparer.Ordinal),
            YamlSequenceNode sequence => sequence.Children
                .Select(child => JsonElementToObject(ToJsonElement(child))).ToArray(),
            YamlScalarNode scalar => ScalarValue(scalar),
            _ => null
        };
        return JsonSerializer.SerializeToElement(value);
    }

    private static YamlNode Clone(YamlNode node)
    {
        var stream = new YamlStream(new YamlDocument(node));
        using var writer = new StringWriter(CultureInfo.InvariantCulture);
        stream.Save(writer, assignAnchors: false);
        var copy = new YamlStream();
        copy.Load(new StringReader(writer.ToString()));
        return copy.Documents[0].RootNode;
    }

    private static object? ScalarValue(YamlScalarNode scalar)
    {
        var value = scalar.Value;
        if (scalar.Style is ScalarStyle.SingleQuoted or ScalarStyle.DoubleQuoted or
            ScalarStyle.Literal or ScalarStyle.Folded) return value;
        if (value is null || value is "null" or "~") return null;
        if (bool.TryParse(value, out var boolean)) return boolean;
        if (long.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var integer)) return integer;
        if (double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out var number)) return number;
        return value;
    }

    private static object? JsonElementToObject(JsonElement element) => element.ValueKind switch
    {
        JsonValueKind.Object => element.EnumerateObject().ToDictionary(
            property => property.Name,
            property => JsonElementToObject(property.Value),
            StringComparer.Ordinal),
        JsonValueKind.Array => element.EnumerateArray().Select(JsonElementToObject).ToArray(),
        JsonValueKind.String => element.GetString(),
        JsonValueKind.Number when element.TryGetInt64(out var integer) => integer,
        JsonValueKind.Number => element.GetDouble(),
        JsonValueKind.True => true,
        JsonValueKind.False => false,
        _ => null
    };

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
    public static LocalMarkdownDocument Parse(
        int id,
        string path,
        bool archived,
        string content,
        string revision)
    {
        try
        {
            return ParseDocument(id, path, archived, content, revision);
        }
        catch (TrackerException exception) when (
            exception.Code == "STORE_SCHEMA_UNSUPPORTED")
        {
            throw;
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
        string content,
        string revision)
    {
        var bounds = FindFrontmatterBounds(content, path);
        var yaml = content[bounds.YamlStart..bounds.YamlEnd];
        var mapping = ParseFrontmatter(yaml, path);
        var document = new LocalMarkdownDocument(
            id,
            path,
            archived,
            mapping,
            content[bounds.BodyStart..],
            revision,
            yaml);
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
        // TODO(post-1.0): Remove pre-overhaul Local Markdown schema detection once pre-1.0
        // stores are no longer expected. This guard must remain read-only and must never migrate
        // legacy metadata into the current schema.
        if (document.HasLegacySchema)
        {
            throw new TrackerException(
                "STORE_SCHEMA_UNSUPPORTED",
                "Wrighty found a work-item file containing unsupported metadata. " +
                $"Unsupported file: '{document.Path}'. " +
                "Remove or rename the listed file so it is outside the active store, then retry.",
                5,
                new Dictionary<string, object?>
                {
                    ["path"] = document.Path,
                    ["unsupportedFiles"] = new[] { document.Path },
                    ["id"] = document.Id
                });
        }

        _ = document.AutomaticExecutionAllowed;
        _ = document.AgentPolicy;
        _ = document.DispatchState;
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
        var document = new LocalMarkdownDocument(id, path, archived, metadata, body, string.Empty, string.Empty)
        {
            Title = title,
            Status = status,
            Priority = priority,
            CreatedAt = now,
            UpdatedAt = now
        };
        document.Creation = creation;
        document.RawFrontmatter = SerializeFrontmatter(document.Metadata);
        return document;
    }

    public static LocalMarkdownImportSource ParseImportSource(string path, string content)
    {
        if (!content.StartsWith("---\n", StringComparison.Ordinal) &&
            !content.StartsWith("---\r\n", StringComparison.Ordinal))
        {
            return new LocalMarkdownImportSource(new YamlMappingNode(), content);
        }

        try
        {
            var bounds = FindFrontmatterBounds(content, path);
            return new LocalMarkdownImportSource(
                ParseFrontmatter(content[bounds.YamlStart..bounds.YamlEnd], path),
                content[bounds.BodyStart..]);
        }
        catch (TrackerException)
        {
            throw;
        }
        catch (Exception exception)
        {
            throw Invalid(path, $"Could not parse import frontmatter: {exception.Message}");
        }
    }

    public static string Serialize(LocalMarkdownDocument document)
    {
        var text = SerializeFrontmatter(document.Metadata);
        return $"---\n{text}---\n{document.Body}";
    }

    public static string SerializeFrontmatter(YamlMappingNode metadata)
    {
        var yaml = new YamlStream(new YamlDocument(metadata));
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

        return text;
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
