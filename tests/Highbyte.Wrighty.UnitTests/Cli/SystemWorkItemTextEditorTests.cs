using Highbyte.Wrighty.Cli;
using Highbyte.Wrighty.Errors;
using System.Reflection;

namespace Highbyte.Wrighty.UnitTests.Cli;

[Collection("Process environment")]
public sealed class SystemWorkItemTextEditorTests : IDisposable
{
    private readonly string? visual = Environment.GetEnvironmentVariable("VISUAL");
    private readonly string? editor = Environment.GetEnvironmentVariable("EDITOR");
    private readonly string root = Path.Combine(
        Path.GetTempPath(), $"wrighty-editor-tests-{Guid.NewGuid():N}");

    public SystemWorkItemTextEditorTests()
    {
        Directory.CreateDirectory(root);
        Environment.SetEnvironmentVariable("VISUAL", null);
        Environment.SetEnvironmentVariable("EDITOR", null);
    }

    [Fact]
    public async Task Edit_with_successful_editor_returns_the_saved_content()
    {
        RequireUnix();
        Environment.SetEnvironmentVariable("VISUAL", "/usr/bin/true");
        var editor = new SystemWorkItemTextEditor();

        editor.Validate();
        var result = await editor.EditAsync("Original", "Body\n", CancellationToken.None);

        Assert.Equal("Original", result.Title);
        Assert.Equal("Body\n", result.Body);
    }

    [Fact]
    public async Task Quoted_editor_command_can_update_title_and_body()
    {
        RequireUnix();
        var script = CreateScript(
            "editor with spaces",
            """
            file="$3"
            printf 'Title: Updated\n\n--- Wrighty Markdown body below this line ---\nNew body\n' > "$file"
            """);
        Environment.SetEnvironmentVariable(
            "VISUAL", $"'{script}' --mode \"two words\"");
        var editor = new SystemWorkItemTextEditor();

        var result = await editor.EditAsync("Original", "Old body", CancellationToken.None);

        Assert.Equal("Updated", result.Title);
        Assert.Equal("New body\n", result.Body);
    }

    [Fact]
    public void Validate_uses_editor_when_visual_is_blank()
    {
        RequireUnix();
        Environment.SetEnvironmentVariable("VISUAL", " ");
        Environment.SetEnvironmentVariable("EDITOR", "/usr/bin/true");

        new SystemWorkItemTextEditor().Validate();
    }

    [Fact]
    public void Validate_rejects_missing_or_unmatched_editor_commands()
    {
        var missing = Assert.Throws<TrackerException>(
            () => new SystemWorkItemTextEditor().Validate());
        Assert.Equal("EDITOR_UNAVAILABLE", missing.Code);

        Environment.SetEnvironmentVariable("VISUAL", "'unterminated");
        var unmatched = Assert.Throws<TrackerException>(
            () => new SystemWorkItemTextEditor().Validate());
        Assert.Equal("EDITOR_UNAVAILABLE", unmatched.Code);
    }

    [Fact]
    public async Task Edit_reports_nonzero_and_missing_editor_failures()
    {
        RequireUnix();
        Environment.SetEnvironmentVariable("VISUAL", "/usr/bin/false");
        var nonzero = await Assert.ThrowsAsync<TrackerException>(
            () => new SystemWorkItemTextEditor().EditAsync(
                "Title", "Body", CancellationToken.None));
        Assert.Equal("EDITOR_FAILED", nonzero.Code);

        Environment.SetEnvironmentVariable(
            "VISUAL", Path.Combine(root, "does-not-exist"));
        var missing = await Assert.ThrowsAsync<TrackerException>(
            () => new SystemWorkItemTextEditor().EditAsync(
                "Title", "Body", CancellationToken.None));
        Assert.Equal("EDITOR_FAILED", missing.Code);
    }

    [Fact]
    public async Task Edit_kills_editor_when_cancelled()
    {
        RequireUnix();
        var script = CreateScript("slow-editor", "sleep 30");
        Environment.SetEnvironmentVariable("VISUAL", script);
        using var cancellation = new CancellationTokenSource(TimeSpan.FromMilliseconds(100));

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => new SystemWorkItemTextEditor().EditAsync(
                "Title", "Body", cancellation.Token));
    }

    [Theory]
    [InlineData("not a title\n\n--- Wrighty Markdown body below this line ---\nBody")]
    [InlineData("Title: \n\n--- Wrighty Markdown body below this line ---\nBody")]
    [InlineData("Title: Valid\nBody without marker")]
    public void Parse_rejects_invalid_editor_content(string value)
    {
        var exception = Assert.Throws<TrackerException>(
            () => InvokeParse(value));

        Assert.Equal("EDITOR_CONTENT_INVALID", exception.Code);
    }

    private string CreateScript(string name, string body)
    {
        if (OperatingSystem.IsWindows())
            throw new PlatformNotSupportedException();
        var path = Path.Combine(root, name);
        File.WriteAllText(path, $"#!/bin/sh\nset -eu\n{body}\n");
        File.SetUnixFileMode(
            path,
            UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        return path;
    }

    private static void RequireUnix()
    {
        if (OperatingSystem.IsWindows())
            throw Xunit.Sdk.SkipException.ForSkip("Shell-based editor test requires Unix.");
    }

    private static EditedWorkItemText InvokeParse(string value)
    {
        var method = typeof(SystemWorkItemTextEditor).GetMethod(
            "Parse", BindingFlags.NonPublic | BindingFlags.Static)
            ?? throw new InvalidOperationException("Parse method was not found.");
        try
        {
            return (EditedWorkItemText)method.Invoke(null, [value])!;
        }
        catch (TargetInvocationException exception) when (exception.InnerException is not null)
        {
            throw exception.InnerException;
        }
    }

    public void Dispose()
    {
        Environment.SetEnvironmentVariable("VISUAL", visual);
        Environment.SetEnvironmentVariable("EDITOR", editor);
        try
        {
            Directory.Delete(root, recursive: true);
        }
        catch (DirectoryNotFoundException)
        {
        }
    }
}

[CollectionDefinition("Process environment", DisableParallelization = true)]
public sealed class ProcessEnvironmentCollection;
