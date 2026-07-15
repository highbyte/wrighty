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
    IReadOnlyList<string>? Priorities = null);

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

public sealed class TrackerInitializationService(
    ITrackerConfigStore configStore,
    IRepositoryDiscovery repositoryDiscovery,
    IGitHubInitializationClient github,
    IProjectClient projects,
    ITrackerBackendRegistry? backends = null)
{
    public async Task<TrackerInitializationResult> InitializeAsync(
        string workingDirectory,
        TrackerInitializationRequest request,
        CancellationToken cancellationToken)
    {
        ValidateArguments(request);
        var configPath = configStore.ResolvePath(workingDirectory, request.ConfigPath);
        var existing = await configStore.TryLoadPathAsync(configPath, cancellationToken);
        var isBootstrap = existing is null;

        var selectedBackend = existing?.Backend ?? request.Backend;
        DiscoveredGitHubRepository? discoveredForSelection = null;
        if (selectedBackend is null && request.Repository is null && request.LocalPath is null)
        {
            discoveredForSelection = await repositoryDiscovery.DiscoverAsync(
                workingDirectory,
                request.Remote ?? "origin",
                cancellationToken);
        }

        selectedBackend ??= request.LocalPath is not null || request.Statuses is not null || request.Priorities is not null
            ? "local-markdown"
            : request.Repository is not null || discoveredForSelection is not null
                ? "github"
                : "local-markdown";
        var backendSelection = existing is not null
            ? "configured"
            : request.Backend is not null
                ? "explicit"
                : request.Repository is not null || request.LocalPath is not null ||
                  request.Statuses is not null || request.Priorities is not null ||
                  discoveredForSelection is not null
                    ? "inferred"
                    : "defaulted";

        if (string.Equals(selectedBackend, "local-markdown", StringComparison.OrdinalIgnoreCase))
        {
            return await InitializeLocalAsync(
                workingDirectory,
                configPath,
                existing,
                request,
                backendSelection,
                cancellationToken);
        }

        if (!string.Equals(selectedBackend, "github", StringComparison.OrdinalIgnoreCase))
        {
            throw new TrackerException(
                "BACKEND_UNSUPPORTED",
                $"Unsupported backend '{selectedBackend}'. Available backends are 'github' and 'local-markdown'.",
                3);
        }

        if (request.LocalPath is not null || request.Statuses is not null || request.Priorities is not null)
        {
            throw new TrackerException(
                "OPTION_BACKEND_MISMATCH",
                "Local Markdown initialization options cannot be used with the github backend.",
                2);
        }

        TrackerConfig seed;
        string? projectTitle = null;
        if (existing is not null)
        {
            AssertExistingConfiguration(existing, request, configPath);
            seed = existing;
        }
        else
        {
            var discovered = discoveredForSelection ?? (request.Repository is null
                ? await repositoryDiscovery.DiscoverAsync(
                    workingDirectory,
                    request.Remote ?? "origin",
                    cancellationToken)
                : null);
            if (request.Repository is null && discovered is null)
            {
                throw RepositoryRequired(configPath);
            }

            var host = request.GitHubHost ?? discovered?.Host ?? "github.com";
            if (discovered is not null && request.GitHubHost is not null &&
                !string.Equals(discovered.Host, request.GitHubHost, StringComparison.OrdinalIgnoreCase))
            {
                throw new TrackerException(
                    "GIT_REMOTE_UNSUPPORTED",
                    $"Git remote host '{discovered.Host}' does not match --github-host '{request.GitHubHost}'.",
                    2);
            }

            var repository = request.Repository ?? discovered!.Repository;
            ValidateRepository(repository);
            seed = new TrackerConfig
            {
                Repository = repository,
                ProjectOwner = request.ProjectOwner,
                ProjectNumber = request.ProjectNumber ?? 1,
                LinkRepository = !request.NoLinkRepository,
                GitHubHost = host
            };
            projectTitle = request.ProjectTitle;
        }

        var repositoryInfo = await github.GetRepositoryAsync(
            seed.GitHubHost,
            seed.Repository,
            cancellationToken);
        var projectOwner = request.ProjectOwner ?? existing?.EffectiveProjectOwner ?? repositoryInfo.Owner;
        var linkRepository = existing?.LinkRepository ?? !request.NoLinkRepository;
        if (!string.Equals(projectOwner, repositoryInfo.Owner, StringComparison.OrdinalIgnoreCase))
        {
            linkRepository = false;
        }

        GitHubProjectInfo project;
        var createdProject = false;
        if (existing is not null || request.ProjectNumber.HasValue)
        {
            var number = existing?.ProjectNumber ?? request.ProjectNumber!.Value;
            project = await github.GetProjectAsync(
                seed.GitHubHost,
                projectOwner,
                number,
                cancellationToken)
                ?? throw new TrackerException(
                    "PROJECT_NOT_FOUND",
                    $"Project {projectOwner}/{number} was not found or is inaccessible.",
                    5);
        }
        else
        {
            projectTitle ??= $"Wrighty - {repositoryInfo.NameWithOwner}";
            var matches = await github.FindProjectsByTitleAsync(
                seed.GitHubHost,
                projectOwner,
                projectTitle,
                cancellationToken);
            if (matches.Count > 1)
            {
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

            if (matches.Count == 1)
            {
                project = matches[0];
            }
            else if (request.CheckOnly)
            {
                throw new TrackerException(
                    "PROJECT_INITIALIZATION_REQUIRED",
                    $"Project '{projectTitle}' does not exist. Run 'wrighty init' without --check to create it.",
                    5,
                    new Dictionary<string, object?>
                    {
                        ["projectOwner"] = projectOwner,
                        ["projectTitle"] = projectTitle
                    });
            }
            else
            {
                project = await github.CreateProjectAsync(
                    seed.GitHubHost,
                    projectOwner,
                    projectTitle,
                    cancellationToken);
                createdProject = true;
            }
        }

        var config = existing ?? seed with
        {
            Repository = repositoryInfo.NameWithOwner,
            ProjectOwner = projectOwner,
            ProjectNumber = project.Number,
            LinkRepository = linkRepository
        };
        var actions = new List<string>();
        if (createdProject)
        {
            actions.Add("created Project");
        }

        var linkedRepository = project.LinkedRepositories.Any(repository =>
            string.Equals(repository, repositoryInfo.NameWithOwner, StringComparison.OrdinalIgnoreCase));

        try
        {
            if (isBootstrap && !request.CheckOnly)
            {
                await configStore.SaveAsync(
                    configPath,
                    config,
                    createdProject ? CancellationToken.None : cancellationToken);
                actions.Add("wrote configuration");
            }

            if (linkRepository && !linkedRepository)
            {
                if (request.CheckOnly)
                {
                    throw new TrackerException(
                        "PROJECT_INITIALIZATION_REQUIRED",
                        $"Project {projectOwner}/{project.Number} is not linked to repository '{repositoryInfo.NameWithOwner}'. Run 'wrighty init'.",
                        5);
                }

                await github.LinkRepositoryAsync(
                    config.GitHubHost,
                    project.NodeId,
                    repositoryInfo.NodeId,
                    cancellationToken);
                linkedRepository = true;
                actions.Add("linked repository");
            }
            else if (!linkRepository &&
                     !string.Equals(projectOwner, repositoryInfo.Owner, StringComparison.OrdinalIgnoreCase))
            {
                actions.Add("repository link skipped because Project and repository owners differ");
            }

            var fieldResult = await projects.InitializeAsync(
                config,
                request.CheckOnly,
                cancellationToken);
            foreach (var archiveStatus in config.Archive.OnStatuses)
            {
                await projects.ValidateUpdateFieldsAsync(
                    config,
                    archiveStatus,
                    null,
                    false,
                    cancellationToken);
            }
            actions.AddRange(fieldResult.Actions);
            return new TrackerInitializationResult(
                config,
                configPath,
                project.Title,
                project.Url,
                createdProject,
                linkedRepository,
                isBootstrap || createdProject || actions.Contains("linked repository") || fieldResult.Changed,
                actions,
                backendSelection);
        }
        catch (Exception exception) when (
            isBootstrap && !request.CheckOnly && exception is not OperationCanceledException)
        {
            throw PartialInitialization(configPath, config, project, exception);
        }
    }

    private static void ValidateArguments(TrackerInitializationRequest request)
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
            (request.Repository is not null || request.ProjectOwner is not null ||
             request.ProjectNumber is not null || request.ProjectTitle is not null ||
             request.GitHubHost is not null || request.NoLinkRepositorySpecified))
        {
            throw new TrackerException(
                "OPTION_BACKEND_MISMATCH",
                "GitHub initialization options cannot be used with the local-markdown backend.",
                2);
        }

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

        if (request.Remote is not null && string.IsNullOrWhiteSpace(request.Remote))
        {
            throw new TrackerException("ARGUMENT_INVALID", "--remote cannot be empty.", 2);
        }

        if (request.GitHubHost is not null &&
            (string.IsNullOrWhiteSpace(request.GitHubHost) ||
             request.GitHubHost.Contains('/') ||
             request.GitHubHost.Any(char.IsWhiteSpace)))
        {
            throw new TrackerException(
                "ARGUMENT_INVALID",
                "--github-host must be a hostname without a URL scheme or path.",
                2);
        }

        if (request.Repository is not null)
        {
            ValidateRepository(request.Repository);
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
        string workingDirectory,
        string configPath,
        TrackerConfig? existing,
        TrackerInitializationRequest request,
        string backendSelection,
        CancellationToken cancellationToken)
    {
        if (backends is null)
        {
            throw new TrackerException(
                "BACKEND_UNSUPPORTED",
                "The local-markdown backend is not registered.",
                3);
        }

        if (request.Repository is not null || request.ProjectOwner is not null ||
            request.ProjectNumber is not null || request.ProjectTitle is not null ||
            request.GitHubHost is not null || request.NoLinkRepositorySpecified)
        {
            throw new TrackerException(
                "OPTION_BACKEND_MISMATCH",
                "GitHub initialization options cannot be used with the local-markdown backend.",
                2);
        }

        if (existing is not null)
        {
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

        var config = existing ?? new TrackerConfig
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

        var backend = backends.Get("local-markdown");
        var actions = new List<string>();
        if (existing is null && !request.CheckOnly)
        {
            await configStore.SaveAsync(configPath, config, cancellationToken);
            config = config with { SourcePath = configPath };
            actions.Add("wrote configuration");
        }

        var initialized = await backend.InitializeAsync(config, request.CheckOnly, cancellationToken);
        actions.AddRange(initialized.Actions);
        var root = Path.GetFullPath(
            config.LocalMarkdown!.Path,
            Path.GetDirectoryName(configPath)!);
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
}
