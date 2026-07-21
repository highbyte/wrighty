using Highbyte.Wrighty.Importing;
using Highbyte.Wrighty.Errors;

namespace Highbyte.Wrighty.UnitTests.Importing;

public sealed class MarkdownImportPlannerTests : IDisposable
{
    private readonly string directory = Path.Combine(
        Path.GetTempPath(),
        $"wrighty-import-planner-{Guid.NewGuid():N}");

    [Fact]
    public async Task Title_precedence_mapping_and_nested_custom_yaml_are_preserved()
    {
        Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, "fallback.md");
        await File.WriteAllTextAsync(
            path,
            """
            ---
            title: Frontmatter title
            state: In Review
            priority: P1
            epic:
              id: PLAT-3
            tags:
              - import
              - safe
            ---
            # Heading title

            Body
            """);

        var source = await MarkdownImportPlanner.PlanFileAsync(
            path,
            new Dictionary<string, string> { ["status"] = "state" },
            null,
            CancellationToken.None);
        var body = MarkdownImportPlanner.AppendCustomFieldBlock(
            source.Body,
            source.CustomFieldsYaml!);

        Assert.Equal("Frontmatter title", source.Title);
        Assert.Equal("In Review", source.Status);
        Assert.Equal("P1", source.Priority);
        Assert.Equal(["epic", "tags"], source.CustomFieldNames);
        Assert.Contains("<!-- wrighty:frontmatter -->", body);
        Assert.Contains("epic:", body);
        Assert.Contains("id: PLAT-3", body);
        Assert.Contains("- import", body);
        Assert.Contains("Body", body);
    }

    [Fact]
    public async Task Heading_then_filename_resolve_title_without_frontmatter()
    {
        Directory.CreateDirectory(directory);
        var heading = Path.Combine(directory, "heading.md");
        var filename = Path.Combine(directory, "filename.md");
        await File.WriteAllTextAsync(heading, "# Heading\nBody");
        await File.WriteAllTextAsync(filename, "Body only");

        Assert.Equal(
            "Heading",
            (await MarkdownImportPlanner.PlanFileAsync(
                heading,
                new Dictionary<string, string>(),
                null,
                CancellationToken.None)).Title);
        Assert.Equal(
            "filename",
            (await MarkdownImportPlanner.PlanFileAsync(
                filename,
                new Dictionary<string, string>(),
                null,
                CancellationToken.None)).Title);
    }

    [Fact]
    public async Task Force_status_overrides_source_and_plain_documents_have_no_custom_block()
    {
        Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, "plain.md");
        await File.WriteAllTextAsync(path, "---\nstatus: Todo\ndate: 2026-07-20\n---\n# Plain\nBody");

        var source = await MarkdownImportPlanner.PlanFileAsync(
            path,
            new Dictionary<string, string>(),
            "In Progress",
            CancellationToken.None);

        Assert.Equal("In Progress", source.Status);
        Assert.Empty(source.CustomFieldNames);
        Assert.Null(source.CustomFieldsYaml);
        Assert.Equal(
            "\n<!-- wrighty:frontmatter -->\n```yaml\nestimate: 5\n```\n",
            MarkdownImportPlanner.AppendCustomFieldBlock(string.Empty, "estimate: 5\n"));
    }

    [Theory]
    [InlineData("missing.md", "was not found")]
    [InlineData("note.txt", "is not Markdown")]
    public async Task Rejects_missing_and_non_markdown_sources(
        string fileName,
        string expected)
    {
        Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, fileName);
        if (Path.GetExtension(path) == ".txt")
        {
            await File.WriteAllTextAsync(path, "text");
        }

        var exception = await Assert.ThrowsAsync<TrackerException>(
            () => MarkdownImportPlanner.PlanFileAsync(
                path,
                new Dictionary<string, string>(),
                null,
                CancellationToken.None));

        Assert.Equal("ARGUMENT_INVALID", exception.Code);
        Assert.Contains(expected, exception.Message);
    }

    [Fact]
    public async Task Rejects_invalid_utf8_and_invalid_resolved_titles()
    {
        Directory.CreateDirectory(directory);
        var invalidUtf8 = Path.Combine(directory, "utf8.md");
        var longTitle = Path.Combine(directory, "long.md");
        await File.WriteAllBytesAsync(invalidUtf8, [0xC3, 0x28]);
        await File.WriteAllTextAsync(
            longTitle,
            $"---\ntitle: {new string('x', 257)}\n---\nBody");

        var encoding = await Assert.ThrowsAsync<TrackerException>(
            () => MarkdownImportPlanner.PlanFileAsync(
                invalidUtf8,
                new Dictionary<string, string>(),
                null,
                CancellationToken.None));
        var title = await Assert.ThrowsAsync<TrackerException>(
            () => MarkdownImportPlanner.PlanFileAsync(
                longTitle,
                new Dictionary<string, string>(),
                null,
                CancellationToken.None));

        Assert.Equal("IMPORT_DOCUMENT_INVALID", encoding.Code);
        Assert.Equal("IMPORT_DOCUMENT_INVALID", title.Code);
    }

    public void Dispose()
    {
        if (Directory.Exists(directory))
        {
            Directory.Delete(directory, true);
        }
    }
}
