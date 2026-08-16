using System.Security.Cryptography;
using System.Text;
using Highbyte.Wrighty.Caching;
using Highbyte.Wrighty.Configuration;
using Highbyte.Wrighty.LocalMarkdown;
using Highbyte.Wrighty.Workers;

namespace Highbyte.Wrighty.Storage;

public enum StorageLocationKind
{
    Directory,
    File,
    Pattern
}

public enum StorageLifecycle
{
    Container,
    RepositoryConfiguration,
    RepositoryContent,
    UserConfiguration,
    RuntimeState,
    Cache,
    Credential,
    Temporary,
    Legacy
}

/// <summary>
/// One path Wrighty owns or may create. Paths are inspection data only: the catalogue never
/// creates, reads, migrates, or deletes the location it describes.
/// </summary>
public sealed record StorageLocationDescriptor(
    string Id,
    string Name,
    string Path,
    StorageLocationKind Kind,
    StorageLifecycle Lifecycle,
    string AppliesTo,
    string Source,
    bool? Exists,
    bool Sensitive,
    string Description)
{
    public string LifecycleLabel => Lifecycle switch
    {
        StorageLifecycle.Container => "Mixed-lifecycle container",
        StorageLifecycle.RepositoryConfiguration => "Repository configuration",
        StorageLifecycle.RepositoryContent => "Repository content",
        StorageLifecycle.UserConfiguration => "User configuration",
        StorageLifecycle.RuntimeState => "Machine-local runtime state",
        StorageLifecycle.Cache => "Regenerable cache",
        StorageLifecycle.Credential => "Credential",
        StorageLifecycle.Temporary => "Temporary/lock",
        StorageLifecycle.Legacy => "Legacy",
        _ => Lifecycle.ToString()
    };
}

/// <summary>
/// Resolves the filesystem footprint from the same effective repository and cache paths used by
/// the process. This is deliberately a read-only catalogue, not a second configuration authority.
/// </summary>
public sealed class StorageLocationCatalog(CachePaths cachePaths)
{
    private const string GitHubBackend = "github";
    private const string LocalMarkdownBackend = "local-markdown";
    private const string LocalMarkdownPathSource = "localMarkdown.path";

    public IReadOnlyList<StorageLocationDescriptor> Describe(
        string repositoryConfigurationPath,
        TrackerConfig? configuration,
        string userSettingsPath,
        string? legacyUserSettingsPath = null)
    {
        var repositoryPath = Path.GetFullPath(repositoryConfigurationPath);
        var repositoryRoot = Path.GetDirectoryName(repositoryPath)
            ?? Environment.CurrentDirectory;
        var userPath = Path.GetFullPath(userSettingsPath);
        var legacyUserPath = Path.GetFullPath(
            legacyUserSettingsPath ??
            Path.Combine(Path.GetDirectoryName(userPath)!, "settings-v1.json"));
        var cacheRoot = Path.GetFullPath(cachePaths.Root);
        var managedTokenPath = WebTokenStoragePaths.ManagedTokenPath(repositoryRoot);

        var locations = new List<StorageLocationDescriptor>
        {
            File(
                "repository.configuration",
                "Repository configuration",
                repositoryPath,
                StorageLifecycle.RepositoryConfiguration,
                new("all", "repository discovery, --config, or WRIGHTY_CONFIG_PATH"),
                sensitive: false,
                "Shared tracker identity and policy; normally committed."),
            File(
                "repository.configuration.edit-lock",
                "Repository configuration edit lock",
                $"{repositoryPath}.edit.lock",
                StorageLifecycle.Temporary,
                new("all", "typed repository configuration edit"),
                sensitive: false,
                "Process lock preventing concurrent typed configuration edits; removed when the edit completes."),
            Pattern(
                "repository.configuration.temporary",
                "Repository configuration atomic writes",
                Path.Combine(
                    Path.GetDirectoryName(repositoryPath)!,
                    $".{Path.GetFileName(repositoryPath)}.<guid>.tmp"),
                StorageLifecycle.Temporary,
                new("all", "repository configuration save"),
                sensitive: false,
                "Temporary files removed after an atomic repository configuration write."),
            File(
                "user.settings",
                "User settings",
                userPath,
                StorageLifecycle.UserConfiguration,
                new("all", "platform default or WRIGHTY_CONFIG_DIR"),
                sensitive: false,
                "Authoritative host label and execution-profile mappings; never committed."),
            File(
                "user.settings.legacy",
                "Legacy user settings",
                legacyUserPath,
                StorageLifecycle.Legacy,
                new("all", "platform default or WRIGHTY_CONFIG_DIR"),
                sensitive: false,
                "Version-1 user settings retained for downgrade compatibility after migration."),
            Pattern(
                "user.settings.temporary",
                "User-settings atomic writes",
                $"{userPath}.<guid>.tmp",
                StorageLifecycle.Temporary,
                new("all", "settings-v2.json write"),
                sensitive: false,
                "Temporary files removed after an atomic settings write."),
            Directory(
                "cache.root",
                "Installation cache root",
                cacheRoot,
                StorageLifecycle.Container,
                new("all", cachePaths.RootSource),
                sensitive: false,
                "Parent for installation identity, GitHub metadata, worker status, provider state, and handoff artifacts."),
            CacheFile("cache.nodes", "GitHub node metadata", cachePaths.NodeCachePath,
                GitHubBackend, "Regenerable Project, field, and option node IDs."),
            File(
                "cache.identity",
                "Installation identity",
                Full(cachePaths.IdentityPath),
                StorageLifecycle.RuntimeState,
                new("all", cachePaths.RootSource),
                sensitive: false,
                "Generated UUID used to derive Wrighty's privacy-preserving installation ID; deleting it changes installation identity."),
            File(
                "cache.work-items",
                "Work-item runtime state",
                Full(cachePaths.WorkItemRuntimePath),
                StorageLifecycle.RuntimeState,
                new(GitHubBackend, cachePaths.RootSource),
                sensitive: true,
                "Recorded sessions, workspaces, failures, and deferred dispatch decisions; not reconstructable from GitHub."),
            CacheFile("cache.provider-capacity", "Provider capacity state", cachePaths.ProviderCapacityPath,
                "all", "Sanitized provider capacity, cooldowns, and probe leases."),
            File(
                "cache.provider-capacity-lock",
                "Provider capacity lock",
                Full(cachePaths.ProviderCapacityLockPath),
                StorageLifecycle.Temporary,
                new("all", cachePaths.RootSource),
                sensitive: false,
                "Cross-process lock protecting provider-capacity updates."),
            Directory(
                "cache.worker-instances",
                "Worker-instance registry",
                Full(cachePaths.WorkerInstancesRoot),
                StorageLifecycle.RuntimeState,
                new("all", cachePaths.RootSource),
                sensitive: true,
                "Per-process heartbeat records used for liveness and configuration-drift reporting."),
            Pattern(
                "cache.worker-instance-files",
                "Worker-instance records",
                Path.Combine(
                    Full(cachePaths.WorkerInstancesRoot),
                    "<configuration-hash>",
                    "<run-id>.json"),
                StorageLifecycle.RuntimeState,
                new("all", cachePaths.RootSource),
                sensitive: true,
                "Heartbeat and configuration-revision record for one running worker process."),
            Directory(
                "cache.handoffs",
                "Handoff artifacts",
                Full(cachePaths.HandoffRoot),
                StorageLifecycle.RuntimeState,
                new("all", cachePaths.RootSource),
                sensitive: true,
                "Rendered cross-agent handoff packets for local operator inspection."),
            Pattern(
                "cache.handoff-files",
                "Handoff packets",
                Path.Combine(Full(cachePaths.HandoffRoot), "<work-item>-<hash>.md"),
                StorageLifecycle.RuntimeState,
                new("all", cachePaths.RootSource),
                sensitive: true,
                "Latest rendered cross-agent handoff packet for one work item."),
            Directory(
                "cache.copilot-shares",
                "Copilot session exports",
                Full(cachePaths.CopilotSharesRoot),
                StorageLifecycle.RuntimeState,
                new("all", cachePaths.RootSource),
                sensitive: true,
                "Worker-owned Copilot Markdown exports used as handoff context."),
            Pattern(
                "cache.copilot-share-files",
                "Copilot session export files",
                Path.Combine(Full(cachePaths.CopilotSharesRoot), "<session-id>.md"),
                StorageLifecycle.RuntimeState,
                new("all", cachePaths.RootSource),
                sensitive: true,
                "Worker-owned export of one Copilot session used as handoff context."),
            File(
                "cache.sessions.legacy",
                "Legacy session cache",
                Full(cachePaths.LegacySessionPath),
                StorageLifecycle.Legacy,
                new(GitHubBackend, cachePaths.RootSource),
                sensitive: true,
                "Older session records read only for migration into work-item-runtime-v1.json."),
            File(
                "cache.provider-availability.legacy",
                "Legacy provider availability cache",
                Full(cachePaths.LegacyProviderAvailabilityPath),
                StorageLifecycle.Legacy,
                new("all", cachePaths.RootSource),
                sensitive: false,
                "Older provider circuit state read only for migration into provider-capacity-v1.json."),
            Pattern(
                "cache.atomic-temporary",
                "Cache atomic writes",
                Path.Combine(cacheRoot, "*.tmp"),
                StorageLifecycle.Temporary,
                new("all", cachePaths.RootSource),
                sensitive: false,
                "Temporary files removed after atomic cache and runtime-state writes."),
            Directory(
                "runtime.workspace-locks",
                "Workspace execution locks",
                FileWorkspaceExecutionLock.DefaultRoot,
                StorageLifecycle.Temporary,
                new("all", "operating-system temporary directory"),
                sensitive: true,
                "Per-user lock files preventing concurrent workers from using one workspace."),
            File(
                "web.managed-token",
                "Managed web token",
                managedTokenPath,
                StorageLifecycle.Credential,
                new("all", "--persist-token platform default"),
                sensitive: true,
                "Bearer credential created only when the web server runs with --persist-token; its value is never displayed."),
            File(
                "web.managed-token-lock",
                "Managed web-token lock",
                $"{managedTokenPath}.lock",
                StorageLifecycle.Temporary,
                new("all", "--persist-token platform default"),
                sensitive: true,
                "Cross-process lock protecting creation and rotation of the managed token."),
            Pattern(
                "web.managed-token-temporary",
                "Managed web-token atomic writes",
                Path.Combine(
                    Path.GetDirectoryName(managedTokenPath)!,
                    ".token.<guid>.tmp"),
                StorageLifecycle.Temporary,
                new("all", "--persist-token platform default"),
                sensitive: true,
                "Temporary credential files removed after token creation or rotation.")
        };

        if (configuration?.Backend == LocalMarkdownBackend && configuration.LocalMarkdown is { } local)
        {
            var storeRoot = Path.GetFullPath(local.Path, repositoryRoot);
            locations.AddRange(
            [
                Directory(
                    "local.store",
                    "Local Markdown store",
                    storeRoot,
                    StorageLifecycle.Container,
                    new(LocalMarkdownBackend, LocalMarkdownPathSource),
                    sensitive: false,
                    "Parent containing authoritative work-item documents and machine-local runtime state."),
                Directory(
                    "local.items",
                    "Active Local Markdown items",
                    Path.Combine(storeRoot, "items"),
                    StorageLifecycle.RepositoryContent,
                    new(LocalMarkdownBackend, LocalMarkdownPathSource),
                    sensitive: false,
                    "Authoritative active work-item Markdown; normally committed."),
                Directory(
                    "local.archive",
                    "Archived Local Markdown items",
                    Path.Combine(storeRoot, "archive"),
                    StorageLifecycle.RepositoryContent,
                    new(LocalMarkdownBackend, LocalMarkdownPathSource),
                    sensitive: false,
                    "Authoritative archived work-item Markdown; normally committed."),
                File(
                    "local.runtime",
                    "Local Markdown runtime state",
                    LocalRuntimeStateStore.PathFor(storeRoot),
                    StorageLifecycle.RuntimeState,
                    new(LocalMarkdownBackend, LocalMarkdownPathSource),
                    sensitive: true,
                    "Authoritative local claims and durable session records; ignored by Git."),
                File(
                    "local.lock",
                    "Local Markdown store lock",
                    Path.Combine(storeRoot, ".lock"),
                    StorageLifecycle.Temporary,
                    new(LocalMarkdownBackend, LocalMarkdownPathSource),
                    sensitive: false,
                    "Store-wide process lock; ignored by Git."),
                File(
                    "local.gitignore",
                    "Local Markdown ignore rules",
                    Path.Combine(storeRoot, ".gitignore"),
                    StorageLifecycle.RepositoryContent,
                    new(LocalMarkdownBackend, LocalMarkdownPathSource),
                    sensitive: false,
                    "Generated ignore rules for runtime, lock, and atomic temporary files; normally committed."),
                Pattern(
                    "local.atomic-temporary",
                    "Local Markdown atomic writes",
                    Path.Combine(storeRoot, ".*.tmp"),
                    StorageLifecycle.Temporary,
                    new(LocalMarkdownBackend, LocalMarkdownPathSource),
                    sensitive: false,
                    "Interrupted atomic work-item and sidecar writes; ignored by Git and normally removed automatically.")
            ]);
        }

        if (configuration?.Backend == GitHubBackend)
        {
            var issueTemplateRoot = Path.Combine(repositoryRoot, ".github", "ISSUE_TEMPLATE");
            locations.AddRange(
            [
                File(
                    "github.issue-form",
                    "Wrighty GitHub Issue Form",
                    Path.Combine(issueTemplateRoot, "wrighty-task.yml"),
                    StorageLifecycle.RepositoryContent,
                    new(GitHubBackend, "wrighty init"),
                    sensitive: false,
                    "Generated task form for the configured GitHub Project; committed when published."),
                File(
                    "github.issue-template-config",
                    "GitHub issue-template chooser",
                    Path.Combine(issueTemplateRoot, "config.yml"),
                    StorageLifecycle.RepositoryContent,
                    new(GitHubBackend, "wrighty init"),
                    sensitive: false,
                    "Generated chooser configuration when Wrighty can manage the path safely."),
                Directory(
                    "github.import-manifests",
                    "Whole-store import manifests",
                    Path.Combine(repositoryRoot, ".wrighty-imports"),
                    StorageLifecycle.RuntimeState,
                    new(GitHubBackend, "wrighty import --to-backend github"),
                    sensitive: false,
                    "Durable retry manifests for Local Markdown to GitHub imports."),
                Pattern(
                    "github.import-manifest-files",
                    "Whole-store import manifest files",
                    Path.Combine(
                        repositoryRoot,
                        ".wrighty-imports",
                        "local-markdown-to-<repository>-project-<number>.json"),
                    StorageLifecycle.RuntimeState,
                    new(GitHubBackend, "wrighty import --to-backend github"),
                    sensitive: true,
                    "Retry-safe creation attempts and source-to-destination mappings for one import.")
            ]);
        }

        if (configuration is not null)
        {
            locations.Add(Directory(
                "worker.worktrees",
                "Worker worktree root",
                GitWorkspaceManager.ResolveWorktreeRoot(
                    configuration.EffectiveWorker,
                    repositoryRoot),
                StorageLifecycle.RuntimeState,
                new("all", "worker.worktreeRoot or Wrighty default"),
                sensitive: true,
                "Parent for retained and active worker Git worktrees when workspace mode is worktree."));
        }

        return locations;
    }

    private StorageLocationDescriptor CacheFile(
        string id,
        string name,
        string path,
        string appliesTo,
        string description) =>
        File(
            id,
            name,
            Full(path),
            StorageLifecycle.Cache,
            new(appliesTo, cachePaths.RootSource),
            sensitive: false,
            description);

    private static StorageLocationDescriptor Directory(
        string id,
        string name,
        string path,
        StorageLifecycle lifecycle,
        StorageLocationContext context,
        bool sensitive,
        string description) =>
        new(
            id,
            name,
            Full(path),
            StorageLocationKind.Directory,
            lifecycle,
            context.AppliesTo,
            context.Source,
            System.IO.Directory.Exists(Full(path)),
            sensitive,
            description);

    private static StorageLocationDescriptor File(
        string id,
        string name,
        string path,
        StorageLifecycle lifecycle,
        StorageLocationContext context,
        bool sensitive,
        string description) =>
        new(
            id,
            name,
            Full(path),
            StorageLocationKind.File,
            lifecycle,
            context.AppliesTo,
            context.Source,
            System.IO.File.Exists(Full(path)),
            sensitive,
            description);

    private static StorageLocationDescriptor Pattern(
        string id,
        string name,
        string path,
        StorageLifecycle lifecycle,
        StorageLocationContext context,
        bool sensitive,
        string description) =>
        new(
            id,
            name,
            path,
            StorageLocationKind.Pattern,
            lifecycle,
            context.AppliesTo,
            context.Source,
            Exists: null,
            sensitive,
            description);

    private static string Full(string path) => Path.GetFullPath(path);

    private sealed record StorageLocationContext(string AppliesTo, string Source);
}

/// <summary>Path convention for managed persistent web credentials. Kept outside the Web project
/// so inspection and authentication cannot drift to different locations.</summary>
public static class WebTokenStoragePaths
{
    public static string ManagedRoot => OperatingSystem.IsWindows()
        ? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "Wrighty",
            "webui")
        : Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            ".wrighty",
            "webui");

    public static string ManagedTokenPath(string trackerRoot, string? managedRoot = null)
    {
        var fullRoot = Path.GetFullPath(trackerRoot);
        var canonicalRoot = fullRoot.Length == Path.GetPathRoot(fullRoot)?.Length
            ? fullRoot
            : fullRoot.TrimEnd(
                Path.DirectorySeparatorChar,
                Path.AltDirectorySeparatorChar);
        var hashInput = OperatingSystem.IsWindows()
            ? canonicalRoot.ToUpperInvariant()
            : canonicalRoot;
        var hash = Convert.ToHexStringLower(
            SHA256.HashData(Encoding.UTF8.GetBytes(hashInput)))[..12];
        var slug = Slug(Path.GetFileName(canonicalRoot));
        return Path.Combine(managedRoot ?? ManagedRoot, $"{slug}-{hash}", "token");
    }

    private static string Slug(string value)
    {
        var characters = value
            .Select(character =>
                char.IsAsciiLetterOrDigit(character) || character is '-' or '_' or '.'
                    ? char.ToLowerInvariant(character)
                    : '-')
            .ToArray();
        var slug = new string(characters).Trim('-', '.', '_');
        return slug.Length == 0 ? "tracker" : slug;
    }
}
