using System.Diagnostics;
using Highbyte.Wrighty.Errors;

namespace Highbyte.Wrighty.Cli;

public sealed record EditedWorkItemText(string Title, string Body);

public interface IWorkItemTextEditor
{
    void Validate();

    Task<EditedWorkItemText> EditAsync(
        string title,
        string body,
        CancellationToken cancellationToken);
}

public sealed class SystemWorkItemTextEditor : IWorkItemTextEditor
{
    private const string BodyMarker = "--- Wrighty Markdown body below this line ---";

    public void Validate() => _ = ResolveCommand();

    public async Task<EditedWorkItemText> EditAsync(
        string title,
        string body,
        CancellationToken cancellationToken)
    {
        var commandParts = ResolveCommand();

        var path = Path.Combine(
            Path.GetTempPath(),
            $"wrighty-takeover-edit-{Guid.NewGuid():N}.md");
        try
        {
            await File.WriteAllTextAsync(
                path,
                $"Title: {title}{Environment.NewLine}{Environment.NewLine}" +
                $"{BodyMarker}{Environment.NewLine}{body}",
                cancellationToken);
            using var process = new Process
            {
                StartInfo = new ProcessStartInfo
                {
                    FileName = commandParts[0],
                    UseShellExecute = false
                }
            };
            foreach (var argument in commandParts.Skip(1))
                process.StartInfo.ArgumentList.Add(argument);
            process.StartInfo.ArgumentList.Add(path);
            try
            {
                if (!process.Start())
                    throw new TrackerException(
                        "EDITOR_FAILED",
                        $"Could not start editor '{commandParts[0]}'. The editing claim remains " +
                        "active, and the same edit command can be retried.",
                        7);
                await process.WaitForExitAsync(cancellationToken);
            }
            catch (OperationCanceledException)
            {
                if (!process.HasExited)
                    process.Kill(entireProcessTree: true);
                throw;
            }
            catch (Exception exception) when (
                exception is InvalidOperationException or System.ComponentModel.Win32Exception)
            {
                throw new TrackerException(
                    "EDITOR_FAILED",
                    $"Could not start editor '{commandParts[0]}': {exception.Message}. " +
                    "The editing claim remains active, and the same edit command can be retried.",
                    7,
                    innerException: exception);
            }

            if (process.ExitCode != 0)
            {
                throw new TrackerException(
                    "EDITOR_FAILED",
                    $"Editor '{commandParts[0]}' exited with code {process.ExitCode}. " +
                    "The editing claim remains active, but the item was not changed.",
                    7);
            }

            return Parse(await File.ReadAllTextAsync(path, cancellationToken));
        }
        finally
        {
            try { File.Delete(path); }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
    }

    internal static EditedWorkItemText Parse(string value)
    {
        using var reader = new StringReader(value);
        var titleLine = reader.ReadLine();
        if (titleLine is null || !titleLine.StartsWith("Title:", StringComparison.Ordinal))
        {
            throw new TrackerException(
                "EDITOR_CONTENT_INVALID",
                "The edited file must keep its first line in the form 'Title: <title>'. " +
                "The editing claim remains active, but the item was not changed.",
                2);
        }
        var title = titleLine["Title:".Length..].Trim();
        if (title.Length == 0 || title.Contains('\n') || title.Contains('\r'))
        {
            throw new TrackerException(
                "EDITOR_CONTENT_INVALID",
                "The edited work-item title must be a non-empty single line. " +
                "The editing claim remains active, but the item was not changed.",
                2);
        }

        var marker = $"{Environment.NewLine}{BodyMarker}{Environment.NewLine}";
        var markerIndex = value.IndexOf(marker, StringComparison.Ordinal);
        if (markerIndex < 0)
        {
            throw new TrackerException(
                "EDITOR_CONTENT_INVALID",
                $"The edited file must retain the '{BodyMarker}' separator. " +
                "The editing claim remains active, but the item was not changed.",
                2);
        }
        return new EditedWorkItemText(title, value[(markerIndex + marker.Length)..]);
    }

    private static IReadOnlyList<string> SplitCommand(string command) =>
        new CommandParser(command).Parse();

    private sealed class CommandParser(string command)
    {
        private readonly List<string> result = [];
        private readonly System.Text.StringBuilder current = new();
        char? quote = null;
        bool escaping;

        public IReadOnlyList<string> Parse()
        {
            foreach (var character in command)
                Consume(character);
            Finish();
            return result;
        }

        private void Consume(char character)
        {
            if (escaping)
            {
                current.Append(character);
                escaping = false;
                return;
            }
            if (character == '\\' && quote != '\'')
            {
                escaping = true;
                return;
            }
            if (quote is not null)
            {
                if (character == quote)
                    quote = null;
                else
                    current.Append(character);
                return;
            }
            if (character is '\'' or '"')
            {
                quote = character;
                return;
            }
            if (char.IsWhiteSpace(character))
            {
                Flush();
                return;
            }
            current.Append(character);
        }

        private void Finish()
        {
            if (escaping)
                current.Append('\\');
            if (quote is not null)
                throw new TrackerException(
                    "EDITOR_UNAVAILABLE",
                    "VISUAL or EDITOR contains an unmatched quote; no claim change was performed.",
                    2);
            Flush();
        }

        private void Flush()
        {
            if (current.Length == 0)
                return;
            result.Add(current.ToString());
            current.Clear();
        }
    }

    private static IReadOnlyList<string> ResolveCommand()
    {
        var command = Environment.GetEnvironmentVariable("VISUAL");
        if (string.IsNullOrWhiteSpace(command))
            command = Environment.GetEnvironmentVariable("EDITOR");
        if (string.IsNullOrWhiteSpace(command))
        {
            throw new TrackerException(
                "EDITOR_UNAVAILABLE",
                "Set VISUAL or EDITOR before interactive editing, or use --title, --body, " +
                "or --body-file for a non-interactive edit. No claim change was performed.",
                2);
        }
        var parts = SplitCommand(command);
        if (parts.Count == 0)
            throw new TrackerException(
                "EDITOR_UNAVAILABLE",
                "VISUAL or EDITOR is empty; no claim change was performed.",
                2);
        return parts;
    }
}
