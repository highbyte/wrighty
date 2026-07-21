using System.Text;
using Highbyte.Wrighty.Errors;
using Highbyte.Wrighty.LocalMarkdown;
using YamlDotNet.RepresentationModel;

namespace Highbyte.Wrighty.Importing;

public sealed record PortableImportSource(
    string Path,
    string Title,
    string Body,
    string? Status,
    string? Priority,
    IReadOnlyList<string> CustomFieldNames,
    string? CustomFieldsYaml);

public static class MarkdownImportPlanner
{
    private static readonly Encoding StrictUtf8 =
        new UTF8Encoding(false, true);

    public static async Task<PortableImportSource> PlanFileAsync(
        string path,
        IReadOnlyDictionary<string, string> mappings,
        string? forceStatus,
        CancellationToken cancellationToken)
    {
        var fullPath = Path.GetFullPath(path);
        if (!File.Exists(fullPath))
        {
            throw new TrackerException(
                "ARGUMENT_INVALID",
                $"Import source '{fullPath}' was not found.",
                2,
                new Dictionary<string, object?> { ["path"] = fullPath });
        }
        if (!string.Equals(Path.GetExtension(fullPath), ".md", StringComparison.OrdinalIgnoreCase))
        {
            throw new TrackerException(
                "ARGUMENT_INVALID",
                $"Import source '{fullPath}' is not Markdown.",
                2);
        }

        string content;
        try
        {
            content = StrictUtf8.GetString(
                await File.ReadAllBytesAsync(fullPath, cancellationToken));
        }
        catch (DecoderFallbackException exception)
        {
            throw new TrackerException(
                "IMPORT_DOCUMENT_INVALID",
                $"Import source '{fullPath}' is not valid UTF-8.",
                2,
                new Dictionary<string, object?> { ["path"] = fullPath },
                exception);
        }

        var source = LocalMarkdownDocumentCodec.ParseImportSource(fullPath, content);
        var title = Scalar(source.Metadata, "title")
            ?? FirstHeading(source.Body)
            ?? Path.GetFileNameWithoutExtension(fullPath);
        if (string.IsNullOrWhiteSpace(title) ||
            title.Length > 256 ||
            title.Contains('\r') ||
            title.Contains('\n'))
        {
            throw new TrackerException(
                "IMPORT_DOCUMENT_INVALID",
                $"Import source '{fullPath}' does not resolve to a valid single-line title.",
                2,
                new Dictionary<string, object?> { ["path"] = fullPath });
        }

        var statusKey = mappings.GetValueOrDefault("status") ?? "status";
        var priorityKey = mappings.GetValueOrDefault("priority") ?? "priority";
        var custom = new YamlMappingNode();
        var customNames = new List<string>();
        foreach (var pair in source.Metadata.Children)
        {
            var key = (pair.Key as YamlScalarNode)?.Value
                ?? throw Invalid(fullPath, "Frontmatter keys must be scalar.");
            if (string.Equals(key, statusKey, StringComparison.Ordinal) ||
                string.Equals(key, priorityKey, StringComparison.Ordinal) ||
                string.Equals(key, "date", StringComparison.Ordinal))
            {
                continue;
            }
            if (!LocalMarkdownReservedFields.IsReserved(key))
            {
                custom.Add(Clone(pair.Key), Clone(pair.Value));
                customNames.Add(key);
                continue;
            }
            if (!LocalMarkdownReservedFields.ManagedKeys.Contains(key, StringComparer.Ordinal))
            {
                throw Invalid(fullPath, $"Frontmatter field '{key}' is reserved for Wrighty.");
            }
        }

        return new PortableImportSource(
            fullPath,
            title.Trim(),
            source.Body,
            forceStatus ?? Scalar(source.Metadata, statusKey),
            Scalar(source.Metadata, priorityKey),
            customNames.Order(StringComparer.Ordinal).ToArray(),
            customNames.Count == 0 ? null : SerializeMapping(custom));
    }

    public static string AppendCustomFieldBlock(string body, string yaml)
    {
        var separator = body.Length == 0 || body.EndsWith('\n') ? string.Empty : "\n";
        return $"{body}{separator}\n<!-- wrighty:frontmatter -->\n```yaml\n{yaml.TrimEnd()}\n```\n";
    }

    private static string? Scalar(YamlMappingNode mapping, string key)
    {
        foreach (var pair in mapping.Children)
        {
            if (pair.Key is not YamlScalarNode { Value: { } name } ||
                !string.Equals(name, key, StringComparison.Ordinal))
            {
                continue;
            }
            return pair.Value is YamlScalarNode scalar
                ? scalar.Value
                : throw new TrackerException(
                    "IMPORT_DOCUMENT_INVALID",
                    $"Frontmatter field '{key}' must be scalar.",
                    2);
        }
        return null;
    }

    private static string? FirstHeading(string body)
    {
        using var reader = new StringReader(body);
        while (reader.ReadLine() is { } line)
        {
            if (line.StartsWith("# ", StringComparison.Ordinal) &&
                !string.IsNullOrWhiteSpace(line[2..]))
            {
                return line[2..].Trim();
            }
        }
        return null;
    }

    private static string SerializeMapping(YamlMappingNode mapping)
    {
        var stream = new YamlStream(new YamlDocument(mapping));
        using var writer = new StringWriter();
        stream.Save(writer, assignAnchors: false);
        return writer.ToString()
            .Replace("---\n", string.Empty, StringComparison.Ordinal)
            .Replace("...\n", string.Empty, StringComparison.Ordinal);
    }

    private static YamlNode Clone(YamlNode node)
    {
        var stream = new YamlStream(new YamlDocument(node));
        using var writer = new StringWriter();
        stream.Save(writer, assignAnchors: false);
        var copy = new YamlStream();
        copy.Load(new StringReader(writer.ToString()));
        return copy.Documents[0].RootNode;
    }

    private static TrackerException Invalid(string path, string message) => new(
        "IMPORT_DOCUMENT_INVALID",
        $"Invalid import source '{path}': {message}",
        2,
        new Dictionary<string, object?> { ["path"] = path });

    public static IReadOnlyList<string> DiscoverPaths(
        IReadOnlyList<string> inputs,
        bool recursive)
    {
        var files = new List<string>();
        foreach (var input in inputs)
        {
            if (File.Exists(input))
            {
                if (!string.Equals(
                        Path.GetExtension(input),
                        ".md",
                        StringComparison.OrdinalIgnoreCase))
                {
                    throw new TrackerException(
                        "ARGUMENT_INVALID",
                        $"Import file '{input}' is not Markdown.",
                        2);
                }
                files.Add(Path.GetFullPath(input));
                continue;
            }
            if (Directory.Exists(input))
            {
                files.AddRange(Directory.EnumerateFiles(
                    input,
                    "*",
                    recursive
                        ? SearchOption.AllDirectories
                        : SearchOption.TopDirectoryOnly)
                    .Where(path => string.Equals(
                        Path.GetExtension(path),
                        ".md",
                        StringComparison.OrdinalIgnoreCase))
                    .Select(Path.GetFullPath));
                continue;
            }
            throw new TrackerException(
                "ARGUMENT_INVALID",
                $"Import path '{input}' does not exist.",
                2);
        }
        return files;
    }
}
