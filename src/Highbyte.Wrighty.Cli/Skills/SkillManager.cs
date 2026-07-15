using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Highbyte.Wrighty.Errors;
using YamlDotNet.Serialization;

namespace Highbyte.Wrighty.Cli.Skills;

public enum SkillScope
{
    Project,
    User
}

public enum SkillInstallationState
{
    Missing,
    Current,
    Outdated,
    Modified,
    Malformed
}

public sealed record SkillOperationResult(
    string Agent,
    string Scope,
    string Path,
    SkillInstallationState PreviousState,
    SkillInstallationState State,
    bool Changed,
    string? PreviousVersion,
    string Version,
    bool DescriptionPreserved);

public interface ISkillManager
{
    Task<IReadOnlyList<SkillOperationResult>> InstallAsync(
        string agent,
        SkillScope scope,
        string workingDirectory,
        string? projectDirectory,
        bool force,
        CancellationToken cancellationToken);

    Task<IReadOnlyList<SkillOperationResult>> CheckAsync(
        string agent,
        SkillScope scope,
        string workingDirectory,
        string? projectDirectory,
        CancellationToken cancellationToken);

    Task<IReadOnlyList<SkillOperationResult>> UpdateAsync(
        string agent,
        SkillScope scope,
        string workingDirectory,
        string? projectDirectory,
        bool force,
        CancellationToken cancellationToken);
}

public sealed class SkillManager(string assetRoot, string userHome) : ISkillManager
{
    public const string SkillName = "wrighty";
    public const string SkillVersion = "0.1.0";
    private const string ManifestName = ".wrighty-skill.json";
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    public static SkillManager CreateDefault() => new(
        Path.Combine(AppContext.BaseDirectory, "skills", SkillName),
        Environment.GetFolderPath(Environment.SpecialFolder.UserProfile));

    public async Task<IReadOnlyList<SkillOperationResult>> InstallAsync(
        string agent,
        SkillScope scope,
        string workingDirectory,
        string? projectDirectory,
        bool force,
        CancellationToken cancellationToken)
    {
        EnsureAssets();
        var results = new List<SkillOperationResult>();
        foreach (var destination in Destinations(agent, scope, workingDirectory, projectDirectory))
        {
            var inspection = await InspectAsync(destination.Path, destination.Target, cancellationToken);
            if (inspection.State == SkillInstallationState.Current)
            {
                results.Add(Result(destination, inspection, inspection.State, false, false));
                continue;
            }

            if (inspection.State != SkillInstallationState.Missing)
            {
                throw new TrackerException(
                    inspection.State == SkillInstallationState.Modified
                        ? "SKILL_MODIFIED"
                        : "SKILL_ALREADY_EXISTS",
                    $"Skill destination '{destination.Path}' already exists with state {inspection.State}. " +
                    "Use skill update for a recognized installation.",
                    9,
                    new Dictionary<string, object?> { ["path"] = destination.Path });
            }

            await WriteInstallationAsync(destination, null, force, cancellationToken);
            results.Add(Result(
                destination,
                inspection,
                SkillInstallationState.Current,
                true,
                false));
        }

        return results;
    }

    public async Task<IReadOnlyList<SkillOperationResult>> CheckAsync(
        string agent,
        SkillScope scope,
        string workingDirectory,
        string? projectDirectory,
        CancellationToken cancellationToken)
    {
        EnsureAssets();
        var results = new List<SkillOperationResult>();
        foreach (var destination in Destinations(agent, scope, workingDirectory, projectDirectory))
        {
            var inspection = await InspectAsync(destination.Path, destination.Target, cancellationToken);
            results.Add(Result(destination, inspection, inspection.State, false, false));
        }

        return results;
    }

    public async Task<IReadOnlyList<SkillOperationResult>> UpdateAsync(
        string agent,
        SkillScope scope,
        string workingDirectory,
        string? projectDirectory,
        bool force,
        CancellationToken cancellationToken)
    {
        EnsureAssets();
        var results = new List<SkillOperationResult>();
        foreach (var destination in Destinations(agent, scope, workingDirectory, projectDirectory))
        {
            var inspection = await InspectAsync(destination.Path, destination.Target, cancellationToken);
            if (inspection.State == SkillInstallationState.Missing)
            {
                throw new TrackerException(
                    "SKILL_NOT_FOUND",
                    $"No installed {SkillName} skill was found at '{destination.Path}'.",
                    5,
                    new Dictionary<string, object?> { ["path"] = destination.Path });
            }
            if (inspection.Manifest is null || inspection.Description is null)
            {
                throw new TrackerException(
                    "SKILL_INVALID",
                    $"The skill at '{destination.Path}' is not a recognized installation.",
                    9);
            }
            if (inspection.State == SkillInstallationState.Modified && !force)
            {
                throw new TrackerException(
                    "SKILL_MODIFIED",
                    $"Tool-owned files at '{destination.Path}' were modified. Use --force to replace them.",
                    9,
                    new Dictionary<string, object?> { ["path"] = destination.Path });
            }

            if (inspection.State == SkillInstallationState.Current)
            {
                results.Add(Result(destination, inspection, inspection.State, false, true));
                continue;
            }

            await WriteInstallationAsync(destination, inspection.Description, force, cancellationToken);
            results.Add(Result(
                destination,
                inspection,
                SkillInstallationState.Current,
                true,
                true));
        }

        return results;
    }

    private async Task WriteInstallationAsync(
        SkillDestination destination,
        string? preservedDescription,
        bool force,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var parent = Path.GetDirectoryName(destination.Path)!;
        Directory.CreateDirectory(parent);
        var temporary = Path.Combine(parent, $".{SkillName}.{Guid.NewGuid():N}.tmp");
        var backup = Path.Combine(parent, $".{SkillName}.{Guid.NewGuid():N}.bak");
        try
        {
            await CopyDirectoryAsync(assetRoot, temporary, cancellationToken);
            if (preservedDescription is not null)
            {
                var skillPath = Path.Combine(temporary, "SKILL.md");
                var canonical = await File.ReadAllTextAsync(skillPath, cancellationToken);
                await File.WriteAllTextAsync(
                    skillPath,
                    ReplaceDescription(canonical, preservedDescription),
                    cancellationToken);
            }

            if (destination.Target == "claude")
            {
                var skillPath = Path.Combine(temporary, "SKILL.md");
                var skill = await File.ReadAllTextAsync(skillPath, cancellationToken);
                await File.WriteAllTextAsync(
                    skillPath,
                    AddClaudeInvocationPolicy(skill),
                    cancellationToken);
            }

            var hash = await MechanicsHashAsync(temporary, cancellationToken);
            var manifest = new SkillManifest(
                1,
                SkillName,
                SkillVersion,
                ThisAssemblyVersion(),
                destination.Target,
                hash);
            await File.WriteAllTextAsync(
                Path.Combine(temporary, ManifestName),
                JsonSerializer.Serialize(manifest, JsonOptions) + Environment.NewLine,
                cancellationToken);

            if (Directory.Exists(destination.Path))
            {
                Directory.Move(destination.Path, backup);
            }
            try
            {
                Directory.Move(temporary, destination.Path);
            }
            catch
            {
                if (Directory.Exists(backup) && !Directory.Exists(destination.Path))
                {
                    Directory.Move(backup, destination.Path);
                }
                throw;
            }

            if (Directory.Exists(backup))
            {
                Directory.Delete(backup, true);
            }
        }
        catch (Exception exception) when (
            exception is IOException or UnauthorizedAccessException or JsonException)
        {
            throw new TrackerException(
                "SKILL_WRITE_FAILED",
                $"Could not install the skill at '{destination.Path}': {exception.Message}",
                9,
                new Dictionary<string, object?> { ["path"] = destination.Path },
                exception);
        }
        finally
        {
            if (Directory.Exists(temporary)) Directory.Delete(temporary, true);
            if (Directory.Exists(backup) && Directory.Exists(destination.Path)) Directory.Delete(backup, true);
        }
    }

    private async Task<SkillInspection> InspectAsync(
        string path,
        string expectedTarget,
        CancellationToken cancellationToken)
    {
        if (File.Exists(path))
        {
            return new SkillInspection(SkillInstallationState.Malformed, null, null);
        }
        if (!Directory.Exists(path))
        {
            return new SkillInspection(SkillInstallationState.Missing, null, null);
        }
        if (new DirectoryInfo(path).LinkTarget is not null)
        {
            return new SkillInspection(SkillInstallationState.Malformed, null, null);
        }

        try
        {
            var manifestPath = Path.Combine(path, ManifestName);
            var skillPath = Path.Combine(path, "SKILL.md");
            if (!File.Exists(manifestPath) || !File.Exists(skillPath))
            {
                return new SkillInspection(SkillInstallationState.Malformed, null, null);
            }

            var manifest = JsonSerializer.Deserialize<SkillManifest>(
                await File.ReadAllTextAsync(manifestPath, cancellationToken),
                JsonOptions);
            if (manifest is null || manifest.SkillName != SkillName || manifest.Target != expectedTarget)
            {
                return new SkillInspection(SkillInstallationState.Malformed, manifest, null);
            }

            var description = ParseDescription(await File.ReadAllTextAsync(skillPath, cancellationToken));
            var actualHash = await MechanicsHashAsync(path, cancellationToken);
            if (!string.Equals(actualHash, manifest.MechanicsSha256, StringComparison.Ordinal))
            {
                return new SkillInspection(SkillInstallationState.Modified, manifest, description);
            }

            var state = manifest.SkillVersion == SkillVersion && manifest.CliVersion == ThisAssemblyVersion()
                ? SkillInstallationState.Current
                : SkillInstallationState.Outdated;
            return new SkillInspection(state, manifest, description);
        }
        catch (Exception exception) when (
            exception is IOException or UnauthorizedAccessException or JsonException or YamlDotNet.Core.YamlException)
        {
            return new SkillInspection(SkillInstallationState.Malformed, null, null);
        }
    }

    private IReadOnlyList<SkillDestination> Destinations(
        string agent,
        SkillScope scope,
        string workingDirectory,
        string? projectDirectory)
    {
        var normalized = agent.Trim().ToLowerInvariant();
        if (normalized == "auto")
        {
            throw new TrackerException(
                "SKILL_AGENT_REQUIRED",
                "Automatic agent detection did not identify one runtime. Use --agent codex, claude, copilot, or all.",
                2);
        }
        if (normalized is not ("codex" or "claude" or "copilot" or "all"))
        {
            throw new TrackerException("ARGUMENT_INVALID", $"Unsupported skill agent '{agent}'.", 2);
        }

        var root = scope == SkillScope.User
            ? userHome
            : Path.GetFullPath(projectDirectory ?? FindProjectRoot(workingDirectory));
        var result = new List<SkillDestination>();
        if (normalized is "codex" or "copilot" or "all")
        {
            result.Add(new SkillDestination(
                normalized == "all" ? "codex-copilot" : normalized,
                "codex-copilot",
                scope,
                Path.Combine(root, ".agents", "skills", SkillName)));
        }
        if (normalized is "claude" or "all")
        {
            result.Add(new SkillDestination(
                "claude",
                "claude",
                scope,
                Path.Combine(root, ".claude", "skills", SkillName)));
        }
        return result;
    }

    private void EnsureAssets()
    {
        if (!File.Exists(Path.Combine(assetRoot, "SKILL.md")))
        {
            throw new TrackerException(
                "SKILL_ASSETS_MISSING",
                $"Packaged skill assets were not found at '{assetRoot}'.",
                9);
        }
    }

    private static async Task CopyDirectoryAsync(
        string source,
        string destination,
        CancellationToken cancellationToken)
    {
        foreach (var directory in Directory.EnumerateDirectories(source, "*", SearchOption.AllDirectories))
        {
            Directory.CreateDirectory(Path.Combine(destination, Path.GetRelativePath(source, directory)));
        }
        Directory.CreateDirectory(destination);
        foreach (var file in Directory.EnumerateFiles(source, "*", SearchOption.AllDirectories))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var target = Path.Combine(destination, Path.GetRelativePath(source, file));
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            await using var input = File.OpenRead(file);
            await using var output = new FileStream(target, FileMode.CreateNew, FileAccess.Write, FileShare.None);
            await input.CopyToAsync(output, cancellationToken);
        }
    }

    private static async Task<string> MechanicsHashAsync(string root, CancellationToken cancellationToken)
    {
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        foreach (var file in Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories)
                     .Where(path => Path.GetFileName(path) != ManifestName)
                     .OrderBy(path => Path.GetRelativePath(root, path), StringComparer.Ordinal))
        {
            var relative = Path.GetRelativePath(root, file).Replace(Path.DirectorySeparatorChar, '/');
            hash.AppendData(Encoding.UTF8.GetBytes(relative + "\n"));
            var content = await File.ReadAllTextAsync(file, cancellationToken);
            if (relative == "SKILL.md")
            {
                content = ReplaceDescription(content, "<preserved-description>");
            }
            hash.AppendData(Encoding.UTF8.GetBytes(content));
        }
        return Convert.ToHexStringLower(hash.GetHashAndReset());
    }

    private static string ParseDescription(string skill)
    {
        var end = skill.IndexOf("\n---", 4, StringComparison.Ordinal);
        if (!skill.StartsWith("---\n", StringComparison.Ordinal) || end < 0)
        {
            throw new YamlDotNet.Core.YamlException("SKILL.md frontmatter is missing.");
        }
        var yaml = skill[4..end];
        var values = new DeserializerBuilder().Build().Deserialize<Dictionary<string, object?>>(yaml);
        if (!values.TryGetValue("name", out var name) || name?.ToString() != SkillName ||
            !values.TryGetValue("description", out var description) ||
            string.IsNullOrWhiteSpace(description?.ToString()))
        {
            throw new YamlDotNet.Core.YamlException("SKILL.md frontmatter is invalid.");
        }
        return description!.ToString()!;
    }

    private static string ReplaceDescription(string skill, string description)
    {
        var end = skill.IndexOf("\n---", 4, StringComparison.Ordinal);
        if (!skill.StartsWith("---\n", StringComparison.Ordinal) || end < 0)
        {
            throw new YamlDotNet.Core.YamlException("SKILL.md frontmatter is missing.");
        }
        var body = skill[(end + 4)..];
        var quoted = JsonSerializer.Serialize(description);
        return $"---\nname: {SkillName}\ndescription: {quoted}\n---{body}";
    }

    private static string AddClaudeInvocationPolicy(string skill)
    {
        if (skill.Contains("\ndisable-model-invocation:", StringComparison.Ordinal))
        {
            return skill;
        }
        var end = skill.IndexOf("\n---", 4, StringComparison.Ordinal);
        if (!skill.StartsWith("---\n", StringComparison.Ordinal) || end < 0)
        {
            throw new YamlDotNet.Core.YamlException("SKILL.md frontmatter is missing.");
        }
        return skill.Insert(end, "\ndisable-model-invocation: true");
    }

    private static string FindProjectRoot(string workingDirectory)
    {
        var directory = new DirectoryInfo(Path.GetFullPath(workingDirectory));
        while (directory is not null)
        {
            if (Directory.Exists(Path.Combine(directory.FullName, ".git")) ||
                File.Exists(Path.Combine(directory.FullName, ".git")))
            {
                return directory.FullName;
            }
            directory = directory.Parent;
        }
        return Path.GetFullPath(workingDirectory);
    }

    private static string ThisAssemblyVersion() =>
        typeof(SkillManager).Assembly.GetName().Version?.ToString(3) ?? SkillVersion;

    private static SkillOperationResult Result(
        SkillDestination destination,
        SkillInspection inspection,
        SkillInstallationState state,
        bool changed,
        bool preserved) => new(
        destination.Agent,
        destination.Scope.ToString().ToLowerInvariant(),
        destination.Path,
        inspection.State,
        state,
        changed,
        inspection.Manifest?.SkillVersion,
        SkillVersion,
        preserved);

    private sealed record SkillDestination(string Agent, string Target, SkillScope Scope, string Path);
    private sealed record SkillInspection(
        SkillInstallationState State,
        SkillManifest? Manifest,
        string? Description);
    private sealed record SkillManifest(
        int SchemaVersion,
        string SkillName,
        string SkillVersion,
        string CliVersion,
        string Target,
        string MechanicsSha256);
}
