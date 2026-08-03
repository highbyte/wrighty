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
    bool SkipIssueForms = false,
    bool PublishIssueForms = false,
    IReadOnlyList<string>? TrustedCommentAuthors = null,
    string? DefaultAgent = null,
    bool DefaultAgentSpecified = false);

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
    IReadOnlyList<string> ManualFollowUp,
    string? WorkerDefaultAgent = null,
    bool WorkerDefaultAgentIncluded = false);

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
        seed = seed with { Config = ApplyDefaultAgent(seed.Config, request) };

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

        var config = ApplyDefaultAgent(existing ?? seed.Config with
        {
            Repository = repositoryInfo.NameWithOwner,
            ProjectOwner = projectOwner,
            ProjectNumber = projectResolution.Project.Number,
            LinkRepository = linkRepository
        }, request);
        var configurationChanged = DefaultAgentChanged(existing, config, request);
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
            configurationChanged,
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
                GitHubHost = request.GitHubHost ?? discovered?.Host ?? "github.com",
                TrustedCommentAuthors = request.TrustedCommentAuthors ?? []
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
        steps.Add("ensure Wrighty dispatch-state lifecycle labels");
        steps.Add("create or reconcile Wrighty Project fields and workflow options");
        steps.Add(createView
            ? "create or reuse the canonical 'Wrighty Board' and 'Wrighty Attention' views"
            : "preserve existing Project views and report board setup guidance when needed");
        steps.Add(request.SkipIssueForms
            ? "skip local GitHub issue-form creation"
            : "create or reuse the neutral Wrighty issue form and disable blank issues");
        if (request.PublishIssueForms)
        {
            steps.Add("stage, commit, and push only the Wrighty-managed issue forms");
        }
        AddDefaultAgentPlanStep(steps, existing, request);

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
                : [],
            request.DefaultAgent,
            request.DefaultAgentSpecified);
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
        bool configurationChanged,
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
            var checkFailures = new List<TrackerException>();
            var persistConfiguration =
                (isBootstrap || configurationChanged) && !request.CheckOnly;
            await PersistBootstrapAsync(
                configPath, config, isBootstrap, projectResolution.Created,
                persistConfiguration, actions, cancellationToken);
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
            try
            {
                actions.AddRange(await github.InitializeWorkerLabelsAsync(
                    config.GitHubHost,
                    config.Repository,
                    request.CheckOnly,
                    cancellationToken));
            }
            catch (TrackerException exception) when (request.CheckOnly)
            {
                checkFailures.Add(exception);
                actions.Add(exception.Message);
            }

            var fieldResult = new ProjectInitializationResult(false, []);
            try
            {
                fieldResult = await InitializeProjectSchemaAsync(
                    config,
                    request.CheckOnly,
                    projectResolution.Created,
                    cancellationToken);
                actions.AddRange(fieldResult.Actions);
            }
            catch (TrackerException exception) when (request.CheckOnly)
            {
                checkFailures.Add(exception);
                actions.Add(exception.Message);
            }

            var viewChanged = await ReconcileCanonicalProjectViewsAsync(
                config,
                request,
                projectResolution,
                fieldResult.FieldDatabaseIds,
                actions,
                cancellationToken);
            AddDefaultRepositoryNotice(config, projectResolution, actions);
            if (checkFailures.Count > 0)
            {
                throw InitializationCheckFailed(checkFailures);
            }

            return new TrackerInitializationResult(
                config,
                configPath,
                projectResolution.Project.Title,
                projectResolution.Project.Url,
                projectResolution.Created,
                linkedRepository,
                isBootstrap || configurationChanged || projectResolution.Created ||
                actions.Contains("linked repository") || fieldResult.Changed ||
                viewChanged,
                actions,
                backendSelection);
        }
        catch (Exception exception) when (
            isBootstrap && !request.CheckOnly && exception is not OperationCanceledException)
        {
            throw PartialInitialization(configPath, config, projectResolution.Project, exception);
        }
    }

    private static TrackerException InitializationCheckFailed(
        IReadOnlyList<TrackerException> failures)
    {
        if (failures.Count == 1)
        {
            return failures[0];
        }

        return new TrackerException(
            "PROJECT_INITIALIZATION_REQUIRED",
            $"GitHub initialization check found {failures.Count} problem(s): " +
            string.Join(" ", failures.Select(failure => failure.Message)),
            5,
            new Dictionary<string, object?>
            {
                ["problems"] = failures
                    .Select(failure => new Dictionary<string, object?>
                    {
                        ["code"] = failure.Code,
                        ["message"] = failure.Message,
                        ["details"] = failure.Details
                    })
                    .ToArray()
            });
    }

    private async Task PersistBootstrapAsync(
        string configPath,
        TrackerConfig config,
        bool isBootstrap,
        bool createdProject,
        bool persistConfiguration,
        ICollection<string> actions,
        CancellationToken cancellationToken)
    {
        if (!persistConfiguration)
            return;

        await configStore.SaveAsync(
            configPath,
            config,
            createdProject ? CancellationToken.None : cancellationToken);
        var action = isBootstrap
            ? "wrote configuration"
            : $"updated worker.defaultAgent to '{config.EffectiveWorker.DefaultAgent ?? "none"}'";
        actions.Add(action);
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
        bool projectCreated,
        CancellationToken cancellationToken)
    {
        var result = await projects.InitializeAsync(
            config, checkOnly, cancellationToken, projectCreated);
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

    /// <summary>The dispatch-state option that marks an item as waiting for an operator.</summary>
    private const string NeedsAttentionOptionName = "Needs attention";

    private async Task<bool> ReconcileCanonicalProjectViewsAsync(
        TrackerConfig config,
        TrackerInitializationRequest request,
        ProjectResolution projectResolution,
        IReadOnlyDictionary<string, long>? fieldDatabaseIds,
        ICollection<string> actions,
        CancellationToken cancellationToken)
    {
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
                ManualBoardGuidance(config));
            return false;
        }

        var boardSpec = new GitHubProjectViewSpec(
            "Wrighty Board",
            "board",
            Filter: null,
            VisibleFieldIds: ResolveVisibleFieldIds(fieldDatabaseIds, BoardCardFields(config)));
        var (boardChanged, refreshedViews) = await ReconcileOneProjectViewAsync(
            config,
            request,
            projectResolution,
            views,
            new ViewReconciliation(
                boardSpec,
                "BOARD_LAYOUT",
                ManualBoardGuidance(config),
                CardFieldAdjustmentHint(boardSpec.Name, BoardCardFields(config))),
            actions,
            cancellationToken);

        var attentionSpec = new GitHubProjectViewSpec(
            "Wrighty Attention",
            "table",
            Filter: NeedsAttentionFilter(config),
            VisibleFieldIds: ResolveVisibleFieldIds(fieldDatabaseIds, AttentionViewFields(config)));
        var (attentionChanged, finalViews) = await ReconcileOneProjectViewAsync(
            config,
            request,
            projectResolution,
            refreshedViews,
            new ViewReconciliation(
                attentionSpec,
                "TABLE_LAYOUT",
                ManualAttentionGuidance(config),
                ExistingViewHint: null),
            actions,
            cancellationToken);

        AddInitialViewNotice(projectResolution, finalViews, actions);
        return boardChanged || attentionChanged;
    }

    /// <summary>One canonical view's creation spec plus the operator guidance that goes with it.</summary>
    private sealed record ViewReconciliation(
        GitHubProjectViewSpec Spec,
        string ExpectedLayout,
        string ManualGuidance,
        string? ExistingViewHint);

    private async Task<(bool Changed, IReadOnlyList<GitHubProjectViewInfo> Views)>
        ReconcileOneProjectViewAsync(
            TrackerConfig config,
            TrackerInitializationRequest request,
            ProjectResolution projectResolution,
            IReadOnlyList<GitHubProjectViewInfo> views,
            ViewReconciliation reconciliation,
            ICollection<string> actions,
            CancellationToken cancellationToken)
    {
        var (spec, expectedLayout, manualGuidance, existingViewHint) = reconciliation;
        var exactMatches = views
            .Where(view => string.Equals(view.Name, spec.Name, StringComparison.Ordinal))
            .ToArray();
        if (exactMatches.Length > 1)
        {
            throw ProjectViewConflict(
                projectResolution.Project,
                spec.Name,
                "multiple views use the exact canonical name");
        }

        if (exactMatches.Length == 1)
        {
            var existing = exactMatches[0];
            if (!string.Equals(existing.Layout, expectedLayout, StringComparison.Ordinal))
            {
                throw ProjectViewConflict(
                    projectResolution.Project,
                    spec.Name,
                    $"the exact-name view uses layout '{existing.Layout}' instead of {spec.Layout}");
            }

            actions.Add($"{spec.Name} is available: {existing.Url}");
            // Shown fields can only be set when a view is created; an explicit --create-view run
            // on a pre-existing view gets the manual recipe instead.
            if (request.CreateView && existingViewHint is not null)
            {
                actions.Add(existingViewHint);
            }
            return (false, views);
        }

        var mayCreate = !request.CheckOnly &&
                        (projectResolution.Created || request.CreateView);
        if (!mayCreate)
        {
            actions.Add(manualGuidance);
            return (false, views);
        }

        try
        {
            await github.CreateProjectViewAsync(
                config.GitHubHost,
                projectResolution.Project,
                spec,
                cancellationToken);
        }
        catch (TrackerException exception) when (IsAdvisoryViewCapabilityFailure(exception))
        {
            // The host may predate the filter/visible_fields view properties. Re-list first: if
            // the enriched create actually landed despite the error, retrying would duplicate the
            // view. Only when it verifiably did not land, retry the plain shape this code sent
            // before those properties existed.
            var retried = await TryCreatePlainProjectViewAsync(
                config, projectResolution, spec, actions, cancellationToken);
            if (!retried.Created)
            {
                actions.Add(
                    $"GitHub could not create and verify {spec.Name} ({exception.Code}). " +
                    manualGuidance);
                return (false, retried.Views ?? views);
            }
        }

        return await VerifyCreatedProjectViewAsync(
            config, projectResolution, reconciliation, views, actions, cancellationToken);
    }

    private async Task<(bool Changed, IReadOnlyList<GitHubProjectViewInfo> Views)>
        VerifyCreatedProjectViewAsync(
            TrackerConfig config,
            ProjectResolution projectResolution,
            ViewReconciliation reconciliation,
            IReadOnlyList<GitHubProjectViewInfo> views,
            ICollection<string> actions,
            CancellationToken cancellationToken)
    {
        var (spec, expectedLayout, manualGuidance, _) = reconciliation;
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
                $"GitHub could not create and verify {spec.Name} ({exception.Code}). " +
                manualGuidance);
            return (false, views);
        }

        var created = views
            .Where(view => string.Equals(view.Name, spec.Name, StringComparison.Ordinal))
            .ToArray();
        if (created.Length != 1 ||
            !string.Equals(created[0].Layout, expectedLayout, StringComparison.Ordinal))
        {
            actions.Add(
                $"GitHub created a view but Wrighty could not verify the exact-name {spec.Name} postcondition. " +
                manualGuidance);
            return (false, views);
        }

        actions.Add($"created {spec.Name}: {created[0].Url}");
        return (true, views);
    }

    private async Task<(bool Created, IReadOnlyList<GitHubProjectViewInfo>? Views)>
        TryCreatePlainProjectViewAsync(
            TrackerConfig config,
            ProjectResolution projectResolution,
            GitHubProjectViewSpec spec,
            ICollection<string> actions,
            CancellationToken cancellationToken)
    {
        if (spec.Filter is null && spec.VisibleFieldIds is null)
        {
            return (false, null);
        }

        IReadOnlyList<GitHubProjectViewInfo> views;
        try
        {
            views = await github.ListProjectViewsAsync(
                config.GitHubHost,
                projectResolution.Project,
                cancellationToken);
            if (views.Any(view => string.Equals(view.Name, spec.Name, StringComparison.Ordinal)))
            {
                return (true, views);
            }

            await github.CreateProjectViewAsync(
                config.GitHubHost,
                projectResolution.Project,
                spec with { Filter = null, VisibleFieldIds = null },
                cancellationToken);
        }
        catch (TrackerException exception) when (IsAdvisoryViewCapabilityFailure(exception))
        {
            return (false, null);
        }

        actions.Add(
            $"{spec.Name} was created without its preset fields or filter " +
            "(this GitHub host rejected the view options). Configure them manually in the view menu.");
        return (true, views);
    }

    private static List<long>? ResolveVisibleFieldIds(
        IReadOnlyDictionary<string, long>? fieldDatabaseIds,
        IReadOnlyList<string> fieldNames)
    {
        if (fieldDatabaseIds is null)
        {
            return null;
        }

        var ids = new List<long>(fieldNames.Count);
        foreach (var name in fieldNames)
        {
            if (fieldDatabaseIds.TryGetValue(name, out var id))
            {
                ids.Add(id);
            }
        }

        return ids.Count > 0 ? ids : null;
    }

    private static IReadOnlyList<string> BoardCardFields(TrackerConfig config) =>
    [
        config.PriorityField,
        config.DispatchStateField,
        config.ContextApprovalField,
        config.ClaimAgentField
    ];

    private static IReadOnlyList<string> AttentionViewFields(TrackerConfig config) =>
    [
        config.StatusField,
        config.PriorityField,
        config.ClaimAgentField,
        config.DispatchDetailField
    ];

    private static string CardFieldAdjustmentHint(
        string viewName,
        IReadOnlyList<string> fieldNames) =>
        $"Wrighty cannot change which fields an existing view shows. To match a newly created " +
        $"{viewName}, enable these card fields manually in its view menu under Fields: " +
        string.Join(", ", fieldNames.Select(name => $"'{name}'")) + ".";

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

    private static string ManualBoardGuidance(TrackerConfig config) =>
        "Create a board named 'Wrighty Board', use the Status field for its columns, and show " +
        string.Join(", ", BoardCardFields(config).Select(name => $"'{name}'")) +
        " on its cards.";

    private static string ManualAttentionGuidance(TrackerConfig config) =>
        "Create a table view named 'Wrighty Attention' with the filter " +
        $"{NeedsAttentionFilter(config)} and the columns " +
        string.Join(", ", AttentionViewFields(config).Select(name => $"'{name}'")) + ".";

    private static string NeedsAttentionFilter(TrackerConfig config) =>
        $"{ProjectFilterQualifier(config.DispatchStateField)}:\"{NeedsAttentionOptionName}\"";

    /// <summary>
    /// The filter-qualifier form of a Project field name: lowercased with every space replaced by
    /// a dash, matching what the Projects filter bar itself inserts (verified live: field
    /// 'Wrighty dispatch - state' filters as 'wrighty-dispatch---state'). A quoted field name is
    /// not recognized as a qualifier and silently matches nothing.
    /// </summary>
    private static string ProjectFilterQualifier(string fieldName) =>
        fieldName.ToLowerInvariant().Replace(' ', '-');

    private static TrackerException ProjectViewConflict(
        GitHubProjectInfo project,
        string viewName,
        string reason) =>
        new(
            "PROJECT_VIEW_CONFLICT",
            $"Project {project.Owner}/{project.Number} has a conflicting '{viewName}': {reason}. Wrighty did not replace it.",
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
        ValidateDefaultAgent(request);
        if (request.SkipIssueForms && request.PublishIssueForms)
        {
            throw new TrackerException(
                "ARGUMENT_INVALID",
                "--skip-issue-forms and --publish-issue-forms cannot be used together.",
                2);
        }
        if (request.CheckOnly && request.PublishIssueForms)
        {
            throw new TrackerException(
                "ARGUMENT_INVALID",
                "--check and --publish-issue-forms cannot be used together.",
                2);
        }
    }

    private static void ValidateDefaultAgent(TrackerInitializationRequest request)
    {
        if (!request.DefaultAgentSpecified)
            return;
        if (request.CheckOnly)
            throw new TrackerException(
                "ARGUMENT_INVALID",
                "--default-agent cannot be combined with --check.",
                2);
        if (request.DefaultAgent is not null &&
            request.DefaultAgent.ToLowerInvariant() is not ("claude" or "codex" or "copilot"))
        {
            throw new TrackerException(
                "ARGUMENT_INVALID",
                "--default-agent must resolve to claude, codex, copilot, or none.",
                2);
        }
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
        request.CreateView || request.SkipIssueForms || request.PublishIssueForms;

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
        "The Project was resolved, but Wrighty initialization did not complete. " +
        $"Cause: {CauseCode(exception)}: {exception.Message} " +
        "The reported Project identity can be used safely on retry.",
        10,
        new Dictionary<string, object?>
        {
            ["configPath"] = configPath,
            ["repository"] = config.Repository,
            ["projectOwner"] = project.Owner,
            ["projectNumber"] = project.Number,
            ["projectUrl"] = project.Url,
            ["causeCode"] = CauseCode(exception),
            ["causeMessage"] = exception.Message
        },
        exception);

    private static string CauseCode(Exception exception) =>
        exception is TrackerException trackerException
            ? trackerException.Code
            : exception.GetType().Name;

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
        var config = ApplyDefaultAgent(
            existing ?? CreateLocalConfiguration(configPath, request),
            request);
        var configurationChanged = DefaultAgentChanged(existing, config, request);
        var root = Path.GetFullPath(
            config.LocalMarkdown!.Path,
            Path.GetDirectoryName(configPath)!);
        var steps = new List<string>();
        if (existing is null)
        {
            steps.Add($"create configuration '{configPath}'");
        }
        steps.Add($"create or validate the Local Markdown store '{root}'");
        AddDefaultAgentPlanStep(steps, existing, request);
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
                [],
                request.DefaultAgent,
                request.DefaultAgentSpecified),
            request,
            approval,
            cancellationToken);
        var actions = new List<string>();
        config = await PersistLocalConfigurationAsync(
            configPath, config, existing, request, configurationChanged, actions, cancellationToken);

        var initialized = await backend.InitializeAsync(config, request.CheckOnly, cancellationToken);
        actions.AddRange(initialized.Actions);
        return new TrackerInitializationResult(
            config,
            configPath,
            "Local Markdown",
            root,
            false,
            false,
            existing is null || configurationChanged || initialized.Changed,
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
                // Mirrors the record default, including the worker-queue column the default
                // pick-from ("Agent queue") requires.
                Statuses = request.Statuses is { Count: > 0 }
                ? request.Statuses
                : ["Todo", "Agent queue", "In Progress", "Done"],
                Priorities = request.Priorities ?? ["P0", "P1", "P2", "P3"]
            }
        };

    private async Task<TrackerConfig> PersistLocalConfigurationAsync(
        string configPath,
        TrackerConfig config,
        TrackerConfig? existing,
        TrackerInitializationRequest request,
        bool configurationChanged,
        ICollection<string> actions,
        CancellationToken cancellationToken)
    {
        if ((existing is not null && !configurationChanged) || request.CheckOnly)
        {
            return config;
        }

        await configStore.SaveAsync(configPath, config, cancellationToken);
        actions.Add(existing is null
            ? "wrote configuration"
            : $"updated worker.defaultAgent to '{config.EffectiveWorker.DefaultAgent ?? "none"}'");
        return config with { SourcePath = configPath };
    }

    private static TrackerConfig ApplyDefaultAgent(
        TrackerConfig config,
        TrackerInitializationRequest request)
    {
        if (!request.DefaultAgentSpecified)
            return config;

        var worker = (config.Worker ?? new WorkerConfig()) with
        {
            DefaultAgent = request.DefaultAgent?.ToLowerInvariant()
        };
        return config with { Worker = WorkerIsEmpty(worker) ? null : worker };
    }

    private static bool DefaultAgentChanged(
        TrackerConfig? existing,
        TrackerConfig planned,
        TrackerInitializationRequest request) =>
        existing is not null &&
        request.DefaultAgentSpecified &&
        !string.Equals(
            existing.EffectiveWorker.DefaultAgent,
            planned.EffectiveWorker.DefaultAgent,
            StringComparison.OrdinalIgnoreCase);

    private static void AddDefaultAgentPlanStep(
        List<string> steps,
        TrackerConfig? existing,
        TrackerInitializationRequest request)
    {
        if (!request.DefaultAgentSpecified)
            return;
        var value = request.DefaultAgent ?? "none";
        var prior = existing?.EffectiveWorker.DefaultAgent;
        string step;
        if (string.Equals(prior, request.DefaultAgent, StringComparison.OrdinalIgnoreCase))
            step = $"keep worker.defaultAgent as '{value}'";
        else if (request.DefaultAgent is null)
            step = "leave worker.defaultAgent unset";
        else
            step = $"set worker.defaultAgent to '{value}'";
        steps.Add(step);
    }

    private static bool WorkerIsEmpty(WorkerConfig worker) =>
        worker.DefaultAgent is null &&
        worker.WorkspaceMode is null &&
        worker.Completion is null &&
        worker.UsageFailure is null &&
        worker.DesktopSessions is null &&
        worker.SessionReportMode is null &&
        worker.Context is null &&
        worker.AgentPermissions is null &&
        worker.Agents is null &&
        worker.WorktreeRoot is null &&
        worker.BranchFormat is null &&
        worker.WorktreeNameFormat is null &&
        worker.HandoverComment is null &&
        !worker.ShareLocalPaths;

    private sealed record BackendSelection(
        string Backend,
        string Source,
        DiscoveredGitHubRepository? DiscoveredRepository);

    private sealed record GitHubSeed(TrackerConfig Config, string? ProjectTitle);

    private sealed record ProjectPlan(GitHubProjectInfo? Project, string Title);

    private sealed record ProjectResolution(GitHubProjectInfo Project, bool Created);

}
