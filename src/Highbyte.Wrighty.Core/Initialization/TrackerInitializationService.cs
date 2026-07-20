using Highbyte.Wrighty.Configuration;
using Highbyte.Wrighty.Errors;
using Highbyte.Wrighty.Projects;
using Highbyte.Wrighty.Backends;
using Highbyte.Wrighty.Models;

namespace Highbyte.Wrighty.Initialization;

public sealed record TrackerInitializationRequest(
    string? Repository,
    string? GitHubHost,
    string? Remote,
    string? ProjectOwner,
    int? ProjectNumber,
    string? ProjectTitle,
    bool NoLinkRepository,
    bool NoLinkRepositorySpecified,
    string? ConfigPath,
    bool CheckOnly,
    string? Backend = null,
    string? LocalPath = null,
    IReadOnlyList<string>? Statuses = null,
    IReadOnlyList<string>? Priorities = null,
    bool CreateView = false,
    bool SkipIssueForms = false);

public sealed record TrackerInitializationPlan(
    string Backend,
    string BackendSelection,
    string ConfigPath,
    bool CreateConfiguration,
    string? Repository,
    string? ProjectOwner,
    int? ProjectNumber,
    string ProjectTitle,
    bool CreateProject,
    bool LinkRepository,
    bool CreateView,
    bool CreateIssueForms,
    string? LocalStorePath,
    IReadOnlyList<string> Steps,
    IReadOnlyList<string> ManualFollowUp);

public delegate Task TrackerInitializationApproval(
    TrackerInitializationPlan plan,
    CancellationToken cancellationToken);

public sealed record TrackerInitializationResult(
    TrackerConfig Config,
    string ConfigPath,
    string ProjectTitle,
    string ProjectUrl,
    bool CreatedProject,
    bool LinkedRepository,
    bool Changed,
    IReadOnlyList<string> Actions,
    string BackendSelection = "configured");

public interface ITrackerInitializationService
{
    Task<TrackerInitializationResult> InitializeAsync(
        string workingDirectory,
        TrackerInitializationRequest request,
        TrackerInitializationApproval? approval,
        CancellationToken cancellationToken);
}

public sealed class TrackerInitializationService(
    ITrackerConfigStore configStore,
    IRepositoryDiscovery repositoryDiscovery,
    IGitHubInitializationClient github,
    IProjectClient projects,
    ITrackerBackendRegistry? backends = null) : ITrackerInitializationService
{
    public Task<TrackerInitializationResult> InitializeAsync(
        string workingDirectory,
        TrackerInitializationRequest request,
        CancellationToken cancellationToken) =>
        InitializeAsync(workingDirectory, request, null, cancellationToken);

    public async Task<TrackerInitializationResult> InitializeAsync(
        string workingDirectory,
        TrackerInitializationRequest request,
        TrackerInitializationApproval? approval,
        CancellationToken cancellationToken)
    {
        ValidateArguments(request);
        var configPath = configStore.ResolvePath(workingDirectory, request.ConfigPath);
        var existing = await configStore.TryLoadPathAsync(configPath, cancellationToken);
        var selection = await SelectBackendAsync(
            workingDirectory,
            existing,
            request,
            cancellationToken);

        if (string.Equals(selection.Backend, "local-markdown", StringComparison.OrdinalIgnoreCase))
        {
            return await InitializeLocalAsync(
                configPath,
                existing,
                request,
                selection.Source,
                approval,
                cancellationToken);
        }

        EnsureGitHubBackend(selection.Backend, request);
        return await InitializeGitHubAsync(
            workingDirectory,
            configPath,
            existing,
            request,
            selection,
            approval,
            cancellationToken);
    }

    private async Task<BackendSelection> SelectBackendAsync(
        string workingDirectory,
        TrackerConfig? existing,
        TrackerInitializationRequest request,
        CancellationToken cancellationToken)
    {
        var backend = existing?.Backend ?? request.Backend;
        DiscoveredGitHubRepository? discovered = null;
        if (backend is null && request.Repository is null && request.LocalPath is null)
        {
            discovered = await repositoryDiscovery.DiscoverAsync(
                workingDirectory,
                request.Remote ?? "origin",
                cancellationToken);
        }

        backend ??= InferBackend(request, discovered);
        return new BackendSelection(
            backend,
            BackendSelectionSource(existing, request, discovered),
            discovered);
    }

    private static string InferBackend(
        TrackerInitializationRequest request,
        DiscoveredGitHubRepository? discovered)
    {
        if (HasLocalOptions(request))
        {
            return "local-markdown";
        }

        return request.Repository is not null || discovered is not null
            ? "github"
            : "local-markdown";
    }

    private static string BackendSelectionSource(
        TrackerConfig? existing,
        TrackerInitializationRequest request,
        DiscoveredGitHubRepository? discovered)
    {
        if (existing is not null)
        {
            return "configured";
        }

        if (request.Backend is not null)
        {
            return "explicit";
        }

        return request.Repository is not null || HasLocalOptions(request) || discovered is not null
            ? "inferred"
            : "defaulted";
    }

    private static bool HasLocalOptions(TrackerInitializationRequest request) =>
        request.LocalPath is not null || request.Statuses is not null || request.Priorities is not null;

    private static void EnsureGitHubBackend(
        string selectedBackend,
        TrackerInitializationRequest request)
    {
        if (!string.Equals(selectedBackend, "github", StringComparison.OrdinalIgnoreCase))
        {
            throw new TrackerException(
                "BACKEND_UNSUPPORTED",
                $"Unsupported backend '{selectedBackend}'. Available backends are 'github' and 'local-markdown'.",
                3);
        }

        if (HasLocalOptions(request))
        {
            throw new TrackerException(
                "OPTION_BACKEND_MISMATCH",
                "Local Markdown initialization options cannot be used with the github backend.",
                2);
        }
    }

    private async Task<TrackerInitializationResult> InitializeGitHubAsync(
        string workingDirectory,
        string configPath,
        TrackerConfig? existing,
        TrackerInitializationRequest request,
        BackendSelection selection,
        TrackerInitializationApproval? approval,
        CancellationToken cancellationToken)
    {
        var seed = await ResolveGitHubSeedAsync(
            workingDirectory,
            configPath,
            existing,
            request,
            selection.DiscoveredRepository,
            cancellationToken);

        var repositoryInfo = await github.GetRepositoryAsync(
            seed.Config.GitHubHost,
            seed.Config.Repository,
            cancellationToken);
        var projectOwner = request.ProjectOwner ?? existing?.EffectiveProjectOwner ?? repositoryInfo.Owner;
        var linkRepository = existing?.LinkRepository ?? !request.NoLinkRepository;
        if (!string.Equals(projectOwner, repositoryInfo.Owner, StringComparison.OrdinalIgnoreCase))
        {
            linkRepository = false;
        }

        var projectPlan = await ResolveProjectAsync(
            seed,
            existing,
            request,
            repositoryInfo,
            projectOwner,
            cancellationToken);

        var plan = BuildGitHubPlan(
            configPath,
            existing,
            request,
            selection.Source,
            repositoryInfo,
            projectOwner,
            linkRepository,
            projectPlan);
        await ApproveAsync(plan, request, approval, cancellationToken);

        var projectResolution = projectPlan.Project is not null
            ? new ProjectResolution(projectPlan.Project, false)
            : new ProjectResolution(
                await github.CreateProjectAsync(
                    seed.Config.GitHubHost,
                    projectOwner,
                    projectPlan.Title,
                    cancellationToken),
                true);

        var config = existing ?? seed.Config with
        {
            Repository = repositoryInfo.NameWithOwner,
            ProjectOwner = projectOwner,
            ProjectNumber = projectResolution.Project.Number,
            LinkRepository = linkRepository
        };
        return await CompleteGitHubInitializationAsync(
            configPath,
            config,
            existing is null,
            request,
            repositoryInfo,
            projectResolution,
            linkRepository,
            projectOwner,
            selection.Source,
            cancellationToken);
    }

    private async Task<GitHubSeed> ResolveGitHubSeedAsync(
        string workingDirectory,
        string configPath,
        TrackerConfig? existing,
        TrackerInitializationRequest request,
        DiscoveredGitHubRepository? discoveredForSelection,
        CancellationToken cancellationToken)
    {
        if (existing is not null)
        {
            AssertExistingConfiguration(existing, request, configPath);
            return new GitHubSeed(existing, null);
        }

        var discovered = discoveredForSelection ?? await DiscoverRepositoryIfNeededAsync(
            workingDirectory,
            request,
            cancellationToken);
        if (request.Repository is null && discovered is null)
        {
            throw RepositoryRequired(configPath);
        }

        EnsureCompatibleHost(request.GitHubHost, discovered);
        var repository = request.Repository ?? discovered!.Repository;
        ValidateRepository(repository);
        return new GitHubSeed(
            new TrackerConfig
            {
                Repository = repository,
                ProjectOwner = request.ProjectOwner,
                ProjectNumber = request.ProjectNumber ?? 1,
                LinkRepository = !request.NoLinkRepository,
                GitHubHost = request.GitHubHost ?? discovered?.Host ?? "github.com"
            },
            request.ProjectTitle);
    }

    private async Task<DiscoveredGitHubRepository?> DiscoverRepositoryIfNeededAsync(
        string workingDirectory,
        TrackerInitializationRequest request,
        CancellationToken cancellationToken)
    {
        if (request.Repository is not null)
        {
            return null;
        }

        return await repositoryDiscovery.DiscoverAsync(
            workingDirectory,
            request.Remote ?? "origin",
            cancellationToken);
    }

    private static void EnsureCompatibleHost(
        string? requestedHost,
        DiscoveredGitHubRepository? discovered)
    {
        if (discovered is not null && requestedHost is not null &&
            !string.Equals(discovered.Host, requestedHost, StringComparison.OrdinalIgnoreCase))
        {
            throw new TrackerException(
                "GIT_REMOTE_UNSUPPORTED",
                $"Git remote host '{discovered.Host}' does not match --github-host '{requestedHost}'.",
                2);
        }
    }

    private async Task<ProjectPlan> ResolveProjectAsync(
        GitHubSeed seed,
        TrackerConfig? existing,
        TrackerInitializationRequest request,
        GitHubRepositoryInfo repository,
        string projectOwner,
        CancellationToken cancellationToken)
    {
        if (existing is not null || request.ProjectNumber.HasValue)
        {
            var number = existing?.ProjectNumber ?? request.ProjectNumber!.Value;
            var project = await github.GetProjectAsync(
                seed.Config.GitHubHost,
                projectOwner,
                number,
                cancellationToken)
                ?? throw new TrackerException(
                    "PROJECT_NOT_FOUND",
                    $"Project {projectOwner}/{number} was not found or is inaccessible.",
                    5);
            return new ProjectPlan(project, project.Title);
        }

        var title = seed.ProjectTitle ?? $"Wrighty - {repository.NameWithOwner}";
        var matches = await github.FindProjectsByTitleAsync(
            seed.Config.GitHubHost,
            projectOwner,
            title,
            cancellationToken);
        EnsureUnambiguousProject(matches, projectOwner, title);
        if (matches.Count == 1)
        {
            return new ProjectPlan(matches[0], title);
        }

        if (request.CheckOnly)
        {
            throw new TrackerException(
                "PROJECT_INITIALIZATION_REQUIRED",
                $"Project '{title}' does not exist. Run 'wrighty init' without --check to create it.",
                5,
                new Dictionary<string, object?>
                {
                    ["projectOwner"] = projectOwner,
                    ["projectTitle"] = title
                });
        }

        return new ProjectPlan(null, title);
    }

    private static void EnsureUnambiguousProject(
        IReadOnlyList<GitHubProjectInfo> matches,
        string projectOwner,
        string projectTitle)
    {
        if (matches.Count <= 1)
        {
            return;
        }

        throw new TrackerException(
            "PROJECT_TITLE_AMBIGUOUS",
            $"Multiple Projects owned by '{projectOwner}' are titled '{projectTitle}'. Use --project-number.",
            2,
            new Dictionary<string, object?>
            {
                ["projectOwner"] = projectOwner,
                ["projectTitle"] = projectTitle,
                ["projectNumbers"] = matches.Select(item => item.Number).ToArray()
            });
    }

    private static TrackerInitializationPlan BuildGitHubPlan(
        string configPath,
        TrackerConfig? existing,
        TrackerInitializationRequest request,
        string backendSelection,
        GitHubRepositoryInfo repository,
        string projectOwner,
        bool linkRepository,
        ProjectPlan project)
    {
        var createProject = project.Project is null;
        var createView = createProject || request.CreateView;
        var needsRepositoryLink = linkRepository &&
            (createProject || !project.Project!.LinkedRepositories.Contains(
                repository.NameWithOwner,
                StringComparer.OrdinalIgnoreCase));
        var steps = new List<string>();
        if (existing is null)
        {
            steps.Add($"create configuration '{configPath}'");
        }
        steps.Add(createProject
            ? $"create GitHub Project '{project.Title}'"
            : $"reuse GitHub Project {projectOwner}/{project.Project!.Number} ('{project.Project.Title}')");
        if (needsRepositoryLink)
        {
            steps.Add($"link the Project from repository '{repository.NameWithOwner}'");
        }
        else if (linkRepository)
        {
            steps.Add($"keep the existing repository link for '{repository.NameWithOwner}'");
        }
        else
        {
            steps.Add("leave repository-to-Project linking disabled");
        }
        steps.Add("ensure Wrighty worker labels for Claude, Codex, and Copilot");
        steps.Add("create or reconcile Wrighty Project fields and workflow options");
        steps.Add(createView
            ? "create or reuse the canonical 'Wrighty Board'"
            : "preserve existing Project views and report board setup guidance when needed");
        steps.Add(request.SkipIssueForms
            ? "skip local GitHub issue-form creation"
            : "create or reuse local Claude, Codex, and Copilot worker issue forms");

        return new TrackerInitializationPlan(
            "github",
            backendSelection,
            configPath,
            existing is null,
            repository.NameWithOwner,
            projectOwner,
            project.Project?.Number,
            project.Title,
            createProject,
            needsRepositoryLink,
            createView,
            !request.SkipIssueForms,
            null,
            steps,
            createProject
                ?
                [
                    $"Set the Project's Default repository to '{repository.NameWithOwner}' in Project Settings.",
                    "Delete GitHub's initial 'View 1' manually if Wrighty Board should be the only and default view."
                ]
                : []);
    }

    private static Task ApproveAsync(
        TrackerInitializationPlan plan,
        TrackerInitializationRequest request,
        TrackerInitializationApproval? approval,
        CancellationToken cancellationToken) =>
        request.CheckOnly || approval is null
            ? Task.CompletedTask
            : approval(plan, cancellationToken);

    private async Task<TrackerInitializationResult> CompleteGitHubInitializationAsync(
        string configPath,
        TrackerConfig config,
        bool isBootstrap,
        TrackerInitializationRequest request,
        GitHubRepositoryInfo repository,
        ProjectResolution projectResolution,
        bool linkRepository,
        string projectOwner,
        string backendSelection,
        CancellationToken cancellationToken)
    {
        var actions = new List<string>();
        if (projectResolution.Created)
        {
            actions.Add("created Project");
        }

        var linkedRepository = projectResolution.Project.LinkedRepositories.Any(linked =>
            string.Equals(linked, repository.NameWithOwner, StringComparison.OrdinalIgnoreCase));

        try
        {
            await PersistBootstrapAsync(
                configPath, config, isBootstrap, projectResolution.Created, request, actions, cancellationToken);
            linkedRepository = await EnsureRepositoryLinkAsync(
                config,
                request,
                repository,
                projectResolution.Project,
                projectOwner,
                linkRepository,
                linkedRepository,
                actions,
                cancellationToken);
            actions.AddRange(await github.InitializeWorkerLabelsAsync(
                config.GitHubHost,
                config.Repository,
                request.CheckOnly,
                cancellationToken));
            var fieldResult = await InitializeProjectSchemaAsync(config, request.CheckOnly, cancellationToken);
            actions.AddRange(fieldResult.Actions);
            var viewChanged = await ReconcileCanonicalProjectViewAsync(
                config,
                request,
                projectResolution,
                actions,
                cancellationToken);
            AddDefaultRepositoryNotice(config, projectResolution, actions);
            return new TrackerInitializationResult(
                config,
                configPath,
                projectResolution.Project.Title,
                projectResolution.Project.Url,
                projectResolution.Created,
                linkedRepository,
                isBootstrap || projectResolution.Created ||
                actions.Contains("linked repository") || fieldResult.Changed || viewChanged,
                actions,
                backendSelection);
        }
        catch (Exception exception) when (
            isBootstrap && !request.CheckOnly && exception is not OperationCanceledException)
        {
            throw PartialInitialization(configPath, config, projectResolution.Project, exception);
        }
    }

    private async Task PersistBootstrapAsync(
        string configPath,
        TrackerConfig config,
        bool isBootstrap,
        bool createdProject,
        TrackerInitializationRequest request,
        ICollection<string> actions,
        CancellationToken cancellationToken)
    {
        if (!isBootstrap || request.CheckOnly)
        {
            return;
        }

        await configStore.SaveAsync(
            configPath,
            config,
            createdProject ? CancellationToken.None : cancellationToken);
        actions.Add("wrote configuration");
    }

    private async Task<bool> EnsureRepositoryLinkAsync(
        TrackerConfig config,
        TrackerInitializationRequest request,
        GitHubRepositoryInfo repository,
        GitHubProjectInfo project,
        string projectOwner,
        bool linkRepository,
        bool linkedRepository,
        ICollection<string> actions,
        CancellationToken cancellationToken)
    {
        if (linkRepository && !linkedRepository)
        {
            if (request.CheckOnly)
            {
                throw new TrackerException(
                    "PROJECT_INITIALIZATION_REQUIRED",
                    $"Project {projectOwner}/{project.Number} is not linked to repository '{repository.NameWithOwner}'. Run 'wrighty init'.",
                    5);
            }

            await github.LinkRepositoryAsync(
                config.GitHubHost,
                project.NodeId,
                repository.NodeId,
                cancellationToken);
            actions.Add("linked repository");
            return true;
        }

        if (!linkRepository &&
            !string.Equals(projectOwner, repository.Owner, StringComparison.OrdinalIgnoreCase))
        {
            actions.Add("repository link skipped because Project and repository owners differ");
        }

        return linkedRepository;
    }

    private async Task<ProjectInitializationResult> InitializeProjectSchemaAsync(
        TrackerConfig config,
        bool checkOnly,
        CancellationToken cancellationToken)
    {
        var result = await projects.InitializeAsync(config, checkOnly, cancellationToken);
        foreach (var archiveStatus in config.Archive.OnStatuses)
        {
            await projects.ValidateUpdateFieldsAsync(
                config,
                archiveStatus,
                null,
                false,
                cancellationToken);
        }

        return result;
    }

    private async Task<bool> ReconcileCanonicalProjectViewAsync(
        TrackerConfig config,
        TrackerInitializationRequest request,
        ProjectResolution projectResolution,
        ICollection<string> actions,
        CancellationToken cancellationToken)
    {
        const string canonicalName = "Wrighty Board";
        IReadOnlyList<GitHubProjectViewInfo> views;
        try
        {
            views = await github.ListProjectViewsAsync(
                config.GitHubHost,
                projectResolution.Project,
                cancellationToken);
        }
        catch (TrackerException exception) when (IsAdvisoryViewCapabilityFailure(exception))
        {
            actions.Add(
                $"Could not inspect GitHub Project views ({exception.Code}). " +
                ManualBoardGuidance());
            return false;
        }

        var exactMatches = views
            .Where(view => string.Equals(view.Name, canonicalName, StringComparison.Ordinal))
            .ToArray();
        if (exactMatches.Length > 1)
        {
            throw ProjectViewConflict(
                projectResolution.Project,
                "multiple views use the exact canonical name");
        }

        if (exactMatches.Length == 1)
        {
            var existing = exactMatches[0];
            if (!string.Equals(existing.Layout, "BOARD_LAYOUT", StringComparison.Ordinal))
            {
                throw ProjectViewConflict(
                    projectResolution.Project,
                    $"the exact-name view uses layout '{existing.Layout}' instead of board");
            }

            actions.Add($"Wrighty Board is available: {existing.Url}");
            AddInitialViewNotice(projectResolution, views, actions);
            return false;
        }

        var mayCreate = !request.CheckOnly &&
                        (projectResolution.Created || request.CreateView);
        if (!mayCreate)
        {
            actions.Add(ManualBoardGuidance());
            return false;
        }

        try
        {
            await github.CreateProjectViewAsync(
                config.GitHubHost,
                projectResolution.Project,
                canonicalName,
                cancellationToken);
            views = await github.ListProjectViewsAsync(
                config.GitHubHost,
                projectResolution.Project,
                cancellationToken);
        }
        catch (TrackerException exception) when (IsAdvisoryViewCapabilityFailure(exception))
        {
            actions.Add(
                $"GitHub could not create and verify Wrighty Board ({exception.Code}). " +
                ManualBoardGuidance());
            return false;
        }

        var created = views
            .Where(view => string.Equals(view.Name, canonicalName, StringComparison.Ordinal))
            .ToArray();
        if (created.Length != 1 ||
            !string.Equals(created[0].Layout, "BOARD_LAYOUT", StringComparison.Ordinal))
        {
            actions.Add(
                "GitHub created a view but Wrighty could not verify the exact-name board postcondition. " +
                ManualBoardGuidance());
            return false;
        }

        actions.Add($"created Wrighty Board: {created[0].Url}");
        AddInitialViewNotice(projectResolution, views, actions);
        return true;
    }

    private static void AddInitialViewNotice(
        ProjectResolution projectResolution,
        IReadOnlyList<GitHubProjectViewInfo> views,
        ICollection<string> actions)
    {
        if (!projectResolution.Created ||
            !views.Any(view =>
                view.Number == 1 &&
                string.Equals(view.Name, "View 1", StringComparison.Ordinal) &&
                string.Equals(view.Layout, "TABLE_LAYOUT", StringComparison.Ordinal)))
        {
            return;
        }

        actions.Add(
            "GitHub also created the initial table view 'View 1'. " +
            "To make Wrighty Board the Project's only view and therefore the default, delete 'View 1' manually from its view menu.");
    }

    private static void AddDefaultRepositoryNotice(
        TrackerConfig config,
        ProjectResolution projectResolution,
        ICollection<string> actions)
    {
        if (!projectResolution.Created)
        {
            return;
        }

        actions.Add(
            $"Set the Project's Default repository to '{config.Repository}' in Project Settings, then save the change. " +
            "This makes issues created from Wrighty Board target the configured repository automatically.");
    }

    private static bool IsAdvisoryViewCapabilityFailure(TrackerException exception) =>
        exception.Code is "GH_API_ERROR" or "GH_AUTH_REQUIRED" or "GH_RESPONSE_INVALID" or
            "NOT_SUPPORTED";

    private static string ManualBoardGuidance() =>
        "Create a board named 'Wrighty Board' and use the Status field for its columns.";

    private static TrackerException ProjectViewConflict(
        GitHubProjectInfo project,
        string reason) =>
        new(
            "PROJECT_VIEW_CONFLICT",
            $"Project {project.Owner}/{project.Number} has a conflicting 'Wrighty Board': {reason}. Wrighty did not replace it.",
            5,
            new Dictionary<string, object?>
            {
                ["projectOwner"] = project.Owner,
                ["projectNumber"] = project.Number,
                ["projectUrl"] = project.Url
            });

    private static void ValidateArguments(TrackerInitializationRequest request)
    {
        ValidateBackendArgument(request);
        ValidateProjectArguments(request);
        ValidateRemote(request.Remote);
        ValidateGitHubHost(request.GitHubHost);
        ValidateRepositoryArgument(request.Repository);
    }

    private static void ValidateBackendArgument(TrackerInitializationRequest request)
    {
        if (request.Backend is not null &&
            !string.Equals(request.Backend, "github", StringComparison.OrdinalIgnoreCase) &&
            !string.Equals(request.Backend, "local-markdown", StringComparison.OrdinalIgnoreCase))
        {
            throw new TrackerException(
                "ARGUMENT_INVALID",
                "--backend must be 'github' or 'local-markdown'.",
                2);
        }

        if (request.Backend is not null &&
            string.Equals(request.Backend, "local-markdown", StringComparison.OrdinalIgnoreCase) &&
            HasGitHubOptions(request))
        {
            throw new TrackerException(
                "OPTION_BACKEND_MISMATCH",
                "GitHub initialization options cannot be used with the local-markdown backend.",
                2);
        }
    }

    private static bool HasGitHubOptions(TrackerInitializationRequest request) =>
        request.Repository is not null || request.ProjectOwner is not null ||
        request.ProjectNumber is not null || request.ProjectTitle is not null ||
        request.GitHubHost is not null || request.NoLinkRepositorySpecified ||
        request.CreateView || request.SkipIssueForms;

    private static void ValidateProjectArguments(TrackerInitializationRequest request)
    {
        if (request.ProjectNumber.HasValue && request.ProjectNumber <= 0)
        {
            throw new TrackerException("ARGUMENT_INVALID", "--project-number must be positive.", 2);
        }

        if (request.ProjectNumber.HasValue && request.ProjectTitle is not null)
        {
            throw new TrackerException(
                "ARGUMENT_INVALID",
                "--project-number and --project-title cannot be used together.",
                2);
        }

        if (request.ProjectTitle is not null && string.IsNullOrWhiteSpace(request.ProjectTitle))
        {
            throw new TrackerException("ARGUMENT_INVALID", "--project-title cannot be empty.", 2);
        }

        if (request.ProjectOwner is not null && string.IsNullOrWhiteSpace(request.ProjectOwner))
        {
            throw new TrackerException("ARGUMENT_INVALID", "--project-owner cannot be empty.", 2);
        }
    }

    private static void ValidateRemote(string? remote)
    {
        if (remote is not null && string.IsNullOrWhiteSpace(remote))
        {
            throw new TrackerException("ARGUMENT_INVALID", "--remote cannot be empty.", 2);
        }
    }

    private static void ValidateGitHubHost(string? host)
    {
        if (host is not null &&
            (string.IsNullOrWhiteSpace(host) || host.Contains('/') || host.Any(char.IsWhiteSpace)))
        {
            throw new TrackerException(
                "ARGUMENT_INVALID",
                "--github-host must be a hostname without a URL scheme or path.",
                2);
        }
    }

    private static void ValidateRepositoryArgument(string? repository)
    {
        if (repository is not null)
        {
            ValidateRepository(repository);
        }
    }

    private static void AssertExistingConfiguration(
        TrackerConfig config,
        TrackerInitializationRequest request,
        string configPath)
    {
        if (request.Backend is not null &&
            !string.Equals(request.Backend, config.Backend, StringComparison.OrdinalIgnoreCase))
        {
            throw Conflict("--backend", request.Backend, config.Backend, configPath);
        }

        if (request.ProjectTitle is not null || request.Remote is not null)
        {
            var option = request.ProjectTitle is not null ? "--project-title" : "--remote";
            throw new TrackerException(
                "OPTION_BOOTSTRAP_ONLY",
                $"{option} can only be used when creating a new configuration. Run 'wrighty init' without it.",
                2,
                new Dictionary<string, object?> { ["configPath"] = configPath });
        }

        AssertMatch("--repository", request.Repository, config.Repository, configPath);
        AssertMatch("--github-host", request.GitHubHost, config.GitHubHost, configPath);
        AssertMatch("--project-owner", request.ProjectOwner, config.EffectiveProjectOwner, configPath);
        if (request.ProjectNumber.HasValue && request.ProjectNumber != config.ProjectNumber)
        {
            throw Conflict("--project-number", request.ProjectNumber, config.ProjectNumber, configPath);
        }

        if (request.NoLinkRepositorySpecified && request.NoLinkRepository == config.LinkRepository)
        {
            throw Conflict(
                "--no-link-repository",
                request.NoLinkRepository,
                !config.LinkRepository,
                configPath);
        }
    }

    private static void AssertMatch(
        string option,
        string? requested,
        string configured,
        string configPath)
    {
        if (requested is not null &&
            !string.Equals(requested, configured, StringComparison.OrdinalIgnoreCase))
        {
            throw Conflict(option, requested, configured, configPath);
        }
    }

    private static TrackerException Conflict(
        string option,
        object? requested,
        object? configured,
        string configPath) => new(
        "CONFIG_CONFLICT",
        $"{option} specifies '{requested}', but {configPath} specifies '{configured}'. Run without the conflicting option or deliberately edit the configuration.",
        3,
        new Dictionary<string, object?>
        {
            ["configPath"] = configPath,
            ["option"] = option,
            ["requested"] = requested,
            ["configured"] = configured
        });

    private static void ValidateRepository(string repository)
    {
        var parts = repository.Split('/');
        if (parts.Length != 2 ||
            parts.Any(string.IsNullOrWhiteSpace) ||
            parts.SelectMany(part => part).Any(character =>
                !char.IsLetterOrDigit(character) && character is not '_' and not '-' and not '.'))
        {
            throw new TrackerException(
                "ARGUMENT_INVALID",
                "--repository must use OWNER/REPOSITORY format.",
                2);
        }
    }

    private static TrackerException RepositoryRequired(string configPath) => new(
        "REPOSITORY_REQUIRED",
        "No GitHub repository was specified or detected. Run 'wrighty init --repository OWNER/REPOSITORY'. To use an existing Project, also pass --project-owner OWNER --project-number NUMBER. Alternatively, create .wrighty.json manually.",
        2,
        new Dictionary<string, object?> { ["configPath"] = configPath });

    private static TrackerException PartialInitialization(
        string configPath,
        TrackerConfig config,
        GitHubProjectInfo project,
        Exception exception) => new(
        "PARTIAL_INITIALIZATION",
        "The Project was resolved, but Wrighty initialization did not complete. The reported Project identity can be used safely on retry.",
        10,
        new Dictionary<string, object?>
        {
            ["configPath"] = configPath,
            ["repository"] = config.Repository,
            ["projectOwner"] = project.Owner,
            ["projectNumber"] = project.Number,
            ["projectUrl"] = project.Url
        },
        exception);

    private async Task<TrackerInitializationResult> InitializeLocalAsync(
        string configPath,
        TrackerConfig? existing,
        TrackerInitializationRequest request,
        string backendSelection,
        TrackerInitializationApproval? approval,
        CancellationToken cancellationToken)
    {
        var backend = GetLocalBackend();
        EnsureLocalBackendOptions(request);
        ValidateExistingLocalConfiguration(existing, request, configPath);
        var config = existing ?? CreateLocalConfiguration(configPath, request);
        var root = Path.GetFullPath(
            config.LocalMarkdown!.Path,
            Path.GetDirectoryName(configPath)!);
        var steps = new List<string>();
        if (existing is null)
        {
            steps.Add($"create configuration '{configPath}'");
        }
        steps.Add($"create or validate the Local Markdown store '{root}'");
        await ApproveAsync(
            new TrackerInitializationPlan(
                "local-markdown",
                backendSelection,
                configPath,
                existing is null,
                null,
                null,
                null,
                "Local Markdown",
                false,
                false,
                false,
                false,
                root,
                steps,
                []),
            request,
            approval,
            cancellationToken);
        var actions = new List<string>();
        config = await PersistLocalConfigurationAsync(
            configPath, config, existing, request, actions, cancellationToken);

        var initialized = await backend.InitializeAsync(config, request.CheckOnly, cancellationToken);
        actions.AddRange(initialized.Actions);
        return new TrackerInitializationResult(
            config,
            configPath,
            "Local Markdown",
            root,
            false,
            false,
            existing is null || initialized.Changed,
            actions,
            backendSelection);
    }

    private ITrackerBackend GetLocalBackend()
    {
        if (backends is null)
        {
            throw new TrackerException(
                "BACKEND_UNSUPPORTED",
                "The local-markdown backend is not registered.",
                3);
        }

        return backends.Get("local-markdown");
    }

    private static void EnsureLocalBackendOptions(TrackerInitializationRequest request)
    {
        if (HasGitHubOptions(request))
        {
            throw new TrackerException(
                "OPTION_BACKEND_MISMATCH",
                "GitHub initialization options cannot be used with the local-markdown backend.",
                2);
        }
    }

    private static void ValidateExistingLocalConfiguration(
        TrackerConfig? existing,
        TrackerInitializationRequest request,
        string configPath)
    {
        if (existing is null)
        {
            return;
        }

        if (!string.Equals(existing.Backend, "local-markdown", StringComparison.OrdinalIgnoreCase))
        {
            throw Conflict("--backend", "local-markdown", existing.Backend, configPath);
        }

        if (request.LocalPath is not null &&
            !string.Equals(request.LocalPath, existing.LocalMarkdown!.Path, StringComparison.Ordinal))
        {
            throw Conflict("--local-path", request.LocalPath, existing.LocalMarkdown.Path, configPath);
        }

        if (request.Statuses is not null || request.Priorities is not null)
        {
            throw new TrackerException(
                "OPTION_BOOTSTRAP_ONLY",
                "--status and --priority can only be used when creating a local configuration.",
                2);
        }
    }

    private static TrackerConfig CreateLocalConfiguration(
        string configPath,
        TrackerInitializationRequest request) => new()
        {
            Backend = "local-markdown",
            SourcePath = configPath,
            LocalMarkdown = new LocalMarkdownBackendConfig
            {
                Path = request.LocalPath ?? ".wrighty",
                Statuses = request.Statuses is { Count: > 0 }
                ? request.Statuses
                : ["Todo", "In Progress", "Done"],
                Priorities = request.Priorities ?? ["P0", "P1", "P2", "P3"]
            }
        };

    private async Task<TrackerConfig> PersistLocalConfigurationAsync(
        string configPath,
        TrackerConfig config,
        TrackerConfig? existing,
        TrackerInitializationRequest request,
        ICollection<string> actions,
        CancellationToken cancellationToken)
    {
        if (existing is not null || request.CheckOnly)
        {
            return config;
        }

        await configStore.SaveAsync(configPath, config, cancellationToken);
        actions.Add("wrote configuration");
        return config with { SourcePath = configPath };
    }

    private sealed record BackendSelection(
        string Backend,
        string Source,
        DiscoveredGitHubRepository? DiscoveredRepository);

    private sealed record GitHubSeed(TrackerConfig Config, string? ProjectTitle);

    private sealed record ProjectPlan(GitHubProjectInfo? Project, string Title);

    private sealed record ProjectResolution(GitHubProjectInfo Project, bool Created);
}
