using Highbyte.Wrighty.Importing;

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

    public void Dispose()
    {
        if (Directory.Exists(directory))
        {
            Directory.Delete(directory, true);
        }
    }
}
