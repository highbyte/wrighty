using Highbyte.Wrighty.AgentContext;
using Highbyte.Wrighty.ApprovedContext;
using Highbyte.Wrighty.Claims;
using Highbyte.Wrighty.Configuration;
using Highbyte.Wrighty.Storage;
using Highbyte.Wrighty.Errors;
using Highbyte.Wrighty.Models;
using Highbyte.Wrighty.Web.Markdown;
using Highbyte.Wrighty.Workers;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.AspNetCore.Http;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace Highbyte.Wrighty.Web.Pages;

public sealed class IndexModel(
    TrackerService tracker,
    WebApplicationState state,
    MarkdownRenderer markdown,
    IProviderCapacityStore providerCapacity,
    IProviderCapacityProbeService providerCapacityProbe,
    WebAgentSessionServices agentSessions,
    WebOperationsServices operationsServices) : PageModel
{
    private const int MaximumBodyLength = 1_000_000;
    private const string ArgumentInvalid = "ARGUMENT_INVALID";
    private const string ContextApprovalPartial = "Shared/_ContextApproval";
    private static readonly JsonSerializerOptions IndentedJson = new() { WriteIndented = true };
    private readonly IWorkspaceInventory workspaceInventory = agentSessions.WorkspaceInventory;
    private readonly IReadOnlyDictionary<string, IAgentAdapter> adaptersByName =
        agentSessions.AdaptersByName;
    private readonly IAgentRuntimeCatalog agentRuntimeCatalog = agentSessions.RuntimeCatalog;
    private readonly ILocalAgentSessionLauncher localAgentSessionLauncher =
        agentSessions.Launcher;
    private readonly IRepositoryConfigurationService? repositoryConfiguration =
        operationsServices.RepositoryConfiguration;
    private readonly Highbyte.Wrighty.Settings.IUserConfigurationService? userConfiguration =
        operationsServices.UserConfiguration;
    private readonly StorageLocationCatalog? storageLocations = operationsServices.StorageLocations;
    private readonly GitHubProjectUrlResolver githubProjectUrls =
        operationsServices.GitHubProjectUrls ?? GitHubProjectUrlResolver.Unavailable;
    private readonly Workers.AgentModelDiscoveries? modelDiscoveries =
        operationsServices.ModelDiscoveries;
    private readonly IWorkerInstanceRegistry workerInstances =
        operationsServices.WorkerInstances;
    private readonly IContextApprovalService? contextApproval =
        operationsServices.ContextApproval;
    private readonly string[] agentOptions = agentSessions.AdaptersByName.Keys
        .Order(StringComparer.OrdinalIgnoreCase)
        .ToArray();

    public string WorkspacePath => state.WorkspacePath;

    public string WorkspaceDisplayPath => state.WorkspaceDisplayPath;

    public string BackendLabel => state.BackendLabel;

    public WebSurfaceCapabilities Capabilities => state.Capabilities;

    public IReadOnlyList<string> AgentOptions => agentOptions;

    public IReadOnlyList<string> PriorityOptions => state.Config.LocalMarkdown?.Priorities ?? [];

    public string WebAuthenticationMode =>
        state.TokenAuthenticationRequired ? "token" : "none";

    public async Task<IActionResult> OnGetOperationsAsync(
        [FromQuery] OperationsListInput input,
        CancellationToken cancellationToken) =>
        Partial("Shared/_Operations", await OperationsAsync(
            cancellationToken,
            query: OperationsListQuery.Parse(input)));

    public async Task<IActionResult> OnGetSettingsAsync(CancellationToken cancellationToken) =>
        Partial("Shared/_Settings", await SettingsAsync(cancellationToken));

    public async Task<IActionResult> OnPostValidateTargetAsync(
        [FromForm] OperationsListInput input,
        CancellationToken cancellationToken)
    {
        var query = OperationsListQuery.Parse(input);
        if (!state.Capabilities.GitHubTarget)
        {
            return Partial(
                "Shared/_Operations",
                await OperationsAsync(
                    cancellationToken,
                    new OperationsFeedback(
                        TargetErrorCode: "TARGET_VALIDATION_UNSUPPORTED",
                        TargetErrorMessage:
                            "Target validation is available only for the GitHub backend."),
                    query));
        }
        try
        {
            await tracker.InitializeAsync(
                state.Config,
                checkOnly: true,
                cancellationToken);
            return Partial(
                "Shared/_Operations",
                await OperationsAsync(
                    cancellationToken,
                    new OperationsFeedback(
                        TargetNotice:
                            "GitHub repository and Project validation passed without making changes."),
                    query));
        }
        catch (TrackerException exception)
        {
            WebDiagnostics.RetainFailure(HttpContext, exception.Code, exception);
            return Partial(
                "Shared/_Operations",
                await OperationsAsync(
                    cancellationToken,
                    new OperationsFeedback(
                        TargetErrorCode: exception.Code,
                        TargetErrorMessage: SafeMessage(exception)),
                    query));
        }
    }

    public async Task<IActionResult> OnGetContextApprovalAsync(
        string id,
        CancellationToken cancellationToken)
    {
        var view = await ContextApprovalAsync(
            new OperationsFeedback(SelectedContextId: id),
            cancellationToken);
        SetContextStateTrigger(view);
        return Partial(ContextApprovalPartial, view);
    }

    public async Task<IActionResult> OnPostApproveContextAsync(
        string id,
        CancellationToken cancellationToken)
    {
        if (!state.Capabilities.ContextApproval || contextApproval is null)
        {
            return Partial(
                ContextApprovalPartial,
                await ContextApprovalAsync(
                    new OperationsFeedback(
                        SelectedContextId: id,
                        ContextErrorCode: ExecutionContextResult.Codes.Unsupported,
                        ContextErrorMessage:
                            "Context approval is available only for the GitHub backend."),
                    cancellationToken));
        }

        try
        {
            var resolved = tracker.ResolveId(state.Config, id);
            var result = await contextApproval.ApproveAsync(
                state.Config,
                resolved,
                cancellationToken);
            var view = await ContextApprovalAsync(
                new OperationsFeedback(
                    SelectedContextId: resolved.Value,
                    ContextNotice: "Context approval renewed.",
                    ContextResult: result,
                    ContextRenewed: true),
                cancellationToken);
            SetContextStateTrigger(view);
            return Partial(
                ContextApprovalPartial,
                view);
        }
        catch (TrackerException exception)
        {
            WebDiagnostics.RetainFailure(HttpContext, exception.Code, exception);
            return Partial(
                ContextApprovalPartial,
                await ContextApprovalAsync(
                    new OperationsFeedback(
                        SelectedContextId: id,
                        ContextErrorCode: exception.Code,
                        ContextErrorMessage: SafeMessage(exception)),
                    cancellationToken));
        }
    }

    /// <summary>
    /// Saves a machine-local setting. Separate from the repository handler because the two scopes
    /// have separate files, separate revisions, and separate reasons to refuse — sharing a handler
    /// would mean one form's revision guarding the other's write.
    /// </summary>
    public async Task<IActionResult> OnPostUserConfigurationAsync(
        UserConfigurationFormInput input,
        CancellationToken cancellationToken)
    {
        if (!state.Capabilities.ConfigurationWrite || userConfiguration is null)
        {
            return await SettingsPartialAsync(
                new SettingsFeedback(
                    ConfigurationErrorCode: "USER_CONFIGURATION_UNAVAILABLE",
                    ConfigurationErrorMessage:
                        "User settings are not available to this web process."),
                cancellationToken);
        }

        try
        {
            var mutation = input.Operation switch
            {
                "hostLabel" => new Highbyte.Wrighty.Settings.HostLabelMutation(input.HostLabel),
                _ => throw new TrackerException(
                    "CONFIG_MUTATION_UNSUPPORTED",
                    "The requested user-settings operation is not supported.",
                    2)
            };

            var result = await userConfiguration.MutateAsync(
                input.Revision, mutation, dryRun: false, cancellationToken);
            return await SettingsPartialAsync(
                new SettingsFeedback(Notice: DescribeUserSave(result)),
                cancellationToken);
        }
        catch (TrackerException exception)
        {
            Response.StatusCode = Status(exception);
            WebDiagnostics.RetainFailure(HttpContext, exception.Code, exception);
            return await SettingsPartialAsync(
                new SettingsFeedback(
                    ConfigurationErrorCode: exception.Code,
                    ConfigurationErrorMessage: SafeMessage(exception)),
                cancellationToken);
        }
    }

    /// <summary>
    /// Says what changed and where it lands. Machine-local settings take effect on the next command
    /// rather than needing a restart, which is worth saying because the repository forms next to
    /// this one usually do.
    /// </summary>
    private static string DescribeUserSave(
        Highbyte.Wrighty.Settings.UserConfigurationMutationResult result) =>
        result.Saved
            ? $"Saved {result.After.SourcePath}. " +
              string.Join(" ", result.Changes.Select(change =>
                  $"{change.Id}: {Show(change.Before)} -> {Show(change.After)}.")) +
              " Applies to the next command; nothing needs restarting."
            : "No change to save.";

    private static string Show(object? value) =>
        value is null or "" ? "not set" : value.ToString()!;

    /// <summary>
    /// Sets or clears what one profile means for one agent on this machine.
    ///
    /// Refuses a model and effort the vendor says cannot go together, exactly as the CLI does, and
    /// for the same reason: without it the pair reaches a launch that fails at the API having spent
    /// a request. Where discovery cannot answer, the mapping is saved and the notice says it went
    /// unchecked — losing discovery must cost a check, never the ability to configure.
    /// </summary>
    public async Task<IActionResult> OnPostProfileMappingAsync(
        ProfileMappingFormInput input,
        CancellationToken cancellationToken)
    {
        if (!state.Capabilities.ConfigurationWrite || userConfiguration is null)
        {
            return await SettingsPartialAsync(
                new SettingsFeedback(
                    ConfigurationErrorCode: "USER_CONFIGURATION_UNAVAILABLE",
                    ConfigurationErrorMessage:
                        "User settings are not available to this web process."),
                cancellationToken);
        }

        try
        {
            var effort = input.Remove ? null : ParseEffortOrThrow(input.Effort);
            var model = input.Remove ? null : input.Model;
            RejectEffortTheVendorCouldNeverAccept(input.Agent, effort);
            var caution = await CheckMappingAsync(
                input.Agent, model, effort, cancellationToken);
            var result = await userConfiguration.MutateAsync(
                input.Revision,
                new Highbyte.Wrighty.Settings.ProfileMappingMutation(
                    input.Profile, input.Agent, model, effort),
                dryRun: false,
                cancellationToken);
            var notice = DescribeUserSave(result);
            return await SettingsPartialAsync(
                new SettingsFeedback(
                    Notice: caution is null ? notice : $"{notice} {caution}"),
                cancellationToken);
        }
        catch (TrackerException exception)
        {
            Response.StatusCode = Status(exception);
            WebDiagnostics.RetainFailure(HttpContext, exception.Code, exception);
            return await SettingsPartialAsync(
                new SettingsFeedback(
                    ConfigurationErrorCode: exception.Code,
                    ConfigurationErrorMessage: SafeMessage(exception)),
                cancellationToken);
        }
    }

    private async Task<IActionResult> SettingsPartialAsync(
        SettingsFeedback feedback, CancellationToken cancellationToken) =>
        Partial("Shared/_Settings", await SettingsAsync(cancellationToken, feedback));

    /// <summary>
    /// The web equivalent of the CLI's early gate: a level outside the vendor's whole flag surface
    /// could never work on any model, and for codex would not fail until the API had spent a
    /// request. Distinct from the per-model check below, which needs discovery; this one does not.
    /// </summary>
    private static void RejectEffortTheVendorCouldNeverAccept(
        string agent, Workers.ExecutionEffort? effort)
    {
        if (effort is not { } level ||
            Workers.AgentExecutionCapabilities.ForAgent(agent) is not { } capability ||
            capability.Supports(level))
        {
            return;
        }

        throw new TrackerException(ArgumentInvalid,
            $"Agent '{agent}' does not accept effort '{level.ToToken()}'. It supports: " +
            $"{string.Join(", ", capability.SupportedEfforts
                .OrderBy(value => value).Select(value => value.ToToken()))}.", 2);
    }

    /// <summary>
    /// The add row's model control, re-rendered when its agent selection changes. Instant in
    /// practice: the page load already discovered every agent, so this reads the cache.
    /// </summary>
    public async Task<IActionResult> OnGetMappingModelChoicesAsync(
        string agent, CancellationToken cancellationToken)
    {
        var catalog = modelDiscoveries is null
            ? Workers.AgentModelCatalog.Unavailable(
                agent, Workers.ModelDiscoveryFailure.NotInstalled)
            : await modelDiscoveries.DiscoverAsync(agent, cancellationToken);
        return Partial("Shared/_MappingModelControl", new MappingModelControl(catalog, null));
    }

    private static Workers.ExecutionEffort? ParseEffortOrThrow(string? effort)
    {
        if (string.IsNullOrWhiteSpace(effort))
        {
            return null;
        }

        if (!Workers.ExecutionEfforts.TryParse(effort, out var level))
        {
            throw new TrackerException(
                ArgumentInvalid,
                $"'{effort}' is not a known effort level.",
                2);
        }

        return level;
    }

    /// <summary>Null when nothing needs saying; a sentence when the operator should know something.</summary>
    private async Task<string?> CheckMappingAsync(
        string agent,
        string? model,
        Workers.ExecutionEffort? effort,
        CancellationToken cancellationToken)
    {
        if (modelDiscoveries is null || string.IsNullOrWhiteSpace(model) || effort is not { } level)
        {
            return null;
        }

        var catalog = await modelDiscoveries.DiscoverAsync(agent, cancellationToken);
        if (!catalog.Succeeded)
        {
            return $"{agent} could not be asked, so the model was not checked.";
        }

        if (catalog.Find(model) is not { } known)
        {
            return $"{agent} did not list a model '{model}'; saved anyway.";
        }

        if (known.Rejects(level.ToToken()))
        {
            throw new TrackerException(
                ArgumentInvalid,
                $"Model '{model}' does not accept effort '{level.ToToken()}'. " +
                (known.Efforts.Count > 0
                    ? $"It accepts: {string.Join(", ", known.Efforts)}."
                    : "It accepts no reasoning effort."),
                2);
        }

        return known.Effort == Workers.EffortSupport.Unknown
            ? $"{agent} does not report which efforts '{model}' accepts, so it was saved unchecked."
            : null;
    }

    public async Task<IActionResult> OnPostConfigurationAsync(
        ConfigurationFormInput input,
        CancellationToken cancellationToken)
    {
        var draft = new ConfigurationFormDraft(
            input.Operation,
            input.DefaultPickFrom,
            input.DefaultPickTo,
            input.DefaultFinishTo,
            input.DefaultAgent,
            input.WorkspaceMode,
            input.CompletionCommit,
            input.CompletionIntegration,
            input.ArchiveStatuses,
            input.ProtectNonHumanClaims,
            input.ApproveCanonicalization,
            input.ExecutionProfiles,
            input.DefaultExecutionProfile);
        if (!state.Capabilities.ConfigurationWrite ||
            state.Config.SourcePath is not { } configurationPath ||
            repositoryConfiguration is null)
        {
            return await SettingsPartialAsync(
                new SettingsFeedback(
                    ConfigurationErrorCode: "CONFIGURATION_UNAVAILABLE",
                    ConfigurationErrorMessage:
                        "Repository configuration is not available to this web process."),
                cancellationToken);
        }

        RepositoryConfigurationMutation mutation;
        try
        {
            mutation = input.Operation switch
            {
                "workflow" => new WorkflowDefaultsMutation(
                    Required(input.DefaultPickFrom, "defaultPickFrom"),
                    Required(input.DefaultPickTo, "defaultPickTo"),
                    Required(input.DefaultFinishTo, "defaultFinishTo")),
                "worker" => new WorkerDefaultsMutation(
                    SetDefaultAgent: true,
                    DefaultAgent: input.DefaultAgent,
                    WorkspaceMode: Required(input.WorkspaceMode, "workspaceMode")),
                "completion" => new CompletionPolicyMutation(
                    Required(input.CompletionCommit, "completionCommit"),
                    Required(input.CompletionIntegration, "completionIntegration")),
                "archive" => new ArchivePolicyMutation(
                    (input.ArchiveStatuses ?? string.Empty)
                        .Split(',', StringSplitOptions.RemoveEmptyEntries |
                                    StringSplitOptions.TrimEntries)),
                "web" => new WebPolicyMutation(input.ProtectNonHumanClaims),
                // Replace rather than add/remove: the form shows the whole vocabulary, so what the
                // operator submits *is* the list. The verbs exist in the CLI because typing them
                // one at a time is error-prone; a form has no such problem.
                "profiles" => new ExecutionProfilesMutation(
                    (input.ExecutionProfiles ?? string.Empty)
                        .Split(',', StringSplitOptions.RemoveEmptyEntries |
                                    StringSplitOptions.TrimEntries)
                        .Select(name => name.ToLowerInvariant())
                        .ToArray(),
                    SetDefault: true,
                    DefaultProfile: string.IsNullOrWhiteSpace(input.DefaultExecutionProfile)
                        ? null
                        : input.DefaultExecutionProfile,
                    ExecutionProfilesEdit.Replace),
                _ => throw new TrackerException(
                    "CONFIG_MUTATION_UNSUPPORTED",
                    "The requested configuration operation is not supported.",
                    2)
            };

            var result = await repositoryConfiguration.MutateAsync(
                configurationPath,
                input.Revision,
                mutation,
                input.ApproveCanonicalization,
                dryRun: false,
                cancellationToken);
            var notice = ConfigurationSaveNotice.Describe(result);
            Response.Headers["HX-Trigger"] = "wrighty:refresh";
            return await SettingsPartialAsync(
                new SettingsFeedback(Notice: notice),
                cancellationToken);
        }
        catch (TrackerException exception)
        {
            WebDiagnostics.RetainFailure(HttpContext, exception.Code, exception);
            return await SettingsPartialAsync(
                new SettingsFeedback(
                    ConfigurationErrorCode: exception.Code,
                    ConfigurationErrorMessage: SafeMessage(exception),
                    ConfigurationDraft: draft),
                cancellationToken);
        }
    }

    public async Task<IActionResult> OnGetBoardAsync(
        [FromQuery] BoardListInput input,
        CancellationToken cancellationToken)
    {
        var archiveScope = ParseScope(input.Scope);
        var query = BoardListQuery.Parse(input);
        try
        {
            var snapshot = await tracker.GetDashboardAsync(state.Config, archiveScope, cancellationToken);
            var (capacityViews, _) =
                await ProviderViewsAsync(cancellationToken);
            var responseRevision = ResponseRevision(
                snapshot.Revision,
                archiveScope,
                capacityViews,
                query.RevisionKey);
            var etag = $"\"{responseRevision}\"";
            if (Request.Headers.IfNoneMatch.Any(value => string.Equals(value, etag, StringComparison.Ordinal)))
            {
                // htmx treats 204 as an explicit no-swap response. Some browser/htmx
                // combinations process an empty 304 as replaceable content.
                return StatusCode(StatusCodes.Status204NoContent);
            }

            Response.Headers.ETag = etag;
            return Partial(
                "Shared/_Board",
                Board(
                    snapshot,
                    archiveScope,
                    responseRevision,
                    capacityViews,
                    query));
        }
        catch (TrackerException exception)
        {
            Response.StatusCode = Status(exception);
            WebDiagnostics.RetainFailure(HttpContext, exception.Code, exception);
            return Partial("Shared/_Board", new BoardPageModel([], [], [], [], input.Scope ?? "active", "error", exception.Code, SafeMessage(exception)));
        }
    }

    private async Task<OperationsPageModel> OperationsAsync(
        CancellationToken cancellationToken,
        OperationsFeedback? feedback = null,
        OperationsListQuery? query = null)
    {
        feedback ??= new OperationsFeedback();
        query ??= OperationsListQuery.Parse(new OperationsListInput());
        var itemsTask = LoadOperationalItemsAsync(query, cancellationToken);
        var workersTask = LoadWorkersAsync(cancellationToken);
        var targetTask = GitHubTargetAsync(cancellationToken);
        var itemsResult = await itemsTask;

        return new OperationsPageModel(
            state.Capabilities,
            state.Config.Backend,
            await targetTask,
            await workersTask,
            itemsResult.Items,
            itemsResult.ErrorCode,
            itemsResult.ErrorMessage,
            feedback.TargetNotice,
            feedback.TargetErrorCode,
            feedback.TargetErrorMessage,
            query,
            itemsResult.IsTruncated,
            AvailableAgents: FilterOptions(AgentOptions, [], query.Agent),
            AvailablePriorities: itemsResult.PriorityOptions,
            AvailableWorkflowStatuses: itemsResult.WorkflowStatusOptions);
    }

    private async Task<SettingsPageModel> SettingsAsync(
        CancellationToken cancellationToken,
        SettingsFeedback? feedback = null)
    {
        feedback ??= new SettingsFeedback();
        var configurationResult = await LoadConfigurationAsync(
            feedback.ConfigurationErrorCode,
            feedback.ConfigurationErrorMessage,
            cancellationToken);
        var user = await LoadUserConfigurationAsync(cancellationToken);
        var repositoryPath = state.Config.SourcePath ?? Path.Combine(
            state.WorkspacePath,
            TrackerConfigLoader.FileName);
        var defaultUserPaths = new Highbyte.Wrighty.Settings.UserConfigPaths(
            Environment.GetEnvironmentVariable("WRIGHTY_CONFIG_DIR"));
        var legacyUserSettingsPath = user is null
            ? defaultUserPaths.LegacySettingsPath
            : null;
        var locations = storageLocations is null
            ? []
            : storageLocations.Describe(
                repositoryPath,
                state.Config,
                user?.SourcePath ?? defaultUserPaths.SettingsPath,
                legacyUserSettingsPath);

        return new SettingsPageModel(
            state.Capabilities,
            state.Config.Backend,
            state.ActiveConfigurationRevision,
            configurationResult.Configuration,
            feedback.ConfigurationDraft,
            user,
            await LoadAgentModelsAsync(cancellationToken),
            configurationResult.Workers,
            locations,
            feedback.Notice,
            configurationResult.ErrorCode,
            configurationResult.ErrorMessage);
    }

    /// <summary>
    /// Worker processes for the operations cards, listed without reading the repository
    /// configuration. A registry that cannot answer renders as an empty list rather than taking
    /// the operations fragment down; configuration failures surface on the Settings tab, which is
    /// where they can be acted on.
    /// </summary>
    private async Task<IReadOnlyList<WorkerInstanceStatus>> LoadWorkersAsync(
        CancellationToken cancellationToken)
    {
        if (state.Config.SourcePath is not { } configurationPath)
        {
            return [];
        }

        try
        {
            return await workerInstances.ListAsync(configurationPath, cancellationToken);
        }
        catch (TrackerException)
        {
            return [];
        }
    }

    private async Task<ContextApprovalView> ContextApprovalAsync(
        OperationsFeedback feedback,
        CancellationToken cancellationToken)
    {
        WorkItemDetail? selected = null;
        if (state.Capabilities.ContextApproval &&
            !string.IsNullOrWhiteSpace(feedback.SelectedContextId))
        {
            try
            {
                selected = await tracker.GetAsync(
                    state.Config,
                    tracker.ResolveId(state.Config, feedback.SelectedContextId),
                    cancellationToken);
            }
            catch (TrackerException exception)
            {
                WebDiagnostics.RetainFailure(HttpContext, exception.Code, exception);
                if (feedback.ContextErrorCode is null)
                {
                    feedback = feedback with
                    {
                        ContextErrorCode = exception.Code,
                        ContextErrorMessage = SafeMessage(exception)
                    };
                }
            }
        }

        // Inspecting one item must not rebuild the Operations table. On GitHub that table performs
        // selective session hydration for every needs-attention and Done candidate; repeating all
        // of those remote reads before the requested context inspection made the 3-second HTMX
        // timeout almost inevitable and did work unrelated to the selected item.
        return await LoadContextApprovalAsync(feedback, selected, cancellationToken) ??
            new ContextApprovalView(
                feedback.SelectedContextId ?? string.Empty,
                feedback.SelectedContextId ?? "Unknown item",
                Url: null,
                ProjectedApproved: false,
                Approved: false,
                Code: ExecutionContextResult.Codes.Unsupported,
                Message: "Context approval is unavailable to this web process.",
                ApprovalSource: null,
                BaseApprovedAt: null,
                BatchCommentCutoff: null,
                Revision: null,
                IncludedCount: null,
                ExcludedCount: null,
                PendingCount: null,
                PendingUrls: []);
    }

    private void SetContextStateTrigger(ContextApprovalView view) =>
        Response.Headers["HX-Trigger-After-Swap"] = JsonSerializer.Serialize(
            new Dictionary<string, object>
            {
                ["wrighty:context-state"] = new
                {
                    automationKey = view.AutomationKey,
                    label = view.InspectedLabel,
                    appearance = view.InspectedAppearance,
                    title = view.InspectedTitle
                }
            });

    private async Task<ConfigurationLoadResult> LoadConfigurationAsync(
        string? errorCode,
        string? errorMessage,
        CancellationToken cancellationToken)
    {
        if (repositoryConfiguration is null ||
            state.Config.SourcePath is not { } configurationPath)
        {
            return new ConfigurationLoadResult(
                null,
                [],
                errorCode ?? "CONFIGURATION_UNAVAILABLE",
                errorMessage ??
                    "Repository configuration is not available to this web process.");
        }

        try
        {
            var configuration = await repositoryConfiguration.ReadPathAsync(
                configurationPath,
                cancellationToken);
            var workers = await workerInstances.ListAsync(
                configuration.SourcePath,
                cancellationToken);
            return new ConfigurationLoadResult(
                configuration,
                workers,
                errorCode,
                errorMessage);
        }
        catch (TrackerException exception)
        {
            return new ConfigurationLoadResult(
                null,
                [],
                errorCode ?? exception.Code,
                errorMessage ?? SafeMessage(exception));
        }
    }

    /// <summary>
    /// This machine's settings, or null when they cannot be read.
    ///
    /// Never fails the page. Machine-local settings are a side panel on the web console; a
    /// build without the service, or a settings file that is momentarily unreadable, must not take
    /// the board down with it.
    /// </summary>
    private async Task<Highbyte.Wrighty.Settings.UserConfigurationSnapshot?>
        LoadUserConfigurationAsync(CancellationToken cancellationToken)
    {
        if (userConfiguration is null)
        {
            return null;
        }

        try
        {
            return await userConfiguration.ReadAsync(cancellationToken);
        }
        catch (Exception exception) when (exception is TrackerException or IOException)
        {
            return null;
        }
    }

    /// <summary>
    /// Discovered models per agent, or an empty list when discovery is unavailable.
    ///
    /// Never fails the page and never blocks it: each probe spawns a vendor CLI, so this is the one
    /// part of rendering that can be slow. A vendor that cannot answer contributes an unavailable
    /// catalogue, which the form shows as a free-text model field.
    /// </summary>
    private async Task<IReadOnlyList<Workers.AgentModelCatalog>> LoadAgentModelsAsync(
        CancellationToken cancellationToken)
    {
        if (modelDiscoveries is null)
        {
            return [];
        }

        var catalogs = new List<Workers.AgentModelCatalog>();
        foreach (var agent in adaptersByName.Keys.OrderBy(name => name, StringComparer.OrdinalIgnoreCase))
        {
            try
            {
                catalogs.Add(await modelDiscoveries.DiscoverAsync(agent, cancellationToken));
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                catalogs.Add(Workers.AgentModelCatalog.Unavailable(
                    agent, Workers.ModelDiscoveryFailure.Unavailable));
            }
        }

        return catalogs;
    }

    private async Task<OperationalItemsLoadResult> LoadOperationalItemsAsync(
        OperationsListQuery query,
        CancellationToken cancellationToken)
    {
        var configuredPriorities = state.Config.LocalMarkdown?.Priorities ?? [];
        var configuredStatuses = state.Config.LocalMarkdown?.Statuses ?? [];
        if (!state.Capabilities.OperationalItems)
            return new OperationalItemsLoadResult(
                [], null, null, false,
                FilterOptions(configuredPriorities, [], query.Priority),
                FilterOptions(configuredStatuses, [], query.WorkflowStatus));

        try
        {
            var items = state.Capabilities.LocalBoard
                ? await LoadLocalOperationalItemsAsync(cancellationToken)
                : await LoadGitHubOperationalItemsAsync(cancellationToken);
            var priorityOptions = FilterOptions(
                configuredPriorities,
                items.Items.Select(item => item.Priority),
                query.Priority);
            var workflowStatusOptions = FilterOptions(
                configuredStatuses,
                items.Items.Select(item => item.Status),
                query.WorkflowStatus);
            var filtered = items.Items
                .Where(item => query.Matches(item, DateTimeOffset.UtcNow,
                    state.Capabilities.LocalBoard))
                .Order(new OperationsItemComparer(
                    query.Sort,
                    state.Config.LocalMarkdown?.Priorities ?? []))
                .ToArray();
            return new OperationalItemsLoadResult(
                filtered, null, null, items.IsTruncated,
                priorityOptions, workflowStatusOptions);
        }
        catch (TrackerException exception)
        {
            return new OperationalItemsLoadResult(
                [],
                exception.Code,
                SafeMessage(exception),
                false,
                FilterOptions(configuredPriorities, [], query.Priority),
                FilterOptions(configuredStatuses, [], query.WorkflowStatus));
        }
    }

    private static IReadOnlyList<string> FilterOptions(
        IEnumerable<string> preferred,
        IEnumerable<string?> observed,
        string? selected)
    {
        var additional = observed
            .Where(value => !string.IsNullOrWhiteSpace(value))
            .Select(value => value!.Trim())
            .Order(StringComparer.OrdinalIgnoreCase);
        return preferred
            .Concat(additional)
            .Append(selected)
            .Where(value => !string.IsNullOrWhiteSpace(value))
            .Select(value => value!.Trim())
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    private async Task<OperationalItemsPage> LoadLocalOperationalItemsAsync(
        CancellationToken cancellationToken)
    {
        var loaded = await tracker.ListOperationalAsync(
                state.Config,
                new ListWorkItemsRequest(Status: null, Limit: 101),
                cancellationToken);
        return new OperationalItemsPage(
            loaded.Take(100)
            .Select(item => OperationsItem(item, contextApprovalFieldApproved: null))
            .ToArray(),
            loaded.Count > 100);
    }

    private async Task<OperationalItemsPage> LoadGitHubOperationalItemsAsync(
        CancellationToken cancellationToken)
    {
        var loaded = await tracker.ListAsync(
            state.Config,
            status: null,
            limit: 101,
            cancellationToken);
        var summaries = loaded.Take(100).ToArray();

        // A GitHub Project list is deliberately a cheap summary read. Hydrating every row with
        // its issue comment chain would turn one Operations refresh into as many as 100 extra
        // GitHub reads. The dispatch projection already identifies the rows where a retained
        // session needs a person. Done rows are the other deliberate candidate set: once they are
        // unclaimed, Wrighty lets the operator open a retained session without taking ownership
        // of the finished item. Only those two sets pay for the local ownership/session state
        // required to decide whether this installation can open it.
        var recoveryStates = await Task.WhenAll(summaries
            .Where(item =>
                IsWorkflowStatus(item.DispatchState, DispatchStates.NeedsAttention) ||
                IsWorkflowStatus(item.Status, state.Config.DefaultFinishTo))
            .Select(item => tracker.GetOperationalAsync(
                state.Config, item.Id, cancellationToken)));
        var recoveryById = recoveryStates.ToDictionary(
            item => item.Item.Id.Value,
            StringComparer.Ordinal);

        return new OperationalItemsPage(summaries.Select(item =>
        {
            recoveryById.TryGetValue(item.Id.Value, out var recovery);
            return new OperationsItemView(
                item.Id.Value,
                item.Title,
                item.Status,
                item.Priority,
                item.DispatchState,
                GitHubOperationalStatus(item, state.Config),
                RecoveryLabel(recovery?.Session),
                SafeExternalUrl(item.Url),
                item.ContextApprovalFieldApproved,
                recovery is null ? [] : OperationsSessionActions(recovery),
                item.AgentPolicy,
                item.CreatedAt,
                item.UpdatedAt);
        }).ToArray(), loaded.Count > 100);
    }

    private OperationsItemView OperationsItem(
        WorkItemOperationalState item,
        bool? contextApprovalFieldApproved) => new(
        item.Item.Id.Value,
        item.Item.Title,
        item.Item.Status,
        item.Item.Priority,
        item.Item.DispatchState,
        item.OperationalStatus,
        RecoveryLabel(item.Session),
        SafeExternalUrl(item.Item.Url),
        contextApprovalFieldApproved,
        OperationsSessionActions(item),
        item.Item.AgentPolicy,
        item.Item.CreatedAt,
        item.Item.UpdatedAt,
        item.Claim.State == ClaimOwnershipState.Unclaimed
            ? "unclaimed"
            : ClaimantKindLabel(item.Claim) ?? "unknown",
        item.Claim.State);

    private IReadOnlyList<CardActionView> OperationsSessionActions(
        WorkItemOperationalState value)
    {
        if (value.Session is not
            {
                IsComplete: true,
                FromCurrentInstallation: true
            } session)
        {
            return [];
        }

        if (value.OperationalStatus != OperationalStatuses.NeedsAttention &&
            !IsUnmanagedTerminal(value.Item, value.Claim))
        {
            return [];
        }

        return LaunchCardActions(
            new DashboardWorkItem(Summary(value.Item), value.Claim, session),
            session);
    }

    private static WorkItemSummary Summary(WorkItemDetail item) => new(
        item.Id,
        item.Title,
        item.Url,
        item.Status,
        item.Priority,
        item.Archived,
        item.AutomaticExecutionAllowed,
        item.AgentPolicy,
        item.DispatchState,
        item.UpdatedAt,
        item.ContextApprovalFieldApproved,
        item.ExecutionProfile,
        item.CreatedAt);

    private static string? RecoveryLabel(AgentSessionRecord? session) =>
        session is { IsComplete: true, FromCurrentInstallation: true, Agent: { } agent }
            ? $"{AgentDisplayName(agent)} session retained here"
            : null;

    private async Task<ContextApprovalView?> LoadContextApprovalAsync(
        OperationsFeedback feedback,
        WorkItemDetail? selected,
        CancellationToken cancellationToken)
    {
        if (!state.Capabilities.ContextApproval ||
            string.IsNullOrWhiteSpace(feedback.SelectedContextId))
            return null;

        var id = selected?.Id.Value ?? feedback.SelectedContextId;
        var title = selected?.Title ?? id;
        var projected = feedback.ContextRenewed ||
            selected?.ContextApprovalFieldApproved is true;

        if (feedback.ContextErrorCode is not null || contextApproval is null)
        {
            return new ContextApprovalView(
                id,
                title,
                SafeExternalUrl(selected?.Url),
                projected,
                Approved: false,
                feedback.ContextErrorCode ?? ExecutionContextResult.Codes.Unsupported,
                feedback.ContextErrorMessage ??
                    "Context approval diagnostics are unavailable to this web process.",
                ApprovalSource: null,
                BaseApprovedAt: null,
                BatchCommentCutoff: null,
                Revision: null,
                IncludedCount: null,
                ExcludedCount: null,
                PendingCount: null,
                PendingUrls: [],
                feedback.ContextNotice);
        }

        WorkItemId resolved;
        ExecutionContextResult result;
        try
        {
            resolved = tracker.ResolveId(state.Config, id);
            result = feedback.ContextResult ?? await contextApproval.InspectAsync(
                state.Config,
                resolved,
                cancellationToken);
        }
        catch (TrackerException exception)
        {
            WebDiagnostics.RetainFailure(HttpContext, exception.Code, exception);
            return new ContextApprovalView(
                id,
                title,
                SafeExternalUrl(selected?.Url),
                projected,
                Approved: false,
                exception.Code,
                SafeMessage(exception),
                ApprovalSource: null,
                BaseApprovedAt: null,
                BatchCommentCutoff: null,
                Revision: null,
                IncludedCount: null,
                ExcludedCount: null,
                PendingCount: null,
                PendingUrls: [],
                feedback.ContextNotice);
        }
        var diagnostics = result.EffectiveDiagnostics;
        return new ContextApprovalView(
            resolved.Value,
            title,
            SafeExternalUrl(selected?.Url),
            projected,
            result.IsApproved,
            result.Code,
            result.Message,
            diagnostics?.Approval.Source.WireName(),
            diagnostics?.Approval.BaseApprovedAt,
            diagnostics?.Approval.BatchCommentCutoff,
            diagnostics?.Revision?.ShortDigest,
            diagnostics?.IncludedCount,
            diagnostics?.ExcludedCount,
            diagnostics?.PendingCount,
            (result.PendingUrls ?? [])
                .Select(SafeExternalUrl)
                .Where(url => url is not null)
                .Select(url => url!)
                .ToArray(),
            feedback.ContextNotice);
    }

    /// <summary>
    /// A machine-local edit. Its own type, carrying its own revision: the settings file and the
    /// repository file change independently, so one form's staleness says nothing about the other.
    /// </summary>
    public sealed class ProfileMappingFormInput
    {
        public string Revision { get; set; } = string.Empty;
        public string Profile { get; set; } = string.Empty;
        public string Agent { get; set; } = string.Empty;
        public string? Model { get; set; }
        public string? Effort { get; set; }

        // The row's Remove button. A dedicated flag rather than "submit with both fields empty":
        // the row's selects still carry values when Remove is pressed, and removal must not depend
        // on the operator blanking them first.
        public bool Remove { get; set; }
    }

    public sealed class UserConfigurationFormInput
    {
        public string Operation { get; set; } = string.Empty;
        public string Revision { get; set; } = string.Empty;
        public string? HostLabel { get; set; }
    }

    public sealed class ConfigurationFormInput
    {
        public string Operation { get; set; } = string.Empty;
        public string Revision { get; set; } = string.Empty;
        public string? DefaultPickFrom { get; set; }
        public string? DefaultPickTo { get; set; }
        public string? DefaultFinishTo { get; set; }
        public string? DefaultAgent { get; set; }
        public string? WorkspaceMode { get; set; }
        public string? CompletionCommit { get; set; }
        public string? CompletionIntegration { get; set; }
        public string? ArchiveStatuses { get; set; }
        public string? ExecutionProfiles { get; set; }
        public string? DefaultExecutionProfile { get; set; }
        public bool ProtectNonHumanClaims { get; set; }
        public bool ApproveCanonicalization { get; set; }
    }

    private sealed record OperationsFeedback(
        string? TargetNotice = null,
        string? TargetErrorCode = null,
        string? TargetErrorMessage = null,
        string? SelectedContextId = null,
        string? ContextNotice = null,
        string? ContextErrorCode = null,
        string? ContextErrorMessage = null,
        ExecutionContextResult? ContextResult = null,
        bool ContextRenewed = false);

    private sealed record SettingsFeedback(
        string? Notice = null,
        string? ConfigurationErrorCode = null,
        string? ConfigurationErrorMessage = null,
        ConfigurationFormDraft? ConfigurationDraft = null);

    private sealed record ConfigurationLoadResult(
        RepositoryConfigurationSnapshot? Configuration,
        IReadOnlyList<WorkerInstanceStatus> Workers,
        string? ErrorCode,
        string? ErrorMessage);

    private sealed record OperationalItemsLoadResult(
        IReadOnlyList<OperationsItemView> Items,
        string? ErrorCode,
        string? ErrorMessage,
        bool IsTruncated,
        IReadOnlyList<string> PriorityOptions,
        IReadOnlyList<string> WorkflowStatusOptions);

    private sealed record OperationalItemsPage(
        IReadOnlyList<OperationsItemView> Items,
        bool IsTruncated);

    private static string Required(string? value, string name)
    {
        if (!string.IsNullOrWhiteSpace(value))
            return value.Trim();
        throw new TrackerException(
            "CONFIG_INVALID",
            $"{name} cannot be empty.",
            3);
    }

    private static string? GitHubTargetUrl(TrackerConfig config)
    {
        if (!string.Equals(config.Backend, "github", StringComparison.OrdinalIgnoreCase) ||
            string.IsNullOrWhiteSpace(config.Repository))
            return null;
        var segments = config.Repository
            .Split('/', StringSplitOptions.RemoveEmptyEntries)
            .Select(Uri.EscapeDataString);
        if (!Uri.TryCreate(
                $"{Uri.UriSchemeHttps}{Uri.SchemeDelimiter}{config.GitHubHost}",
                UriKind.Absolute,
                out var origin) ||
            origin.AbsolutePath != "/" ||
            !string.IsNullOrEmpty(origin.Query) ||
            !string.IsNullOrEmpty(origin.Fragment))
            return null;
        return new UriBuilder(origin)
        {
            Path = string.Join('/', segments)
        }.Uri.AbsoluteUri;
    }

    private async Task<GitHubTargetView?> GitHubTargetAsync(
        CancellationToken cancellationToken)
    {
        var repositoryUrl = GitHubTargetUrl(state.Config);
        if (repositoryUrl is null)
            return null;

        return new GitHubTargetView(
            state.Config.GitHubHost,
            state.Config.Repository,
            repositoryUrl,
            $"Project {state.Config.EffectiveProjectOwner}/#{state.Config.ProjectNumber}",
            await githubProjectUrls.ResolveAsync(state.Config, cancellationToken));
    }

    private static string? SafeExternalUrl(string? value) =>
        Uri.TryCreate(value, UriKind.Absolute, out var uri) &&
        (string.Equals(uri.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase) ||
         string.Equals(uri.Scheme, Uri.UriSchemeHttp, StringComparison.OrdinalIgnoreCase))
            ? uri.AbsoluteUri
            : null;

    private static string GitHubOperationalStatus(
        WorkItemSummary item,
        TrackerConfig config) =>
        item.DispatchState switch
        {
            DispatchStates.NeedsAttention => OperationalStatuses.NeedsAttention,
            DispatchStates.Queued => OperationalStatuses.Queued,
            DispatchStates.RetryScheduled => OperationalStatuses.RetryScheduled,
            DispatchStates.HandoffQueued => OperationalStatuses.HandoffQueued,
            _ when item.AutomaticExecutionAllowed &&
                   string.Equals(
                       item.Status,
                       config.DefaultPickFrom,
                       StringComparison.OrdinalIgnoreCase) =>
                OperationalStatuses.Ready,
            _ => OperationalStatuses.None
        };

    public async Task<IActionResult> OnGetProviderCapacityAsync(
        CancellationToken cancellationToken)
    {
        try
        {
            var (_, providers) = await ProviderViewsAsync(cancellationToken);
            var revision = ProviderRevision(providers);
            var etag = $"\"{revision}\"";
            if (Request.Headers.IfNoneMatch.Any(value =>
                    string.Equals(value, etag, StringComparison.Ordinal)))
                return StatusCode(StatusCodes.Status204NoContent);

            Response.Headers.ETag = etag;
            return Partial(
                "Shared/_ProviderCapacity",
                new ProviderCapacityPageModel(providers, revision));
        }
        catch (TrackerException exception)
        {
            Response.StatusCode = Status(exception);
            WebDiagnostics.RetainFailure(HttpContext, exception.Code, exception);
            return Partial(
                "Shared/_ProviderCapacity",
                new ProviderCapacityPageModel(
                    [],
                    "error",
                    ErrorCode: exception.Code,
                    ErrorMessage: SafeMessage(exception)));
        }
    }

    public async Task<IActionResult> OnGetItemAsync(string id, CancellationToken cancellationToken)
    {
        try { return Partial("Shared/_ItemDetail", await Item(id, cancellationToken: cancellationToken)); }
        catch (TrackerException exception) { return KnownError(exception); }
    }

    public async Task<IActionResult> OnPostProbeProviderAsync(
        string agent,
        string? id,
        string? surface,
        CancellationToken cancellationToken)
    {
        try
        {
            var availability = await providerCapacityProbe.ProbeProviderAsync(
                state.Config,
                agent,
                state.Config.SourcePath is { } sourcePath
                    ? Path.GetDirectoryName(Path.GetFullPath(sourcePath)) ??
                      Directory.GetCurrentDirectory()
                    : Directory.GetCurrentDirectory(),
                _ => Task.CompletedTask,
                cancellationToken);
            var label = ProviderCapacityView.From(availability).AgentLabel;
            var notice = availability.State == ProviderCapacityState.Available
                ? $"{label} capacity is available. Automatic {label} work is enabled."
                : $"{label} still reports exhausted capacity. Automatic work remains paused.";
            Response.Headers["HX-Trigger"] = "wrighty:refresh";
            if (string.Equals(surface, "header", StringComparison.OrdinalIgnoreCase))
                return await ProviderCapacityProbeAsync(
                    notice,
                    cancellationToken);
            if (!string.IsNullOrWhiteSpace(id))
            {
                return Partial(
                    "Shared/_ItemDetail",
                    await Item(id, notice, cancellationToken: cancellationToken));
            }
            return await ProviderCapacityProbeAsync(notice, cancellationToken);
        }
        catch (TrackerException exception)
        {
            if (string.Equals(surface, "header", StringComparison.OrdinalIgnoreCase))
            {
                Response.StatusCode = Status(exception);
                return await ProviderCapacityProbeAsync(
                    null,
                    cancellationToken,
                    exception);
            }
            if (!string.IsNullOrWhiteSpace(id))
                return await ItemError(id, exception, cancellationToken);
            Response.StatusCode = Status(exception);
            return await ProviderCapacityProbeAsync(
                null,
                cancellationToken,
                exception);
        }
    }

    public async Task<IActionResult> OnPostProbeAllProvidersAsync(
        CancellationToken cancellationToken)
    {
        try
        {
            var agents = providerCapacityProbe.SupportedAgents
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .OrderBy(value => value, StringComparer.OrdinalIgnoreCase)
                .ToArray();
            var repositoryPath = state.Config.SourcePath is { } sourcePath
                ? Path.GetDirectoryName(Path.GetFullPath(sourcePath)) ??
                  Directory.GetCurrentDirectory()
                : Directory.GetCurrentDirectory();
            var availability = await Task.WhenAll(
                agents.Select(agent => providerCapacityProbe.ProbeProviderAsync(
                    state.Config,
                    agent,
                    repositoryPath,
                    _ => Task.CompletedTask,
                    cancellationToken)));
            var availableCount = availability.Count(value =>
                value.State == ProviderCapacityState.Available);
            var unavailableCount = availability.Count(value =>
                value.State == ProviderCapacityState.UnavailableUntil);
            var probingCount = availability.Count(value =>
                value.State == ProviderCapacityState.ProbeInProgress);
            var notice =
                $"Checked {availability.Length} providers: {availableCount} available, " +
                $"{unavailableCount} unavailable" +
                (probingCount > 0 ? $", {probingCount} already being probed" : string.Empty) +
                ".";
            Response.Headers["HX-Trigger"] = "wrighty:refresh";
            return await ProviderCapacityProbeAsync(notice, cancellationToken);
        }
        catch (TrackerException exception)
        {
            Response.StatusCode = Status(exception);
            return await ProviderCapacityProbeAsync(
                null,
                cancellationToken,
                exception);
        }
    }

    public IActionResult OnGetCreate()
    {
        var local = state.Config.LocalMarkdown
            ?? throw new TrackerException(
                "WEB_BACKEND_UNSUPPORTED",
                "Web creation is supported only by the Local Markdown backend.",
                2);
        return Partial("Shared/_CreateForm", new CreateItemPageModel(
            string.Empty,
            string.Empty,
            state.Config.DefaultPickFrom,
            null,
            false,
            null,
            CreationAttempt.NormalizeOrCreate(null),
            local.Statuses,
            local.Priorities));
    }

    public async Task<IActionResult> OnPostCreateAsync(
        string title,
        string? body,
        string status,
        string? priority,
        bool automaticExecutionAllowed,
        string? agentPolicy,
        string creationAttemptId,
        CancellationToken cancellationToken)
    {
        var local = state.Config.LocalMarkdown
            ?? throw new TrackerException(
                "WEB_BACKEND_UNSUPPORTED",
                "Web creation is supported only by the Local Markdown backend.",
                2);
        body ??= string.Empty;
        var draft = new CreateItemPageModel(
            title,
            body,
            status,
            string.IsNullOrWhiteSpace(priority) ? null : priority,
            automaticExecutionAllowed,
            string.IsNullOrWhiteSpace(agentPolicy) ? null : agentPolicy,
            creationAttemptId,
            local.Statuses,
            local.Priorities);

        if (body.Length > MaximumBodyLength)
        {
            Response.StatusCode = StatusCodes.Status400BadRequest;
            return Partial("Shared/_CreateForm", draft with
            {
                ErrorCode = "ARGUMENT_INVALID",
                ErrorMessage = "Markdown body must not exceed 1,000,000 characters."
            });
        }

        try
        {
            var result = await tracker.CreateAsync(
                state.Config,
                new CreateWorkItemRequest(
                    title,
                    body,
                    status,
                    draft.Priority,
                    AutomaticExecutionAllowed: automaticExecutionAllowed,
                    AgentPolicy: draft.AgentPolicy),
                creationAttemptId,
                cancellationToken);
            Response.Headers["HX-Trigger"] = "wrighty:refresh";
            return Partial(
                "Shared/_ItemDetail",
                await Item(
                    result.Id.Value,
                    result.Disposition == CreateDisposition.Resumed
                        ? "Creation resumed without allocating a duplicate item."
                        : "Item created. Worker processing was not started.",
                    cancellationToken: cancellationToken));
        }
        catch (TrackerException exception)
        {
            Response.StatusCode = Status(exception);
            WebDiagnostics.RetainFailure(HttpContext, exception.Code, exception);
            return Partial("Shared/_CreateForm", draft with
            {
                ErrorCode = exception.Code,
                ErrorMessage = SafeMessage(exception)
            });
        }
    }

    public async Task<IActionResult> OnGetEditAsync(string id, CancellationToken cancellationToken)
    {
        try
        {
            await EnsureWebMutationAllowed(id, cancellationToken);
            return Partial("Shared/_EditForm", (await Item(id, editing: true, cancellationToken: cancellationToken)) with { Editing = true });
        }
        catch (TrackerException exception) { return await ItemError(id, exception, cancellationToken); }
    }

    /// <param name="fromCard">
    /// Set by the board's Edit action. It makes the edit a bounded gesture — save or cancel, both
    /// releasing — matching every other card action. The panel's own claim button leaves it unset,
    /// because there the operator is working inside the item and the full action set is the point.
    /// The needs-attention card's Clarify also leaves it unset: its natural next step is one of the
    /// session hand-backs, which the reduced set would remove.
    /// </param>
    /// <summary>
    /// Whether a human at this installation may simply pick up an item the worker has handed to
    /// them, without an explicit takeover.
    ///
    /// Needs-attention is the worker's own statement that it has stopped and a person is needed.
    /// Once it says that, the claim it left is residue, not an occupant — and a claim held by this
    /// installation under a different claimant is usually a previous web console process, since the
    /// web console mints a claimant per process. Wrighty is local-first for small trusted teams; the
    /// party worth refusing is another *installation*, which the claim service refuses anyway.
    ///
    /// Two things narrow it. The needs-attention marker, without which an item claimed by a run
    /// that is still going would be fair game, and that is a genuine occupant. And the claim being
    /// a *human* one — web console residue. An agent's claim stays behind
    /// <c>web.protectNonHumanClaims</c>, which exists precisely so the web console cannot take an
    /// agent's item without the operator saying so; that setting is the operator's to relax, not
    /// this method's to route around.
    /// </summary>
    private bool CanTakeOverHandedOffItem(
        WorkItemDetail item,
        WorkItemClaimSummary claim) =>
        claim.State == ClaimOwnershipState.OwnedByCurrent &&
        string.Equals(
            item.DispatchState, DispatchStates.NeedsAttention, StringComparison.OrdinalIgnoreCase) &&
        (ClaimantKinds.FromStorageValue(claim.ClaimantKind) == ClaimantKind.Human ||
         !state.Config.EffectiveWeb.ProtectNonHumanClaims);

    public async Task<IActionResult> OnPostClaimAsync(
        string id, bool fromCard, CancellationToken cancellationToken)
    {
        try
        {
            var resolved = tracker.ResolveId(state.Config, id);
            var session = await tracker.GetAgentSessionAsync(
                state.Config, resolved, cancellationToken);
            var editable = await tracker.GetEditableAsync(state.Config, resolved, cancellationToken);
            var notice = "Claimed by this Wrighty installation.";
            ClaimResult result;
            if (CanTakeOverHandedOffItem(editable.Item, editable.Claim))
            {
                // The worker wrote needs-attention when it stood down: that marker *is* the
                // hand-off to a person. Requiring a takeover ceremony to answer it protects nobody
                // — the claim left behind belongs to this installation, most often a web console
                // process that has since exited, and claimant identity here is per-process rather
                // than per-person. Another installation is the real other party, and is refused
                // below by the claim service.
                //
                // Takeover rather than claim, because it carries the recorded agent and session
                // forward, and because it fences: anything that did still hold this item is
                // rejected on its next write instead of losing work silently.
                result = await tracker.TakeoverAsync(
                    state.Config,
                    resolved,
                    state.ClaimantContext,
                    state.TryHandle(resolved.Value, out var current) ? current.ClaimToken : null,
                    cancellationToken);
                notice = "Claimed for editing. The recorded agent session was preserved.";
            }
            else if (session is { IsComplete: true, FromCurrentInstallation: true })
            {
                // Recover the durable address under an agent claim first, then rotate it to the
                // human web claimant. A direct human acquisition cannot carry agent metadata.
                var recoveryContext = new AgentExecutionContext(
                    session.Agent,
                    session.SessionId,
                    AgentContextSource.ExplicitOption,
                    ClaimantKind: ClaimantKind.Agent,
                    ClaimantId: $"agent:web-recover:{Guid.NewGuid():N}");
                var recovered = await tracker.ClaimAsync(
                    state.Config, resolved, recoveryContext, cancellationToken);
                recovered = await tracker.RenewClaimAsync(
                    state.Config,
                    resolved,
                    new ClaimHandle(recoveryContext, recovered.ClaimToken),
                    session.WorkspacePath,
                    session.SessionId,
                    cancellationToken);
                result = await tracker.TakeoverAsync(
                    state.Config,
                    resolved,
                    state.ClaimantContext,
                    recovered.ClaimToken,
                    cancellationToken);
                notice = "Claimed for editing. The recorded agent session was preserved.";
            }
            else
            {
                result = await tracker.ClaimAsync(
                    state.Config, resolved, state.ClaimantContext, cancellationToken);
            }
            state.Retain(resolved.Value, result);
            return Partial("Shared/_EditForm", await Item(
                id, notice, editing: true, cardEntry: fromCard,
                cancellationToken: cancellationToken));
        }
        catch (TrackerException exception)
        {
            try
            {
                Response.StatusCode = Status(exception);
                return Partial("Shared/_ItemDetail", await Item(id, error: exception, cancellationToken: cancellationToken));
            }
            catch (TrackerException) { return KnownError(exception); }
        }
    }

    public async Task<IActionResult> OnPostSaveAsync(
        string id,
        string expectedRevision,
        string expectedClaimGeneration,
        string title,
        string? body,
        string status,
        string? priority,
        bool automaticExecutionAllowed,
        string? agentPolicy,
        string? executionProfile,
        string action,
        bool fromCard,
        CancellationToken cancellationToken)
    {
        ClaimHandle handle;
        try
        {
            handle = await PrepareSaveAsync(
                id, expectedClaimGeneration, cancellationToken);
        }
        catch (TrackerException exception) { return await ItemError(id, exception, cancellationToken); }

        if (string.Equals(action, "release", StringComparison.Ordinal))
        {
            return fromCard
                ? await ReleaseCardDraftAsync(id, handle, cancellationToken)
                : await ReleaseDraftAsync(id, handle, cancellationToken);
        }

        body ??= string.Empty;
        if (body.Length > MaximumBodyLength)
        {
            var tooLarge = new TrackerException("ARGUMENT_INVALID", "Markdown body must not exceed 1,000,000 characters.", 2);
            Response.StatusCode = 400;
            return Partial("Shared/_EditForm", await Draft(
                id, title, body, status, priority, automaticExecutionAllowed, agentPolicy,
                executionProfile, tooLarge, cancellationToken));
        }

        try
        {
            var resolved = tracker.ResolveId(state.Config, id);
            var before = await tracker.GetAsync(state.Config, resolved, cancellationToken);
            var retryScheduled = string.Equals(
                before.DispatchState,
                DispatchStates.RetryScheduled,
                StringComparison.OrdinalIgnoreCase);
            if (retryScheduled &&
                !string.Equals(
                    before.AgentPolicy,
                    string.IsNullOrWhiteSpace(agentPolicy) ? null : agentPolicy,
                    StringComparison.OrdinalIgnoreCase))
            {
                var session = await tracker.GetAgentSessionAsync(
                    state.Config, resolved, cancellationToken);
                throw new TrackerException(
                    "AGENT_HANDOFF_REQUIRED",
                    $"The scheduled retry belongs to {session?.Agent ?? "the recorded agent"}. " +
                    "Changing the agent policy here requires an explicit cross-agent handoff " +
                    $"from the CLI: 'wrighty worker --item {id} --handoff --agent AGENT --yes'.",
                    2);
            }
            var handbackClaim = await LoadHandbackClaimAsync(
                resolved, action, cancellationToken);
            var cancelScheduledRetry =
                retryScheduled &&
                (!automaticExecutionAllowed ||
                 !string.Equals(
                     status,
                     state.Config.DefaultPickTo,
                     StringComparison.OrdinalIgnoreCase));
            var patch = new WorkItemPatch(
                OptionalValue<string>.From(title),
                OptionalValue<string>.From(body),
                OptionalValue<string>.From(status),
                OptionalValue<string?>.From(string.IsNullOrWhiteSpace(priority) ? null : priority),
                // Only an actually toggled checkbox is an operator decision. An unchanged value
                // stays unspecified so it cannot mask the worker queue, which yields to an
                // explicitly patched execution policy.
                AutomaticExecutionAllowed:
                    automaticExecutionAllowed == before.AutomaticExecutionAllowed
                        ? OptionalValue<bool>.Unspecified
                        : OptionalValue<bool>.From(automaticExecutionAllowed),
                AgentPolicy: OptionalValue<string?>.From(
                    string.IsNullOrWhiteSpace(agentPolicy) ? null : agentPolicy),
                ExecutionProfile: OptionalValue<string?>.From(
                    string.IsNullOrWhiteSpace(executionProfile) ? null : executionProfile),
                DispatchState: string.Equals(action, "save-handback", StringComparison.Ordinal) ||
                             cancelScheduledRetry
                    ? OptionalValue<string?>.From(null)
                    : OptionalValue<string?>.Unspecified);
            var updated = await tracker.UpdateAsync(
                state.Config, resolved, patch, expectedRevision, handle, cancellationToken);
            if (retryScheduled &&
                !string.Equals(
                    updated.Item.DispatchState,
                    DispatchStates.RetryScheduled,
                    StringComparison.OrdinalIgnoreCase))
            {
                await tracker.ClearPendingDispatchAsync(
                    state.Config, resolved, cancellationToken);
            }
            if (fromCard)
            {
                // Save and release, and nothing else. Resuming, finishing and handing back are the
                // card's own actions; a bounded gesture does not decide them on the operator's
                // behalf just because they were editing.
                await ReleaseCardGestureAsync(resolved, handle, cancellationToken);
                // A card gesture ends on the board, not in the viewer. Landing in the item panel
                // left the operator with a second thing to close, for a decision they had already
                // finished making.
                return ClosePanelAndRefresh();
            }

            var notice = await CompleteSaveActionAsync(
                resolved, action, handle, handbackClaim, cancellationToken);
            return Partial("Shared/_ItemDetail", await Item(id, notice, cancellationToken: cancellationToken));
        }
        catch (TrackerException exception) when (exception.Code == "UPDATE_CONFLICT")
        {
            Response.StatusCode = StatusCodes.Status409Conflict;
            var current = await Item(id, cancellationToken: cancellationToken);
            return Partial("Shared/_Conflict", new ConflictPageModel(
                current, title, body, status, priority, automaticExecutionAllowed, agentPolicy));
        }
        catch (TrackerException exception)
        {
            Response.StatusCode = Status(exception);
            try
            {
                return Partial("Shared/_EditForm", await Draft(
                    id, title, body, status, priority, automaticExecutionAllowed, agentPolicy,
                    executionProfile, exception, cancellationToken));
            }
            catch (TrackerException) { return KnownError(exception); }
        }
    }

    private async Task<ClaimHandle> PrepareSaveAsync(
        string id,
        string expectedClaimGeneration,
        CancellationToken cancellationToken)
    {
        await EnsureWebMutationAllowed(id, cancellationToken);
        var handle = RequiredWebHandle(id);
        var resolved = tracker.ResolveId(state.Config, id);
        if (!string.Equals(
                state.Generation(resolved.Value),
                expectedClaimGeneration,
                StringComparison.Ordinal))
            throw new TrackerException(
                "WEB_CLAIM_GENERATION_STALE",
                "This editor was opened under an older claim generation.",
                6);
        return handle;
    }

    /// <summary>
    /// End a card-entry edit: the board refreshes and the panel closes, because the gesture is
    /// over. Two events rather than a redirect, so the panel's own teardown runs.
    /// </summary>
    private IActionResult ClosePanelAndRefresh()
    {
        Response.Headers["HX-Trigger"] = "wrighty:refresh, wrighty:close-panel";
        return new NoContentResult();
    }

    private async Task<IActionResult> ReleaseCardDraftAsync(
        string id,
        ClaimHandle handle,
        CancellationToken cancellationToken)
    {
        try
        {
            var resolved = tracker.ResolveId(state.Config, id);
            await ReleaseCardGestureAsync(resolved, handle, cancellationToken);
            return ClosePanelAndRefresh();
        }
        catch (TrackerException exception) { return await ItemError(id, exception, cancellationToken); }
    }

    /// <summary>
    /// Give back the claim a card gesture took, keeping whatever decision is pending on the item.
    ///
    /// The ordinary web release clears the dispatch state. That is right when someone finishes with
    /// an item, and wrong here: clarifying a paused session and saving would drop it out of
    /// needs-attention, so the Resume the operator was about to press would not be on the card any
    /// more. Editing an item says nothing about what should happen to it next.
    /// </summary>
    private async Task ReleaseCardGestureAsync(
        WorkItemId id,
        ClaimHandle handle,
        CancellationToken cancellationToken)
    {
        await tracker.ReleaseAsync(
            state.Config, id, handle, false, DispatchStateOnRelease.Preserve, cancellationToken);
        state.Forget(id.Value);
    }

    private async Task<IActionResult> ReleaseDraftAsync(
        string id,
        ClaimHandle handle,
        CancellationToken cancellationToken)
    {
        try
        {
            var resolved = tracker.ResolveId(state.Config, id);
            var preservedRetry = await ReleaseFromWebAsync(
                resolved, handle, cancellationToken);
            state.Forget(resolved.Value);
            return Partial(
                "Shared/_ItemDetail",
                await Item(
                    id,
                    preservedRetry
                        ? "Draft discarded, claim released, and scheduled retry preserved."
                        : "Draft discarded and claim released.",
                    cancellationToken: cancellationToken));
        }
        catch (TrackerException exception)
        {
            return KnownError(exception);
        }
    }

    private async Task<WorkItemClaimSummary?> LoadHandbackClaimAsync(
        WorkItemId id,
        string action,
        CancellationToken cancellationToken)
    {
        if (action is not ("save-handback" or "save-queue"))
            return null;
        var claim = (await tracker.GetEditableAsync(
            state.Config, id, cancellationToken)).Claim;
        if (!HasResumeAddress(claim))
            throw new TrackerException(
                "RESUME_ADDRESS_UNAVAILABLE",
                "This claim does not have a complete agent session address to hand back.",
                5);
        return claim;
    }

    private async Task<string> CompleteSaveActionAsync(
        WorkItemId id,
        string action,
        ClaimHandle handle,
        WorkItemClaimSummary? handbackClaim,
        CancellationToken cancellationToken)
    {
        if (action == "save-release")
        {
            var preservedRetry = await ReleaseFromWebAsync(
                id, handle, cancellationToken);
            state.Forget(id.Value);
            return preservedRetry
                ? "Saved and released. The scheduled retry was preserved."
                : "Saved and released.";
        }
        if (action == "finish")
        {
            await tracker.FinishAsync(state.Config, id, null, handle, cancellationToken);
            await tracker.ClearPendingDispatchAsync(
                state.Config, id, cancellationToken);
            state.Forget(id.Value);
            return "Saved and finished.";
        }
        if (action == "save-queue")
        {
            await tracker.RequeueAsync(state.Config, id, handle, cancellationToken);
            await tracker.ClearPendingDispatchAsync(
                state.Config, id, cancellationToken);
            state.Forget(id.Value);
            return "Saved and queued. A continuous worker can now resume the recorded session.";
        }
        if (handbackClaim is null)
            return "Saved. The claim remains active.";
        await tracker.ClearPendingDispatchAsync(
            state.Config, id, cancellationToken);
        return await HandBackAsync(id, handle, handbackClaim, cancellationToken);
    }

    private async Task<bool> ReleaseFromWebAsync(
        WorkItemId id,
        ClaimHandle handle,
        CancellationToken cancellationToken)
    {
        var item = await tracker.GetAsync(state.Config, id, cancellationToken);
        var preserveRetry = string.Equals(
            item.DispatchState,
            DispatchStates.RetryScheduled,
            StringComparison.OrdinalIgnoreCase);
        if (preserveRetry)
        {
            await tracker.ReleaseAsync(
                state.Config, id, handle, false, DispatchStateOnRelease.Preserve,
                cancellationToken);
            return true;
        }

        await tracker.ReleaseAsync(
            state.Config, id, handle, false, DispatchStateOnRelease.Clear, cancellationToken);
        // Clears explicitly as well: on a backend whose release does not own the dispatch field,
        // the request above is a no-op and this is what actually removes it.
        await tracker.ClearPendingDispatchAsync(
            state.Config, id, cancellationToken);
        return false;
    }

    private async Task<string> HandBackAsync(
        WorkItemId id,
        ClaimHandle handle,
        WorkItemClaimSummary claim,
        CancellationToken cancellationToken)
    {
        var claimantContext = new AgentExecutionContext(
            claim.Agent,
            claim.SessionId,
            AgentContextSource.ExplicitOption,
            ClaimantKind: ClaimantKind.Agent,
            ClaimantId: $"agent:web-handback:{Guid.NewGuid():N}");
        var result = await tracker.TakeoverAsync(
            state.Config, id, claimantContext, handle.ClaimToken, cancellationToken);
        state.Retain(id.Value, result, claimantContext);
        return $"Saved. Use the command below to resume " +
               $"{RecordedAgentLabel(claim) ?? "the agent"} manually.";
    }

    public async Task<IActionResult> OnPostReleaseAsync(
        string id,
        CancellationToken cancellationToken)
    {
        try
        {
            await EnsureWebMutationAllowed(id, cancellationToken);
            var resolved = tracker.ResolveId(state.Config, id);
            var preservedRetry = await ReleaseFromWebAsync(
                resolved, RequiredWebHandle(id), cancellationToken);
            state.Forget(resolved.Value);
            return Partial(
                "Shared/_ItemDetail",
                await Item(
                    id,
                    preservedRetry
                        ? "Released. The scheduled retry was preserved."
                        : "Released.",
                    cancellationToken: cancellationToken));
        }
        catch (TrackerException exception)
        {
            return await ItemError(id, exception, cancellationToken);
        }
    }

    public Task<IActionResult> OnPostArchiveAsync(string id, CancellationToken cancellationToken) =>
        Mutate(id, async resolved => { await tracker.ArchiveAsync(state.Config, resolved, RequiredWebHandle(id), cancellationToken); state.Forget(resolved.Value); }, "Archived.", cancellationToken, protectNonHumanClaim: true);

    // Archives an unclaimed item in one step: archiving requires an owned claim, so acquire a human
    // web claim, then archive with it. The archive's session preservation is address-only, so this
    // human claim (which carries no workspace address) never clobbers the recorded agent session.
    public Task<IActionResult> OnPostClaimAndArchiveAsync(string id, CancellationToken cancellationToken) =>
        Mutate(id, async resolved =>
        {
            var claim = await tracker.ClaimAsync(state.Config, resolved, state.ClaimantContext, cancellationToken);
            state.Retain(resolved.Value, claim);
            var handle = new ClaimHandle(state.ClaimantContext, claim.ClaimToken);
            try
            {
                await tracker.ArchiveAsync(state.Config, resolved, handle, cancellationToken);
            }
            catch (TrackerException)
            {
                // Never strand the just-acquired claim if archiving fails.
                await ReleaseScaffoldingClaimAsync(resolved, handle, cancellationToken);
                state.Forget(resolved.Value);
                throw;
            }
            state.Forget(resolved.Value);
        }, "Archived.", cancellationToken);

    /// <summary>
    /// The card's archive action for a finished item. Same one-step claim-and-archive as
    /// <see cref="OnPostClaimAndArchiveAsync"/>, which the item panel keeps, but the board's
    /// contract: the card leaving the active board is the feedback. Destructive-adjacent, so the
    /// card carries the confirmation the panel has always shown.
    /// </summary>
    public async Task<IActionResult> OnPostArchiveItemAsync(
        string id,
        CancellationToken cancellationToken)
    {
        try
        {
            var resolved = tracker.ResolveId(state.Config, id);
            var claim = await tracker.ClaimAsync(
                state.Config, resolved, state.ClaimantContext, cancellationToken);
            state.Retain(resolved.Value, claim);
            var handle = new ClaimHandle(state.ClaimantContext, claim.ClaimToken);
            try
            {
                await tracker.ArchiveAsync(state.Config, resolved, handle, cancellationToken);
            }
            catch (TrackerException)
            {
                // Never strand the just-acquired claim if archiving fails.
                await ReleaseScaffoldingClaimAsync(resolved, handle, cancellationToken);
                state.Forget(resolved.Value);
                throw;
            }

            state.Forget(resolved.Value);
            Response.Headers["HX-Trigger"] = "wrighty:refresh";
            return new NoContentResult();
        }
        catch (TrackerException exception)
        {
            return await ItemError(id, exception, cancellationToken);
        }
    }

    public Task<IActionResult> OnPostUnarchiveAsync(string id, CancellationToken cancellationToken) =>
        Mutate(id, async resolved => await tracker.UnarchiveAsync(state.Config, resolved, cancellationToken), "Restored to the active board.", cancellationToken);

    public async Task<IActionResult> OnPostTakeoverAsync(
        string id,
        bool fromCard,
        CancellationToken cancellationToken)
    {
        try
        {
            var resolved = tracker.ResolveId(state.Config, id);
            var result = await tracker.TakeoverAsync(state.Config, resolved, state.ClaimantContext, null, cancellationToken);
            state.Retain(resolved.Value, result);
            var notice = fromCard
                ? "Ready for clarification. Edit the requirements, then save or cancel. The " +
                  "saved agent session will remain available afterward."
                : "Takeover complete. The previous claimant is fenced from later Wrighty " +
                  "mutations. Save keeps human ownership. Use Save and resume automatically to " +
                  "queue the recorded session, or use the manual resume action under More actions " +
                  "to continue it yourself. An operation already holding the store lock may have " +
                  "finished first.";
            return Partial("Shared/_EditForm", await Item(id,
                notice,
                editing: true,
                cardEntry: fromCard,
                cancellationToken: cancellationToken));
        }
        catch (TrackerException exception) { return await ItemError(id, exception, cancellationToken); }
    }

    public async Task<IActionResult> OnPostOverrideReleaseAsync(string id, CancellationToken cancellationToken)
    {
        try
        {
            var resolved = tracker.ResolveId(state.Config, id);
            // Releasing a claim says who stops holding the item, not what should happen to it.
            await tracker.ReleaseAsync(state.Config, resolved,
                new ClaimHandle(state.ClaimantContext, null), true,
                DispatchStateOnRelease.Preserve, cancellationToken);
            state.Forget(resolved.Value);
            return Partial("Shared/_ItemDetail", await Item(id, "Existing claim released.", cancellationToken: cancellationToken));
        }
        catch (TrackerException exception) { return await ItemError(id, exception, cancellationToken); }
    }

    /// <summary>
    /// The board's one-click queue action: claim, move to the pick-from status, release. The
    /// status move runs through the tracker service, so with the worker queue enabled the move
    /// authorizes execution and, on GitHub, context approval — the button is the whole "give this
    /// to the worker" ceremony.
    /// </summary>
    public async Task<IActionResult> OnPostQueueItemAsync(
        string id,
        CancellationToken cancellationToken)
    {
        try
        {
            var resolved = tracker.ResolveId(state.Config, id);
            var claim = await tracker.ClaimAsync(
                state.Config, resolved, state.ClaimantContext, cancellationToken);
            var handle = new ClaimHandle(
                state.ClaimantContext with { ClaimantId = claim.ClaimantId },
                claim.ClaimToken);
            try
            {
                await tracker.UpdateAsync(
                    state.Config,
                    resolved,
                    WorkItemPatch.StatusOnly(state.Config.DefaultPickFrom),
                    expectedRevision: null,
                    handle,
                    cancellationToken);
            }
            catch (TrackerException)
            {
                // The claim was only scaffolding for this one move; do not leave the item claimed
                // behind a failed update. A failing release must not mask the update error.
                await ReleaseScaffoldingClaimAsync(resolved, handle, cancellationToken);
                throw;
            }

            await tracker.ReleaseAsync(
                state.Config, resolved, handle, false, DispatchStateOnRelease.Preserve,
                cancellationToken);
            // A fire-and-forget action stays on the board: no content is swapped in, the refresh
            // moves the card into the queue column, and that move is the feedback. Failures still
            // open the item panel below, because that is when the operator needs the detail.
            Response.Headers["HX-Trigger"] = "wrighty:refresh";
            return new NoContentResult();
        }
        catch (TrackerException exception)
        {
            return await ItemError(id, exception, cancellationToken);
        }
    }

    // Both sides are nullable: a work item's status may be absent, and so may the status it is
    // being compared against. string.Equals defines that comparison, so the callers do not each
    // need a guard.
    private static bool IsWorkflowStatus(string? status, string? workflowStatus) =>
        string.Equals(status, workflowStatus, StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Finished work is outside Wrighty's execution lifecycle once no claimant remains. Opening
    /// its retained vendor session is therefore an unmanaged local action: it neither reacquires
    /// a Wrighty claim nor passes claimant credentials to the client. An active claim deliberately
    /// keeps this false even if an external tracker moved the item to Done; Wrighty must not infer
    /// that a still-running claimant has stopped.
    /// </summary>
    private bool IsUnmanagedTerminal(
        WorkItemDetail item,
        WorkItemClaimSummary claim) =>
        IsUnmanagedTerminal(item.Archived, item.Status, claim);

    private bool IsUnmanagedTerminal(
        WorkItemSummary item,
        WorkItemClaimSummary claim) =>
        IsUnmanagedTerminal(item.Archived, item.Status, claim);

    private bool IsUnmanagedTerminal(
        bool archived,
        string? status,
        WorkItemClaimSummary claim) =>
        claim.State == ClaimOwnershipState.Unclaimed &&
        (archived || IsWorkflowStatus(status, state.Config.DefaultFinishTo));

    /// <summary>
    /// The actions a card offers, in offer order, resolved from the item's own state. One place
    /// decides what is possible, so the board template renders and decides nothing, and the panel
    /// and the board can never disagree about eligibility.
    ///
    /// Most states offer one action, because a board's value is scanning and a toolbar per card
    /// destroys that. Needs-attention is the exception: there the next step genuinely branches —
    /// answer the question, or hand it back — so it carries a small set. The list shape is what
    /// plan 036's catalogue will fill later.
    /// </summary>
    private IReadOnlyList<CardActionView> CardActions(
        DashboardWorkItem value,
        string activity,
        IReadOnlyList<string> statuses)
    {
        if (value.Item.Archived)
            return [];

        // Queueable: an untouched backlog item — unclaimed, no recovery state, and not already in
        // the queue, in progress, or finished. For the default statuses that is exactly the first
        // (backlog) column.
        if (value.Claim.State == ClaimOwnershipState.Unclaimed &&
            value.Item.DispatchState is null &&
            !IsWorkflowStatus(value.Item.Status, state.Config.DefaultPickFrom) &&
            !IsWorkflowStatus(value.Item.Status, state.Config.DefaultPickTo) &&
            !IsWorkflowStatus(value.Item.Status, state.Config.DefaultFinishTo))
        {
            // Two next moves, so two actions — the same shape as a needs-attention card. Editing
            // an item you can see took opening the panel and then claiming; the panel's claim
            // already lands on the edit form, so the ceremony was purely getting there.
            //
            // Claiming for editing is not queueing. The worker-queue rule keys off status moves,
            // so taking a claim here leaves automatic execution exactly as it was.
            return
            [
                new CardActionView(
                    "queue",
                    "QueueItem",
                    "Queue",
                    "Move to the worker queue so an agent can pick it up",
                    "for agent"),
                new CardActionView(
                    "edit",
                    "Claim",
                    "Edit",
                    "Claim the item, edit it, and release on save or cancel",
                    "for editing",
                    IsPrimary: false,
                    OpensPanel: true,
                    BoundedGesture: true)
            ];
        }

        // In the queue and untouched: the symmetric revocation. Once a worker has claimed it or
        // recorded recovery state, taking it back is no longer a one-gesture decision.
        if (IsWorkflowStatus(value.Item.Status, state.Config.DefaultPickFrom) &&
            value.Claim.State == ClaimOwnershipState.Unclaimed &&
            value.Item.DispatchState is null)
        {
            var backlog = BacklogStatus(statuses);
            return
            [
                new CardActionView(
                    "dequeue",
                    "DequeueItem",
                    "Send back",
                    backlog is null
                        ? "Move out of the worker queue; the queue rule revokes automatic execution"
                        : $"Move back to {backlog}; the worker queue rule revokes automatic execution",
                    "out of the worker queue",
                    UnavailableReason: backlog is null
                        ? "No backlog status is configured to send this item back to."
                        : null)
            ];
        }

        // A retained session waiting on a person: clarify what it asked, or hand it back to a
        // worker. The interactive ways back in (terminal, Desktop app) are still panel-only —
        // see the plan's launch-action slice.
        if (activity == OperationalStatuses.NeedsAttention &&
            value.Session is { IsComplete: true, FromCurrentInstallation: true } session)
        {
            return NeedsAttentionCardActions(value, session);
        }

        // Finished, unclaimed work has left Wrighty's execution lifecycle. The retained session
        // remains available as an unmanaged operator action alongside filing the item away; the
        // latter is destructive-adjacent, so it keeps the panel's confirmation.
        if (IsWorkflowStatus(value.Item.Status, state.Config.DefaultFinishTo) &&
            value.Claim.State == ClaimOwnershipState.Unclaimed)
        {
            return FinishedCardActions(value);
        }

        return DispatchMarkerActions(value, activity);
    }

    private IReadOnlyList<CardActionView> NeedsAttentionCardActions(
        DashboardWorkItem value,
        AgentSessionRecord session)
    {
        // Clarify and Resume acquire a free claim, so they are only offered when there is one
        // to take. A retained non-human claim is deliberately protected: Clarify must route
        // through the existing confirmed takeover instead of advertising an ordinary claim that
        // the server will reject. Human dashboard residue keeps its ceremony-free hand-off path.
        // Launch can also reclaim this installation's own ended session, so it remains available
        // when that ended run's claim is still attached.
        List<CardActionView> actions = [];
        if (value.Claim.State != ClaimOwnershipState.HeldByOther)
        {
            var requiresConfirmedTakeover =
                value.Claim.State == ClaimOwnershipState.OwnedByCurrent &&
                state.Config.EffectiveWeb.ProtectNonHumanClaims &&
                ClaimantKinds.FromStorageValue(value.Claim.ClaimantKind) != ClaimantKind.Human;
            actions.Add(new CardActionView(
                "clarify",
                requiresConfirmedTakeover ? "Takeover" : "Claim",
                "Clarify",
                requiresConfirmedTakeover
                    ? "Open the paused item, edit its requirements, and release it on save or cancel"
                    : "Claim the item, edit it, and release on save or cancel",
                "for editing",
                IsPrimary: true,
                OpensPanel: true,
                BoundedGesture: true,
                ConfirmTitle: requiresConfirmedTakeover
                    ? "Clarify this paused item?"
                    : null,
                ConfirmMessage: requiresConfirmedTakeover
                    ? "The agent has stopped and is waiting for input. Wrighty will give you " +
                      "control of the item so you can edit its requirements. The saved agent " +
                      "session will remain available to resume afterward."
                    : null,
                ConfirmAction: requiresConfirmedTakeover ? "Open for clarification" : null));
            actions.Add(new CardActionView(
                "resume",
                "ResumeSession",
                "Resume",
                "Queue the recorded session so a continuous worker resumes it",
                "recorded session",
                IsPrimary: false));
        }

        actions.AddRange(LaunchCardActions(value, session));
        return actions;
    }

    private IReadOnlyList<CardActionView> FinishedCardActions(DashboardWorkItem value)
    {
        List<CardActionView> actions = [];
        if (value.Session is { IsComplete: true, FromCurrentInstallation: true } session)
            actions.AddRange(LaunchCardActions(value, session));
        actions.Add(new CardActionView(
            "archive",
            "ArchiveItem",
            "Archive",
            "Archive this finished item and remove it from the active board",
            "finished item",
            ConfirmTitle: "Archive this item?",
            ConfirmMessage: "Archiving removes the item from the active board. Its recorded " +
                "agent session and workspace are preserved, and it can be restored from the " +
                "archived view.",
            ConfirmAction: "Archive"));
        return actions;
    }

    /// <summary>
    /// The two ways an unclaimed retained session moves its dispatch marker, which are the same
    /// move approached from opposite sides.
    ///
    /// From <c>queued</c>, *Cancel resume* takes the marker back to needs-attention so no worker
    /// picks the item up — without it the only ways out were running the worker or editing the
    /// store by hand. From a paused session with *no* marker, *Needs attention* puts one back:
    /// that state is resumable but was unreachable from every surface, because queueing a recorded
    /// session requires the very marker that is missing.
    ///
    /// Extracted from the action resolver so the shared precondition is stated once, rather than
    /// as two nearly identical guards a reader has to diff by eye.
    /// </summary>
    private static IReadOnlyList<CardActionView> DispatchMarkerActions(
        DashboardWorkItem value,
        string activity)
    {
        if (value.Claim.State != ClaimOwnershipState.Unclaimed ||
            value.Session is not { IsComplete: true, FromCurrentInstallation: true })
        {
            return [];
        }

        return activity switch
        {
            OperationalStatuses.Queued =>
            [
                new CardActionView(
                    "hold",
                    "HoldSession",
                    "Cancel resume",
                    "Put the recorded session back to needs attention so no worker picks it up",
                    "queued resume")
            ],
            OperationalStatuses.PausedSession =>
            [
                new CardActionView(
                    "reopen",
                    "HoldSession",
                    "Needs attention",
                    "Mark the retained session as waiting for a person, restoring its actions",
                    "retained session")
            ],
            _ => []
        };
    }

    /// <summary>
    /// The interactive ways back into a paused session: continue it in a terminal, or open it in
    /// the vendor's Desktop app. These were panel-only until now, buried behind a collapsed
    /// section and — worse — each demanding the operator already hold a claim of the opposite kind,
    /// so the same item could never offer both and often offered neither.
    ///
    /// They are offered here as ordinary card actions because
    /// <see cref="AcquireForLaunchAsync"/> now acquires what each mode needs. Ownership itself is
    /// not re-checked in this list beyond excluding another installation's claim: the handler is
    /// the authority, and duplicating its rules here is how a board and a panel start disagreeing.
    /// What is checked is what the board can know cheaply and the operator cannot act on — an
    /// unknown vendor, an uninstalled CLI, a platform that cannot launch, a Desktop route this
    /// vendor has not enabled.
    /// </summary>
    private IReadOnlyList<CardActionView> LaunchCardActions(
        DashboardWorkItem value,
        AgentSessionRecord session)
    {
        if (value.Claim.State == ClaimOwnershipState.HeldByOther ||
            session.Agent is not { } agent ||
            session.SessionId is not { } sessionId ||
            !adaptersByName.TryGetValue(agent, out var adapter))
        {
            return [];
        }

        var capabilities = localAgentSessionLauncher.GetCapabilities(agent);
        var generation = SessionGeneration(
            value.Item.Id,
            value.Item.Status,
            value.Item.Archived,
            session,
            value.Claim);
        var vendor = AgentDisplayName(agent);
        var unmanagedTerminal = IsUnmanagedTerminal(value.Item, value.Claim);
        var desktop = adapter.BuildDesktopLaunch(new SessionHandle(sessionId))
            .EnableExperimental(
                state.Config.EffectiveWorker.AllowsExperimentalDesktopSession(agent));
        var hasCli = capabilities.CanLaunchCli &&
            agentRuntimeCatalog.Snapshot().Find(agent) is { Installed: true };
        var hasDesktop = capabilities.CanLaunchDesktop && desktop.CanLaunch;

        if (hasCli && hasDesktop)
        {
            // One button, then a choice. Two buttons for one intent crowded the card, and the
            // choice between them is not cosmetic — it decides who holds the item afterwards — so
            // the modes carry their consequences rather than just their names.
            return [LaunchChoiceAction(
                agent, vendor, sessionId, generation, desktop, unmanagedTerminal)];
        }

        if (hasCli)
        {
            // Only one way in, so name it and go: a chooser with a single option is a wasted
            // click, and the unqualified label would say less than this one does.
            return [CliLaunchAction(vendor, sessionId, generation, unmanagedTerminal)];
        }

        if (hasDesktop)
        {
            // Without the chooser to state it, the Desktop warning and vendor prerequisite go back
            // to being a confirmation — they must reach the operator either way.
            return [DesktopLaunchAction(
                agent, vendor, sessionId, generation, desktop, unmanagedTerminal)];
        }

        return [];
    }

    private static CardActionView LaunchChoiceAction(
        string agent,
        string vendor,
        string sessionId,
        string generation,
        DesktopLaunchAddress desktop,
        bool unmanagedTerminal) => new(
        "open-session",
        string.Empty,
        $"Open {vendor}",
        $"Continue the recorded {vendor} session in a terminal or the Desktop app",
        "recorded session",
        ExpectedSessionId: sessionId,
        ExpectedSessionGeneration: generation,
        Options:
        [
            new CardActionOption(
                "open-cli",
                "OpenSessionCli",
                "In a terminal",
                unmanagedTerminal
                    ? UnmanagedSessionConsequence(vendor, "CLI")
                    : $"Continues the session as the agent. Wrighty passes the claim into the " +
                      $"{vendor} CLI, so the session can finish or hand the item back itself.",
                "in a terminal"),
            new CardActionOption(
                "open-desktop",
                "OpenSessionDesktop",
                "In the Desktop app",
                unmanagedTerminal
                    ? UnmanagedDesktopConsequence(vendor, desktop)
                    : DesktopCardConfirmation(agent, desktop),
                "in the Desktop app")
        ]);

    private static CardActionView CliLaunchAction(
        string vendor,
        string sessionId,
        string generation,
        bool unmanagedTerminal) => new(
        "open-cli",
        "OpenSessionCli",
        $"Open {vendor} CLI",
        $"Continue the recorded session in a new {vendor} terminal",
        "in a terminal",
        ConfirmTitle: unmanagedTerminal ? $"Open this Done session in {vendor} CLI?" : null,
        ConfirmMessage: unmanagedTerminal ? UnmanagedSessionConsequence(vendor, "CLI") : null,
        ConfirmAction: unmanagedTerminal ? "Open CLI" : null,
        ExpectedSessionId: sessionId,
        ExpectedSessionGeneration: generation);

    private static CardActionView DesktopLaunchAction(
        string agent,
        string vendor,
        string sessionId,
        string generation,
        DesktopLaunchAddress desktop,
        bool unmanagedTerminal) => new(
        "open-desktop",
        "OpenSessionDesktop",
        $"Open {vendor} Desktop",
        $"Open the recorded session in {vendor} Desktop",
        "in the Desktop app",
        ConfirmTitle: unmanagedTerminal
            ? $"Open this Done session in {vendor} Desktop?"
            : $"Open this session in {vendor} Desktop?",
        ConfirmMessage: unmanagedTerminal
            ? UnmanagedDesktopConsequence(vendor, desktop)
            : DesktopCardConfirmation(agent, desktop),
        ConfirmAction: "Open Desktop",
        ExpectedSessionId: sessionId,
        ExpectedSessionGeneration: generation);

    private static string UnmanagedSessionConsequence(string vendor, string client) =>
        $"Opens the recorded session in {vendor} {client} without a Wrighty claim or claimant " +
        "credentials. The item stays outside Wrighty's management; you are responsible " +
        "for any further conversation or workspace changes.";

    private static string UnmanagedDesktopConsequence(
        string vendor,
        DesktopLaunchAddress desktop)
    {
        var message = UnmanagedSessionConsequence(vendor, "Desktop");
        if (desktop.Prerequisite is { } prerequisite)
            message += $" {prerequisite}";
        if (desktop.CompatibilityWarning is { } warning)
            message += $" {warning}";
        return message;
    }

    private static string DesktopCardConfirmation(string agent, DesktopLaunchAddress desktop)
    {
        // Desktop cannot be handed the claim the way a terminal can: the vendor's only integration
        // point is a deep link, which must not carry a scoped credential. So the operator holds
        // the claim, and the wording says who owns the item and what they owe afterwards.
        var message =
            $"You supervise this one. Wrighty takes a human claim and keeps it while you work in " +
            $"{AgentDisplayName(agent)} Desktop. Stop or idle Desktop before handing back to a " +
            "worker; Wrighty cannot detect a vendor client that is still running.";
        if (desktop.Prerequisite is { } prerequisite)
            message += $" {prerequisite}";
        if (desktop.CompatibilityWarning is { } warning)
            message += $" {warning}";
        return message;
    }

    /// <summary>
    /// The statuses a card may be dragged to. Drag is the general form of the button gestures,
    /// so it obeys the same state rules rather than becoming a way around them.
    ///
    /// A card is not draggable at all when the item is not the operator's to move in one gesture:
    ///
    /// - **claimed** — it belongs to its claimant, and the move would be refused anyway;
    /// - **a worker decision pending** (queued, retry-scheduled, handoff-queued) — a worker is
    ///   about to act on it, so a move races that decision. Cancel it first; the card offers it.
    ///
    /// An item **holding a recorded session** — paused, waiting for a person — may only be
    /// finished. Dragging it to a backlog or queue status would strand it: queueing a paused
    /// session requires the in-progress status, so it would look available for work while its
    /// resume path was broken. Finishing is not stranding, it is the work ending: an operator who
    /// judges the agent did enough is entitled to say so with one gesture, and the finish status
    /// carries the archive policy and clears the dispatch state as it goes.
    ///
    /// For everything else, every column is a legal target except the in-progress status: that is
    /// where the *worker* moves an item when it claims one. A manual move there puts the item
    /// where the worker does not look — it picks from the queue — while the board claims work is
    /// happening with no claim and no session behind it.
    /// </summary>
    private IReadOnlyList<string> DropTargets(
        DashboardWorkItem value,
        IReadOnlyList<string> statuses)
    {
        if (value.Item.Archived || value.Claim.State != ClaimOwnershipState.Unclaimed)
            return [];
        if (value.Item.DispatchState is { } dispatch &&
            !string.Equals(dispatch, DispatchStates.NeedsAttention, StringComparison.OrdinalIgnoreCase))
            return [];

        var finishTo = statuses.FirstOrDefault(
            status => IsWorkflowStatus(status, state.Config.DefaultFinishTo));
        if (value.Session is { HasAddress: true } ||
            IsWorkflowStatus(value.Item.DispatchState, DispatchStates.NeedsAttention))
        {
            return finishTo is null || IsWorkflowStatus(value.Item.Status, finishTo)
                ? []
                : [finishTo];
        }

        return
        [
            .. statuses.Where(status =>
                !IsWorkflowStatus(status, state.Config.DefaultPickTo) &&
                !IsWorkflowStatus(status, value.Item.Status))
        ];
    }

    /// <summary>
    /// Where a de-queued item goes: the first configured status that is not the queue, the
    /// in-progress, or the finished column. Provenance is deliberately not remembered yet — see
    /// the plan's open question — so this is "the backlog", not "where it came from".
    /// </summary>
    private string? BacklogStatus(IReadOnlyList<string> statuses) =>
        statuses.FirstOrDefault(status =>
            !IsWorkflowStatus(status, state.Config.DefaultPickFrom) &&
            !IsWorkflowStatus(status, state.Config.DefaultPickTo) &&
            !IsWorkflowStatus(status, state.Config.DefaultFinishTo));

    /// <summary>
    /// A card dropped on another column: the general gesture the buttons are specialisations of.
    /// Same claim → move → release bundle, so the worker-queue rule authorizes on the way in and
    /// revokes on the way out exactly as it does for the buttons — dropping into the queue column
    /// is visibly identical to the GitHub board drag it mirrors.
    ///
    /// The target status is validated against the configured statuses rather than trusted: the
    /// browser supplies it, and an unconfigured column would otherwise strand the item outside
    /// the board.
    /// </summary>
    public async Task<IActionResult> OnPostMoveItemAsync(
        string id,
        string status,
        CancellationToken cancellationToken)
    {
        try
        {
            var resolved = tracker.ResolveId(state.Config, id);
            var snapshot = await tracker.GetDashboardAsync(
                state.Config, ArchiveScope.Active, cancellationToken);
            var target = snapshot.Statuses.FirstOrDefault(
                value => string.Equals(value, status, StringComparison.OrdinalIgnoreCase))
                ?? throw new TrackerException(
                    "STATUS_UNKNOWN",
                    $"'{status}' is not a configured status for this repository.",
                    2);
            // The same rule the card advertises, enforced here: the browser is not the authority
            // on which moves are legal, and a drag is the general form of the button gestures
            // rather than a way around the eligibility they enforce.
            var card = snapshot.Items.FirstOrDefault(item => item.Item.Id == resolved)
                ?? throw new TrackerException(
                    "WORK_ITEM_NOT_FOUND",
                    $"Work item '{id}' is not on the active board.",
                    5);
            if (!DropTargets(card, snapshot.Statuses).Contains(target, StringComparer.OrdinalIgnoreCase))
                throw new TrackerException(
                    "STATUS_MOVE_NOT_ALLOWED",
                    IsWorkflowStatus(target, state.Config.DefaultPickTo)
                        ? $"'{target}' is where the worker moves an item when it claims one; " +
                          "queue the item instead so a worker picks it up."
                        : $"This item cannot be moved to '{target}' by dragging: it is claimed, " +
                          "has a worker decision pending, or holds a recorded agent session that " +
                          $"only '{state.Config.DefaultFinishTo}' may end. Use the item's own " +
                          "actions instead.",
                    6);
            var claim = await tracker.ClaimAsync(
                state.Config, resolved, state.ClaimantContext, cancellationToken);
            var handle = new ClaimHandle(
                state.ClaimantContext with { ClaimantId = claim.ClaimantId },
                claim.ClaimToken);
            try
            {
                await tracker.UpdateAsync(
                    state.Config,
                    resolved,
                    WorkItemPatch.StatusOnly(target),
                    expectedRevision: null,
                    handle,
                    cancellationToken);
            }
            catch (TrackerException)
            {
                await ReleaseScaffoldingClaimAsync(resolved, handle, cancellationToken);
                throw;
            }

            await tracker.ReleaseAsync(
                state.Config, resolved, handle, false, DispatchStateOnRelease.Preserve,
                cancellationToken);
            Response.Headers["HX-Trigger"] = "wrighty:refresh";
            return new NoContentResult();
        }
        catch (TrackerException exception)
        {
            // A refused drop opens the panel with the reason; the board refresh puts the card
            // back where it came from, so the snap-back needs no client bookkeeping.
            return await ItemError(id, exception, cancellationToken);
        }
    }

    /// <summary>
    /// The symmetric revocation of the queue button: move an untouched queued item back to the
    /// backlog. The worker-queue rule clears automatic execution as part of the move, so leaving
    /// the queue is one gesture for the same reason entering it is.
    /// </summary>
    public async Task<IActionResult> OnPostDequeueItemAsync(
        string id,
        CancellationToken cancellationToken)
    {
        try
        {
            var resolved = tracker.ResolveId(state.Config, id);
            var snapshot = await tracker.GetDashboardAsync(
                state.Config, ArchiveScope.Active, cancellationToken);
            var backlog = BacklogStatus(snapshot.Statuses)
                ?? throw new TrackerException(
                    "STATUS_UNAVAILABLE",
                    "No backlog status is configured to send this item back to.",
                    2);
            var claim = await tracker.ClaimAsync(
                state.Config, resolved, state.ClaimantContext, cancellationToken);
            var handle = new ClaimHandle(
                state.ClaimantContext with { ClaimantId = claim.ClaimantId },
                claim.ClaimToken);
            try
            {
                await tracker.UpdateAsync(
                    state.Config,
                    resolved,
                    WorkItemPatch.StatusOnly(backlog),
                    expectedRevision: null,
                    handle,
                    cancellationToken);
            }
            catch (TrackerException)
            {
                // Same discipline as the queue button: the claim was scaffolding for one move, so
                // a failed move must not leave the item claimed, and a failing release must not
                // mask the move's error.
                await ReleaseScaffoldingClaimAsync(resolved, handle, cancellationToken);
                throw;
            }

            await tracker.ReleaseAsync(
                state.Config, resolved, handle, false, DispatchStateOnRelease.Preserve,
                cancellationToken);
            Response.Headers["HX-Trigger"] = "wrighty:refresh";
            return new NoContentResult();
        }
        catch (TrackerException exception)
        {
            return await ItemError(id, exception, cancellationToken);
        }
    }

    /// <summary>
    /// The card's resume action. Same operation as <see cref="OnPostQueueForWorkerAsync"/>, which
    /// the item panel keeps, but the board's contract: no content, just the refresh trigger, so
    /// the card moving is the feedback. A card action must not open the panel on success — that
    /// is what the panel's own button is for, and mixing the two makes one board gesture behave
    /// unlike its neighbours.
    /// </summary>
    public async Task<IActionResult> OnPostResumeSessionAsync(
        string id,
        CancellationToken cancellationToken)
    {
        try
        {
            var resolved = tracker.ResolveId(state.Config, id);
            await tracker.QueuePausedAsync(state.Config, resolved, cancellationToken);
            state.Forget(resolved.Value);
            Response.Headers["HX-Trigger"] = "wrighty:refresh";
            return new NoContentResult();
        }
        catch (TrackerException exception)
        {
            return await ItemError(id, exception, cancellationToken);
        }
    }

    /// <summary>
    /// Moves a retained session's dispatch state to needs-attention. The item stays where it is
    /// and keeps its recorded session; only the marker changes.
    ///
    /// Two card actions reach this, from opposite sides. *Cancel resume* uses it to undo a queued
    /// resume, the exact inverse of <see cref="OnPostResumeSessionAsync"/>. *Needs attention* uses
    /// it to restore the marker on a paused session that has none — a state that was otherwise
    /// unreachable, because queueing a recorded session requires the very marker it is missing.
    /// One transition, because it is the same transition; what differs is where the item came from.
    /// </summary>
    public async Task<IActionResult> OnPostHoldSessionAsync(
        string id,
        CancellationToken cancellationToken)
    {
        try
        {
            var resolved = tracker.ResolveId(state.Config, id);
            var claim = await tracker.ClaimAsync(
                state.Config, resolved, state.ClaimantContext, cancellationToken);
            var handle = new ClaimHandle(
                state.ClaimantContext with { ClaimantId = claim.ClaimantId },
                claim.ClaimToken);
            try
            {
                await tracker.UpdateAsync(
                    state.Config,
                    resolved,
                    new WorkItemPatch(
                        OptionalValue<string>.Unspecified,
                        OptionalValue<string>.Unspecified,
                        OptionalValue<string>.Unspecified,
                        OptionalValue<string?>.Unspecified,
                        DispatchState: OptionalValue<string?>.From(
                            DispatchStates.NeedsAttention)),
                    expectedRevision: null,
                    handle,
                    cancellationToken);
            }
            catch (TrackerException)
            {
                await ReleaseScaffoldingClaimAsync(resolved, handle, cancellationToken);
                throw;
            }

            // Preserving, not ordinary, release: an ordinary release clears the dispatch state,
            // which would undo the very field this action just set and leave the item merely
            // paused instead of waiting for attention.
            await tracker.ReleaseAsync(
                state.Config, resolved, handle, false, DispatchStateOnRelease.Preserve,
                cancellationToken);
            state.Forget(resolved.Value);
            Response.Headers["HX-Trigger"] = "wrighty:refresh";
            return new NoContentResult();
        }
        catch (TrackerException exception)
        {
            return await ItemError(id, exception, cancellationToken);
        }
    }

    public async Task<IActionResult> OnPostQueueForWorkerAsync(
        string id,
        CancellationToken cancellationToken)
    {
        try
        {
            var resolved = tracker.ResolveId(state.Config, id);
            await tracker.QueuePausedAsync(state.Config, resolved, cancellationToken);
            state.Forget(resolved.Value);
            Response.Headers["HX-Trigger"] = "wrighty:refresh";
            return Partial(
                "Shared/_ItemDetail",
                await Item(
                    id,
                    "Queued. A continuous worker can now resume the recorded session.",
                    cancellationToken: cancellationToken));
        }
        catch (TrackerException exception)
        {
            return await ItemError(id, exception, cancellationToken);
        }
    }

    public async Task<IActionResult> OnPostLaunchAgentCliAsync(
        string id,
        string expectedSessionId,
        string expectedSessionGeneration,
        CancellationToken cancellationToken)
    {
        try
        {
            var notice = await LaunchCliAsync(
                id, expectedSessionId, expectedSessionGeneration, cancellationToken);
            return Partial(
                "Shared/_ItemDetail",
                await Item(id, notice, cancellationToken: cancellationToken));
        }
        catch (TrackerException exception)
        {
            return await ItemError(id, exception, cancellationToken);
        }
    }

    /// <summary>
    /// The card's Open CLI action. Same operation as <see cref="OnPostLaunchAgentCliAsync"/>, in
    /// the board's contract: no content, just the refresh trigger. Reusing the panel's handler
    /// verbatim was the mistake the first attempt made — one card gesture opened the viewer while
    /// its neighbours did not.
    /// </summary>
    public async Task<IActionResult> OnPostOpenSessionCliAsync(
        string id,
        string expectedSessionId,
        string expectedSessionGeneration,
        CancellationToken cancellationToken)
    {
        try
        {
            await LaunchCliAsync(
                id, expectedSessionId, expectedSessionGeneration, cancellationToken);
            Response.Headers["HX-Trigger"] = "wrighty:refresh";
            return new NoContentResult();
        }
        catch (TrackerException exception)
        {
            return await ItemError(id, exception, cancellationToken);
        }
    }

    private async Task<string> LaunchCliAsync(
        string id,
        string expectedSessionId,
        string expectedSessionGeneration,
        CancellationToken cancellationToken)
    {
        var launch = await ResolveSessionLaunchAsync(
            id, expectedSessionId, expectedSessionGeneration, cancellationToken);
        var capabilities = localAgentSessionLauncher.GetCapabilities(launch.Adapter.Agent);
        if (!capabilities.CanLaunchCli)
            throw new TrackerException(
                "TERMINAL_LAUNCH_UNSUPPORTED",
                capabilities.CliUnavailableReason ??
                "Opening a new agent terminal is unavailable on this platform.",
                3);
        if (agentRuntimeCatalog.Snapshot().Find(launch.Adapter.Agent) is not { Installed: true })
            throw new TrackerException(
                "TERMINAL_LAUNCH_UNSUPPORTED",
                $"{AgentDisplayName(launch.Adapter.Agent)} CLI is not installed.",
                3);

        if (IsUnmanagedTerminal(launch.Item, launch.Claim))
        {
            var unmanagedInvocation = launch.Adapter.BuildInteractiveInvocation(
                new SessionHandle(launch.Session.SessionId!),
                new Workspace(launch.Session.WorkspacePath!),
                TrackerEnvironment());
            EnsureLaunched(
                await localAgentSessionLauncher.LaunchCliAsync(
                    unmanagedInvocation, cancellationToken),
                "TERMINAL_LAUNCH_UNSUPPORTED");
            return $"Opened the recorded {AgentDisplayName(launch.Adapter.Agent)} CLI session. " +
                   "The item remains outside Wrighty's management.";
        }

        // Capabilities are checked before acquiring: an unsupported platform must not leave the
        // item claimed for a terminal that was never going to open.
        var handle = await AcquireForLaunchAsync(
            launch, ClaimantKind.Agent, cancellationToken);
        var environment = TrackerEnvironment();
        environment["WRIGHTY_CLAIMANT_ID"] = handle.ClaimantId;
        environment["WRIGHTY_CLAIM_TOKEN"] = handle.ClaimToken!;
        var invocation = launch.Adapter.BuildInteractiveInvocation(
            new SessionHandle(launch.Session.SessionId!),
            new Workspace(launch.Session.WorkspacePath!),
            environment);
        await LaunchOrReleaseAsync(
            launch,
            handle,
            async token => EnsureLaunched(
                await localAgentSessionLauncher.LaunchCliAsync(invocation, token),
                "TERMINAL_LAUNCH_UNSUPPORTED"),
            cancellationToken);
        return $"Opened {AgentDisplayName(launch.Adapter.Agent)} CLI in a new terminal.";
    }

    public async Task<IActionResult> OnPostLaunchAgentDesktopAsync(
        string id,
        string expectedSessionId,
        string expectedSessionGeneration,
        CancellationToken cancellationToken)
    {
        try
        {
            var notice = await LaunchDesktopAsync(
                id, expectedSessionId, expectedSessionGeneration, cancellationToken);
            return Partial(
                "Shared/_ItemDetail",
                await Item(id, notice, cancellationToken: cancellationToken));
        }
        catch (TrackerException exception)
        {
            return await ItemError(id, exception, cancellationToken);
        }
    }

    /// <summary>
    /// The card's Open Desktop action; see <see cref="OnPostOpenSessionCliAsync"/> for why the
    /// board gets its own handler rather than reusing the panel's.
    /// </summary>
    public async Task<IActionResult> OnPostOpenSessionDesktopAsync(
        string id,
        string expectedSessionId,
        string expectedSessionGeneration,
        CancellationToken cancellationToken)
    {
        try
        {
            await LaunchDesktopAsync(
                id, expectedSessionId, expectedSessionGeneration, cancellationToken);
            Response.Headers["HX-Trigger"] = "wrighty:refresh";
            return new NoContentResult();
        }
        catch (TrackerException exception)
        {
            return await ItemError(id, exception, cancellationToken);
        }
    }

    private async Task<string> LaunchDesktopAsync(
        string id,
        string expectedSessionId,
        string expectedSessionGeneration,
        CancellationToken cancellationToken)
    {
        {
            var launch = await ResolveSessionLaunchAsync(
                id, expectedSessionId, expectedSessionGeneration, cancellationToken);
            // An unclaimed Done or archived item has left Wrighty's execution lifecycle. Opening
            // its vendor client is an unmanaged operator action, not a reason to reacquire it.
            var unmanagedTerminal = IsUnmanagedTerminal(launch.Item, launch.Claim);

            var capabilities = localAgentSessionLauncher.GetCapabilities(launch.Adapter.Agent);
            if (!capabilities.CanLaunchDesktop)
                throw new TrackerException(
                    "DESKTOP_SESSION_UNSUPPORTED",
                    capabilities.DesktopUnavailableReason ??
                    "Opening an agent Desktop session is unavailable on this platform.",
                    3);
            var address = launch.Adapter.BuildDesktopLaunch(
                    new SessionHandle(launch.Session.SessionId!))
                .EnableExperimental(
                    state.Config.EffectiveWorker.AllowsExperimentalDesktopSession(
                        launch.Adapter.Agent));
            if (!address.CanLaunch)
                throw new TrackerException(
                    "DESKTOP_SESSION_UNSUPPORTED",
                    address.Reason ?? "This vendor's Desktop session link is not enabled.",
                    3);

            if (unmanagedTerminal)
            {
                EnsureLaunched(
                    await localAgentSessionLauncher.LaunchDesktopAsync(address, cancellationToken),
                    "DESKTOP_APP_UNAVAILABLE");
                return $"Opened the recorded {AgentDisplayName(launch.Adapter.Agent)} Desktop " +
                       "session. The item remains outside Wrighty's management.";
            }

            var handle = await AcquireForLaunchAsync(
                launch, ClaimantKind.Human, cancellationToken);
            await LaunchOrReleaseAsync(
                launch,
                handle,
                async token => EnsureLaunched(
                    await localAgentSessionLauncher.LaunchDesktopAsync(address, token),
                    "DESKTOP_APP_UNAVAILABLE"),
                cancellationToken);
            return $"Opened {AgentDisplayName(launch.Adapter.Agent)} Desktop. Your human claim " +
                   "remains active; stop or idle Desktop before handing back to a worker.";
        }
    }

    /// <summary>
    /// Run a launch that a claim was just acquired for, releasing that claim if the launch fails.
    /// Getting in is one gesture; getting out is not, so the claim persists on success — the
    /// terminal or Desktop app holds the work and outlives this request. But a launch that never
    /// happened must leave no residue, exactly as the queue button releases when its move fails.
    /// </summary>
    private async Task LaunchOrReleaseAsync(
        ResolvedSessionLaunch launch,
        ClaimHandle handle,
        Func<CancellationToken, Task> action,
        CancellationToken cancellationToken)
    {
        try
        {
            await action(cancellationToken);
        }
        catch (TrackerException)
        {
            // Same shape as any other mutation that failed after acquiring: give the claim back,
            // preserving the dispatch state. Here that state is the needs-attention marker which
            // made the item reclaimable — clearing it would demote the item to a paused session
            // with no way back in. Verified live: it did.
            await ReleaseScaffoldingClaimAsync(launch.Id, handle, cancellationToken);
            state.Forget(launch.Id.Value);
            throw;
        }
    }

    /// <summary>
    /// Give back a claim that was only scaffolding for a mutation that has just failed.
    ///
    /// Best-effort by design: a failing release must not replace the error the caller is about to
    /// report, and an unreleased claim expires on its own. Preserving, because the mutation did not
    /// happen — a failed operation has no business discarding a decision it never touched.
    /// </summary>
    private async Task ReleaseScaffoldingClaimAsync(
        WorkItemId id,
        ClaimHandle handle,
        CancellationToken cancellationToken)
    {
        try
        {
            await tracker.ReleaseAsync(
                state.Config, id, handle, false, DispatchStateOnRelease.Preserve,
                cancellationToken);
        }
        catch (TrackerException)
        {
            // Reported state stays the caller's failure; the claim expires on its own.
        }
    }

    private async Task<IActionResult> Mutate(
        string id,
        Func<WorkItemId, Task> operation,
        string notice,
        CancellationToken cancellationToken,
        bool protectNonHumanClaim = false)
    {
        try
        {
            if (protectNonHumanClaim)
            {
                await EnsureWebMutationAllowed(id, cancellationToken);
            }
            await operation(tracker.ResolveId(state.Config, id));
            return Partial("Shared/_ItemDetail", await Item(id, notice, cancellationToken: cancellationToken));
        }
        catch (TrackerException exception)
        {
            try
            {
                Response.StatusCode = Status(exception);
                return Partial("Shared/_ItemDetail", await Item(id, error: exception, cancellationToken: cancellationToken));
            }
            catch (TrackerException) { return KnownError(exception); }
        }
    }

    private async Task EnsureWebMutationAllowed(string id, CancellationToken cancellationToken)
    {
        var editable = await tracker.GetEditableAsync(
            state.Config,
            tracker.ResolveId(state.Config, id),
            cancellationToken);
        if (IsExactWebClaim(editable.Claim) && state.TryHandle(editable.Item.Id.Value, out _))
        {
            return;
        }

        var claimant = AgentLabel(editable.Claim) ?? ClaimantKindLabel(editable.Claim) ?? "another claimant";
        throw new TrackerException(
            "CLAIM_STALE",
            $"This item is claimed by {claimant}. Take over explicitly before editing.",
            7);
    }

    private async Task<IActionResult> ItemError(
        string id,
        TrackerException exception,
        CancellationToken cancellationToken)
    {
        WebDiagnostics.RetainFailure(HttpContext, exception.Code, exception);
        // A conflict means the action's rendered preconditions no longer match current item,
        // claim, or session state. Keep the explanation in the panel, but refresh the Board in the
        // same response so the obsolete action is replaced without requiring a manual refresh.
        if (Status(exception) == StatusCodes.Status409Conflict)
            Response.Headers["HX-Trigger"] = "wrighty:refresh";
        try
        {
            Response.StatusCode = Status(exception);
            return Partial("Shared/_ItemDetail", await Item(id, error: exception, cancellationToken: cancellationToken));
        }
        catch (TrackerException)
        {
            return KnownError(exception);
        }
    }

    private IActionResult KnownError(TrackerException exception)
    {
        WebDiagnostics.RetainFailure(HttpContext, exception.Code, exception);
        Response.StatusCode = Status(exception);
        return Partial("Shared/_Error", new WebErrorModel(exception.Code, SafeMessage(exception)));
    }

    private string SafeMessage(TrackerException exception)
    {
        var message = exception.Message;
        if (state.Config.SourcePath is { } sourcePath)
        {
            var root = Path.GetDirectoryName(Path.GetFullPath(sourcePath));
            if (!string.IsNullOrEmpty(root))
            {
                message = message.Replace(root, "<tracker>", StringComparison.Ordinal);
            }
        }

        return message;
    }

    private async Task<ItemPageModel> Draft(
        string id,
        string title,
        string body,
        string status,
        string? priority,
        bool automaticExecutionAllowed,
        string? agentPolicy,
        string? executionProfile,
        TrackerException error,
        CancellationToken cancellationToken)
    {
        var current = await Item(id, editing: true, cancellationToken: cancellationToken);
        return current with
        {
            Title = title,
            Body = body,
            Status = status,
            Priority = priority,
            AutomaticExecutionAllowed = automaticExecutionAllowed,
            AgentPolicy = string.IsNullOrWhiteSpace(agentPolicy) ? null : agentPolicy,
            ExecutionProfile =
                string.IsNullOrWhiteSpace(executionProfile) ? null : executionProfile,
            ErrorCode = error.Code,
            ErrorMessage = SafeMessage(error),
            Editing = true
        };
    }

    private async Task<ItemPageModel> Item(
        string id,
        string? notice = null,
        TrackerException? error = null,
        bool editing = false,
        bool cardEntry = false,
        CancellationToken cancellationToken = default)
    {
        var resolvedId = tracker.ResolveId(state.Config, id);
        var editable = await tracker.GetEditableAsync(state.Config, resolvedId, cancellationToken);
        var item = editable.Item;
        // The durable session record (survives claim release, and carries the captured run outcome)
        // is the authority for the "Last run" block and the completed-vs-paused activity label.
        var operational = await tracker.GetOperationalAsync(state.Config, resolvedId, cancellationToken);
        var durableSession = operational.Session;
        var workspaceView = await WorkspaceViewAsync(durableSession, cancellationToken);
        var claimantKindLabel = ClaimantKindLabel(editable.Claim);
        var agentTypeLabel = AgentLabel(editable.Claim);
        var webMutationProtected = IsWebMutationProtected(editable.Claim);
        var session = durableSession ?? (HasResumeAddress(editable.Claim)
            ? new AgentSessionRecord(
                editable.Claim.Agent,
                editable.Claim.SessionId,
                editable.Claim.WorkspacePath,
                editable.Claim.ExpiresAt ?? DateTimeOffset.MinValue,
                editable.Claim.State != ClaimOwnershipState.HeldByOther)
            : null);
        var activity = OperationalStatuses.Resolve(
            item,
            editable.Claim,
            session,
            state.Config.DefaultPickFrom,
            state.Config.DefaultFinishTo);
        var lastRun = LastRunView.From(session);
        var providerBlock = await ProviderBlockAsync(item, activity, cancellationToken);
        var canQueueForWorker =
            !item.Archived &&
            activity == OperationalStatuses.NeedsAttention &&
            editable.Claim.State != ClaimOwnershipState.HeldByOther &&
            item.AutomaticExecutionAllowed &&
            string.Equals(item.Status, state.Config.DefaultPickTo,
                StringComparison.OrdinalIgnoreCase) &&
            session is { IsComplete: true, FromCurrentInstallation: true };
        string? claimProtectionNotice = null;
        if (webMutationProtected)
        {
            claimProtectionNotice = activity == OperationalStatuses.NeedsAttention
                ? $"{agentTypeLabel ?? claimantKindLabel ?? "The agent"} has paused and its headless process has exited. The retained claim is ownership and fencing metadata for the recorded session."
                : $"This item is claimed by {agentTypeLabel ?? claimantKindLabel ?? "another claimant"}. Takeover does not stop that process; it fences later cooperating Wrighty mutations. An operation already executing may finish first.";
        }

        return new ItemPageModel(
            item.Id.Value,
            tracker.FormatShort(state.Config, item.Id),
            item.Title,
            item.Body,
            item.Status,
            item.Priority,
            item.Archived,
            editable.Revision,
            editable.Claim.State,
            ClaimLabel(editable.Claim),
            claimantKindLabel,
            agentTypeLabel,
            webMutationProtected,
            claimProtectionNotice,
            editable.Claim.TakeoverAvailable && editable.Claim.State == ClaimOwnershipState.OwnedByCurrent,
            editable.Claim.ClaimantId,
            state.Generation(item.Id.Value),
            HasResumeAddress(editable.Claim),
            canQueueForWorker,
            BuildResumeCommand(item.Id, editable.Claim),
            BuildWorkerResumeCommand(item.Id, editable.Claim),
            BuildResumePrompt(item.Id, editable.Claim),
            HasResumeAddress(editable.Claim) ? RecordedAgentLabel(editable.Claim) : null,
            item.AutomaticExecutionAllowed,
            item.AgentPolicy,
            item.DispatchState,
            activity,
            state.Config.LocalMarkdown?.Statuses ?? [],
            state.Config.LocalMarkdown?.Priorities ?? [],
            markdown.Render(item.Body),
            notice,
            error?.Code,
            error is null ? null : SafeMessage(error),
            editing,
            cardEntry,
            item.EffectiveFields.ToDictionary(
                pair => pair.Key,
                pair => FormatFieldValue(pair.Value),
                StringComparer.Ordinal),
            item.RawFrontmatter,
            workspaceView,
            lastRun,
            session?.Dispatch,
            providerBlock,
            SessionAgentLabel: session?.Agent is null
                ? null
                : char.ToUpperInvariant(session.Agent[0]) + session.Agent[1..],
            SessionId: session?.SessionId,
            SessionLaunch: BuildSessionLaunch(
                item,
                editable.Claim,
                session,
                workspaceView),
            ExecutionProfile: item.ExecutionProfile,
            ExecutionProfiles: state.Config.Worker?.EffectiveExecutionProfiles ?? [],
            CreatedAt: item.CreatedAt,
            UpdatedAt: item.UpdatedAt);
    }

    // Reads the durable recorded session (which survives claim release, unlike editable.Claim) and,
    // when a worktree is recorded, safely calculates its git state for display. The probe applies
    // its own timeout and never throws for git failures, so a missing or unreadable worktree
    // degrades to an "unavailable" message instead of breaking the item view.
    private async Task<WorkspaceView?> WorkspaceViewAsync(
        AgentSessionRecord? session,
        CancellationToken cancellationToken)
    {
        if (session?.WorkspacePath is not { } workspacePath ||
            string.IsNullOrWhiteSpace(workspacePath))
        {
            return null;
        }

        var repositoryRoot = state.Config.SourcePath is { } sourcePath
            ? Path.GetDirectoryName(Path.GetFullPath(sourcePath)) ?? Directory.GetCurrentDirectory()
            : Directory.GetCurrentDirectory();
        var status = await workspaceInventory.GetStatusAsync(
            repositoryRoot, workspacePath, session.Branch, cancellationToken);
        // Completion commands are only meaningful when the git state could actually be read and a
        // branch is recorded; otherwise the workspace-status line already explains why it is
        // unavailable. The integrate step reads the current worker.completion.integration setting
        // (a repo preference, deliberately not snapshotted onto the item), matching the CLI/skill.
        var completionActions = status is { IsAvailable: true, Status: { } gitStatus }
            && session.Branch is { } branch
            ? WorkerCompletionGuidance.ForCompletedWorktree(
                workspacePath,
                branch,
                state.Config.Worker?.Completion?.Integration,
                gitStatus.Dirty,
                gitStatus.MergedIntoHead)
            : [];
        return new WorkspaceView(
            workspacePath,
            session.Branch,
            status.IsAvailable,
            status.Status?.Dirty ?? false,
            status.Status?.MergedIntoHead ?? false,
            status.Unavailable,
            status.WorktreeAbsent,
            completionActions);
    }

    private string? BuildResumeCommand(WorkItemId id, WorkItemClaimSummary claim)
    {
        if (!HasResumeAddress(claim) ||
            ClaimantKinds.FromStorageValue(claim.ClaimantKind) != ClaimantKind.Agent ||
            !state.TryHandle(id.Value, out var handle) ||
            !string.Equals(handle.ClaimantId, claim.ClaimantId, StringComparison.Ordinal) ||
            handle.ClaimToken is null)
        {
            return null;
        }

        IAgentAdapter adapter = claim.Agent switch
        {
            "claude" => new ClaudeAgentAdapter(),
            "codex" => new CodexAgentAdapter(),
            "copilot" => new CopilotAgentAdapter(),
            _ => throw new TrackerException(
                "AGENT_UNSUPPORTED",
                $"Unsupported recorded agent '{claim.Agent}'.",
                3)
        };
        var environment = TrackerEnvironment();
        environment["WRIGHTY_CLAIMANT_ID"] = handle.ClaimantId;
        environment["WRIGHTY_CLAIM_TOKEN"] = handle.ClaimToken;
        return adapter.BuildInteractiveCommand(
            new SessionHandle(claim.SessionId!),
            new Workspace(claim.WorkspacePath!),
            environment);
    }

    private string? BuildWorkerResumeCommand(WorkItemId id, WorkItemClaimSummary claim)
    {
        if (!HasResumeAddress(claim) ||
            !state.TryHandle(id.Value, out var handle) ||
            !string.Equals(handle.ClaimantId, claim.ClaimantId, StringComparison.Ordinal) ||
            handle.ClaimToken is null)
        {
            return null;
        }

        var configPrefix = string.IsNullOrWhiteSpace(state.Config.SourcePath)
            ? string.Empty
            : $"{TrackerConfigLoader.ConfigPathEnvironmentVariable}=" +
              $"{ShellQuote(Path.GetFullPath(state.Config.SourcePath))} ";
        return $"cd {ShellQuote(claim.WorkspacePath!)} && " +
               configPrefix +
               $"WRIGHTY_CLAIMANT_ID={ShellQuote(handle.ClaimantId)} " +
               $"WRIGHTY_CLAIM_TOKEN={ShellQuote(handle.ClaimToken)} " +
               $"wrighty worker --item {ShellQuote(id.Value)} --resume --yes";
    }

    private static string ShellQuote(string value) =>
        $"'{value.Replace("'", "'\\''", StringComparison.Ordinal)}'";

    private Dictionary<string, string> TrackerEnvironment()
    {
        var environment = new Dictionary<string, string>();
        if (!string.IsNullOrWhiteSpace(state.Config.SourcePath))
            environment[TrackerConfigLoader.ConfigPathEnvironmentVariable] =
                Path.GetFullPath(state.Config.SourcePath);
        return environment;
    }

    private static string? BuildResumePrompt(WorkItemId id, WorkItemClaimSummary claim) =>
        HasResumeAddress(claim) &&
        ClaimantKinds.FromStorageValue(claim.ClaimantKind) == ClaimantKind.Agent &&
        claim.Agent is not null
            ? WorkerPrompt.ForResume(id, claim.Agent)
            : null;

    private static bool HasResumeAddress(WorkItemClaimSummary claim) =>
        claim.Agent is "claude" or "codex" or "copilot" &&
        !string.IsNullOrWhiteSpace(claim.SessionId) &&
        !string.IsNullOrWhiteSpace(claim.WorkspacePath);

    private SessionLaunchView? BuildSessionLaunch(
        WorkItemDetail item,
        WorkItemClaimSummary claim,
        AgentSessionRecord? session,
        WorkspaceView? workspace)
    {
        if (!TryResolveLaunchSession(
                session,
                workspace,
                out var completeSession,
                out var agent,
                out var sessionId,
                out var adapter))
            return null;

        var capabilities = localAgentSessionLauncher.GetCapabilities(agent);
        var runtime = agentRuntimeCatalog.Snapshot().Find(agent);
        var ownsAgent = OwnsCurrentAgentClaim(item.Id, claim);
        var ownsHuman = OwnsCurrentHumanClaim(item.Id, claim);
        var unmanagedTerminal = IsUnmanagedTerminal(item, claim);
        var desktop = adapter.BuildDesktopLaunch(new SessionHandle(sessionId))
            .EnableExperimental(
                state.Config.EffectiveWorker.AllowsExperimentalDesktopSession(agent));
        var runtimeInstalled = runtime is { Installed: true };
        var canOpenCli =
            (ownsAgent || unmanagedTerminal) && capabilities.CanLaunchCli && runtimeInstalled;
        var cliReason = CliUnavailableReason(
            agent,
            claim,
            ownsHuman,
            ownsAgent || unmanagedTerminal,
            runtimeInstalled,
            capabilities);
        var canOpenDesktop =
            (ownsHuman || unmanagedTerminal) &&
            capabilities.CanLaunchDesktop &&
            desktop.CanLaunch;
        var desktopReason = DesktopUnavailableReason(
            desktop,
            capabilities,
            ownsHuman || unmanagedTerminal,
            canOpenDesktop);

        return new SessionLaunchView(
            agent,
            AgentDisplayName(agent),
            sessionId,
            SessionGeneration(item.Id, item.Status, item.Archived, completeSession, claim),
            canOpenCli,
            cliReason,
            canOpenDesktop,
            desktop.Support,
            desktopReason,
            DesktopIsHumanSupervised: ownsHuman,
            UnmanagedTerminal: unmanagedTerminal,
            desktop.Prerequisite,
            desktop.CompatibilityWarning);
    }

    private bool TryResolveLaunchSession(
        AgentSessionRecord? session,
        WorkspaceView? workspace,
        out AgentSessionRecord completeSession,
        out string agent,
        out string sessionId,
        out IAgentAdapter adapter)
    {
        completeSession = null!;
        agent = string.Empty;
        sessionId = string.Empty;
        adapter = null!;
        if (session is not
            {
                IsComplete: true,
                FromCurrentInstallation: true,
                Agent: { } recordedAgent,
                SessionId: { } recordedSessionId
            } ||
            workspace is null ||
            workspace.Removed ||
            !adaptersByName.TryGetValue(recordedAgent, out var recordedAdapter))
        {
            return false;
        }

        completeSession = session;
        agent = recordedAgent;
        sessionId = recordedSessionId;
        adapter = recordedAdapter;
        return true;
    }

    private static string? CliUnavailableReason(
        string agent,
        WorkItemClaimSummary claim,
        bool ownsHuman,
        bool ownsAgent,
        bool runtimeInstalled,
        LocalSessionLaunchCapabilities capabilities)
    {
        if (ownsAgent && capabilities.CanLaunchCli && runtimeInstalled)
            return null;
        if (!ownsAgent)
            return CliOwnershipGuidance(agent, claim, ownsHuman);
        return runtimeInstalled
            ? capabilities.CliUnavailableReason
            : $"{AgentDisplayName(agent)} CLI is not installed.";
    }

    private static string? DesktopUnavailableReason(
        DesktopLaunchAddress desktop,
        LocalSessionLaunchCapabilities capabilities,
        bool ownsDesktopReview,
        bool canOpenDesktop)
    {
        if (canOpenDesktop)
            return null;
        if (!desktop.CanLaunch)
            return desktop.Reason;
        return ownsDesktopReview
            ? capabilities.DesktopUnavailableReason
            : "Take over as human before opening Desktop. Completed unclaimed sessions " +
              "may be opened for review.";
    }

    private async Task<ResolvedSessionLaunch> ResolveSessionLaunchAsync(
        string id,
        string expectedSessionId,
        string expectedSessionGeneration,
        CancellationToken cancellationToken)
    {
        var resolved = tracker.ResolveId(state.Config, id);
        var operational = await tracker.GetOperationalAsync(
            state.Config, resolved, cancellationToken);
        var session = operational.Session;
        if (session is not
            {
                IsComplete: true,
                FromCurrentInstallation: true,
                Agent: { } agent,
                SessionId: { } sessionId,
                WorkspacePath: { } workspacePath
            } ||
            !adaptersByName.TryGetValue(agent, out var adapter))
        {
            throw new TrackerException(
                "RESUME_ADDRESS_UNAVAILABLE",
                "This item does not have a complete agent session address from this Wrighty " +
                "installation.",
                5);
        }
        if (!Directory.Exists(workspacePath))
            throw new TrackerException(
                "RESUME_WORKTREE_ABSENT",
                "The recorded workspace is no longer present on this host.",
                5);
        if (!SessionIdsEqual(sessionId, expectedSessionId) ||
            !string.Equals(
                SessionGeneration(
                    resolved,
                    operational.Item.Status,
                    operational.Item.Archived,
                    session,
                    operational.Claim),
                expectedSessionGeneration,
                StringComparison.Ordinal))
        {
            throw new TrackerException(
                "RESUME_SESSION_CHANGED",
                "The recorded session or its ownership changed after this item panel was loaded.",
                6);
        }
        return new ResolvedSessionLaunch(
            resolved, operational.Item, operational.Claim, session, adapter);
    }

    private bool OwnsCurrentAgentClaim(WorkItemId id, WorkItemClaimSummary claim) =>
        OwnsCurrentClaim(id, claim, ClaimantKind.Agent);

    private bool OwnsCurrentHumanClaim(WorkItemId id, WorkItemClaimSummary claim) =>
        OwnsCurrentClaim(id, claim, ClaimantKind.Human);

    private Task ValidateCurrentClaimAsync(
        ResolvedSessionLaunch launch,
        ClaimHandle handle,
        CancellationToken cancellationToken) =>
        tracker.RenewClaimAsync(
            state.Config,
            launch.Id,
            handle,
            launch.Session.WorkspacePath,
            launch.Session.SessionId,
            launch.Session.Branch,
            cancellationToken);

    /// <summary>
    /// Whether a launch may reclaim the claim this item already holds, per plan 028's decision
    /// that a launch may take back <em>this installation's own ended session</em>.
    ///
    /// The ordinary state of a paused item is that it still holds the agent claim of the run that
    /// just stopped, on this installation, until the lease expires. Refusing that case would put
    /// the launch actions out of reach in exactly the state an operator wants them, so reclaim is
    /// allowed — but only on recorded evidence, never on a guess about whether a process is alive:
    ///
    /// - the claim is held by <b>this installation</b>; another installation's claim is refused
    ///   below this method, by the claim service itself;
    /// - the item carries the <b>needs-attention</b> dispatch state, which the run itself wrote
    ///   when it stopped. That marker is evidence the run ended rather than an inference that it
    ///   did, and its absence means "treat as live" — explicit takeover stays the override;
    /// - no <b>worker decision is pending</b> (queued, retry-scheduled, handoff-queued): a worker
    ///   is about to act on those, so reclaiming races it. This is the rule the board already
    ///   applies to dragging;
    /// - the claim addresses the <b>same session</b> being launched.
    ///
    /// Safety does not come from detecting the vendor process — Wrighty cannot — it comes from
    /// fencing. Takeover mints a fresh claim token, so a client that turns out to still be running
    /// is rejected with CLAIM_STALE on its next cooperating mutation instead of silently writing
    /// over the operator who reclaimed.
    /// </summary>
    private static bool CanReclaimEndedSession(
        string? dispatchState,
        WorkItemClaimSummary claim,
        AgentSessionRecord session) =>
        claim.State == ClaimOwnershipState.OwnedByCurrent &&
        string.Equals(
            dispatchState, DispatchStates.NeedsAttention, StringComparison.OrdinalIgnoreCase) &&
        (claim.SessionId is null ||
         SessionIdsEqual(claim.SessionId, session.SessionId!));

    /// <summary>
    /// Acquire the claim a launch needs, reclaiming this installation's own ended session when
    /// <see cref="CanReclaimEndedSession"/> allows it. Returns the handle the caller must launch
    /// with — the one already held when the item is the operator's under <paramref name="kind"/>,
    /// otherwise a freshly acquired or reclaimed one.
    ///
    /// Reclaim goes through takeover rather than a fresh acquisition on purpose: takeover carries
    /// the recorded address forward (agent and session fall back to the current claim's), where a
    /// direct acquisition cannot and once wrote <c>agent: null</c> over a live address.
    /// </summary>
    private async Task<ClaimHandle> AcquireForLaunchAsync(
        ResolvedSessionLaunch launch,
        ClaimantKind kind,
        CancellationToken cancellationToken)
    {
        if (OwnsCurrentClaim(launch.Id, launch.Claim, kind) &&
            state.TryHandle(launch.Id.Value, out var owned))
        {
            await ValidateCurrentClaimAsync(launch, owned, cancellationToken);
            return owned;
        }

        // A pending worker decision refuses before any acquisition, claimed or not: queued,
        // retry-scheduled and handoff-queued all mean a worker is about to act on this item, and
        // an unclaimed one races it just as surely as a held one — the worker is about to claim it.
        if (launch.Item.DispatchState is { } pending &&
            !string.Equals(
                pending, DispatchStates.NeedsAttention, StringComparison.OrdinalIgnoreCase))
        {
            throw new TrackerException(
                "SESSION_LAUNCH_NOT_ALLOWED",
                LaunchOwnershipRefusal(pending, launch.Claim),
                6);
        }

        // Every acquisition below arrives at the claim carrying the recorded address, because the
        // address is the item's way back to its work and a claim that drops it strands the item.
        // An agent claimant can state the address itself; a human claimant cannot, so the human
        // modes reach their claim *through* an agent one rather than acquiring directly.
        var agentContext = new AgentExecutionContext(
            launch.Session.Agent,
            launch.Session.SessionId,
            AgentContextSource.ExplicitOption,
            ClaimantKind: ClaimantKind.Agent,
            ClaimantId: $"agent:web-launch:{Guid.NewGuid():N}");

        ClaimResult result;
        if (launch.Claim.State == ClaimOwnershipState.Unclaimed)
        {
            result = await tracker.ClaimAsync(
                state.Config, launch.Id, agentContext, cancellationToken);
            result = await tracker.RenewClaimAsync(
                state.Config,
                launch.Id,
                new ClaimHandle(
                    agentContext with { ClaimantId = result.ClaimantId }, result.ClaimToken),
                launch.Session.WorkspacePath,
                launch.Session.SessionId,
                cancellationToken);
        }
        else if (CanReclaimEndedSession(
            launch.Item.DispatchState, launch.Claim, launch.Session))
        {
            result = await tracker.TakeoverAsync(
                state.Config,
                launch.Id,
                agentContext,
                state.TryHandle(launch.Id.Value, out var previous) ? previous.ClaimToken : null,
                cancellationToken);
        }
        else
        {
            throw new TrackerException(
                "SESSION_LAUNCH_NOT_ALLOWED",
                LaunchOwnershipRefusal(launch.Item.DispatchState, launch.Claim),
                6);
        }

        var context = agentContext;
        if (kind == ClaimantKind.Human)
        {
            // Rotate to the web console's human claimant. Takeover carries the current claim's agent
            // and session forward where a direct human acquisition cannot, which is why the human
            // modes cannot simply claim: renewing does not restore a dropped agent, so the address
            // would be gone by the time anything noticed.
            context = state.ClaimantContext;
            result = await tracker.TakeoverAsync(
                state.Config, launch.Id, context, result.ClaimToken, cancellationToken);
        }

        var handle = new ClaimHandle(
            context with { ClaimantId = result.ClaimantId }, result.ClaimToken);
        state.Retain(launch.Id.Value, result, handle.Claimant);
        return handle;
    }

    private static string LaunchOwnershipRefusal(
        string? dispatchState,
        WorkItemClaimSummary claim)
    {
        if (claim.State == ClaimOwnershipState.HeldByOther)
        {
            return "Another Wrighty installation holds this item. Opening its session would " +
                "displace that claimant; take over explicitly if that is what you mean.";
        }

        if (dispatchState is { } dispatch &&
            !string.Equals(
                dispatch, DispatchStates.NeedsAttention, StringComparison.OrdinalIgnoreCase))
        {
            return "A worker decision is pending on this item. Cancel it first — opening the " +
                "session now would race the worker.";
        }

        return "This item's session is still running on this installation. Wrighty cannot see " +
            "whether the vendor client stopped, so take over explicitly to open it anyway.";
    }

    private bool OwnsCurrentClaim(
        WorkItemId id,
        WorkItemClaimSummary claim,
        ClaimantKind kind) =>
        claim.State == ClaimOwnershipState.OwnedByCurrent &&
        ClaimantKinds.FromStorageValue(claim.ClaimantKind) == kind &&
        state.TryHandle(id.Value, out var handle) &&
        handle.ClaimToken is not null &&
        string.Equals(handle.ClaimantId, claim.ClaimantId, StringComparison.Ordinal);

    private string SessionGeneration(
        WorkItemId id,
        string? status,
        bool archived,
        AgentSessionRecord session,
        WorkItemClaimSummary claim)
    {
        var value =
            $"{status}\n{archived}\n{session.Agent}\n{session.SessionId}\n" +
            $"{session.WorkspacePath}\n" +
            $"{session.FromCurrentInstallation}\n{claim.State}\n{claim.ClaimantKind}\n" +
            $"{claim.ClaimantId}\n{state.Generation(id.Value)}";
        return Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(value)));
    }

    private static bool SessionIdsEqual(string expected, string actual) =>
        Guid.TryParse(expected, out var expectedUuid) &&
        Guid.TryParse(actual, out var actualUuid)
            ? expectedUuid == actualUuid
            : string.Equals(expected, actual, StringComparison.Ordinal);

    private static string AgentDisplayName(string agent) =>
        string.IsNullOrEmpty(agent)
            ? "Agent"
            : char.ToUpperInvariant(agent[0]) + agent[1..];

    private static string CliOwnershipGuidance(
        string agent,
        WorkItemClaimSummary claim,
        bool ownsHuman)
    {
        var firstAction = "Take over for editing…";
        if (ownsHuman)
            firstAction = "Edit";
        else if (claim.State == ClaimOwnershipState.Unclaimed)
            firstAction = "Claim for editing";
        return $"Select “{firstAction}”, then open “More actions…” and choose " +
               $"“Save and show manual {AgentDisplayName(agent)} resume command”.";
    }

    private static void EnsureLaunched(
        SessionLaunchResult result,
        string fallbackCode)
    {
        if (result.Launched)
            return;
        var code = result.Status switch
        {
            SessionLaunchStatus.ApplicationMissing => "DESKTOP_APP_UNAVAILABLE",
            SessionLaunchStatus.Unsupported => fallbackCode,
            _ => "SESSION_LAUNCH_FAILED"
        };
        throw new TrackerException(
            code,
            code switch
            {
                "DESKTOP_APP_UNAVAILABLE" =>
                    "The Desktop application or URI handler is unavailable.",
                "TERMINAL_LAUNCH_UNSUPPORTED" =>
                    "Opening a new agent terminal is unavailable.",
                _ => "The local agent session could not be launched."
            },
            3);
    }

    private sealed record ResolvedSessionLaunch(
        WorkItemId Id,
        WorkItemDetail Item,
        WorkItemClaimSummary Claim,
        AgentSessionRecord Session,
        IAgentAdapter Adapter);

    private static string FormatFieldValue(JsonElement value) =>
        value.ValueKind is JsonValueKind.Object or JsonValueKind.Array
            ? JsonSerializer.Serialize(value, IndentedJson)
            : value.ToString();

    private BoardPageModel Board(
        DashboardSnapshot snapshot,
        ArchiveScope scope,
        string responseRevision,
        IReadOnlyList<ProviderCapacityView> providerCapacity,
        BoardListQuery query)
    {
        var circuitsByAgent = providerCapacity.ToDictionary(
            value => value.Agent,
            StringComparer.OrdinalIgnoreCase);
        var cards = snapshot.Items.Select(value =>
        {
            // The durable session record travels with the snapshot so the board can tell a
            // completed retained session from a paused one — the same distinction the single-item
            // page already draws from this record.
            var activity = OperationalStatuses.Resolve(
                value.Item,
                value.Claim,
                state.Config.DefaultPickFrom,
                value.Session,
                state.Config.DefaultFinishTo);
            var agent = ResolvedProviderAgent(value.Item.AgentPolicy);
            var providerBlock = activity == OperationalStatuses.Ready &&
                agent is not null &&
                circuitsByAgent.TryGetValue(agent, out var availability)
                    ? availability
                    : null;
            return new BoardCardModel(
                value.Item.Id.Value,
                tracker.FormatShort(state.Config, value.Item.Id),
                value.Item.Title,
                value.Item.Status,
                value.Item.Priority,
                value.Item.Archived,
                value.Claim.State,
                ClaimLabel(value.Claim),
                ClaimantKindLabel(value.Claim),
                AgentLabel(value.Claim),
                value.Item.AutomaticExecutionAllowed,
                value.Item.AgentPolicy,
                value.Item.DispatchState,
                activity,
                value.HasRecordedWorktree,
                providerBlock,
                CardActions(value, activity, snapshot.Statuses),
                DropTargets(value, snapshot.Statuses),
                value.Item.CreatedAt,
                value.Item.UpdatedAt,
                BoardAgentKey(value));
        })
            .ToArray();
        var filtered = cards.Where(card => query.Matches(card, DateTimeOffset.UtcNow)).ToArray();
        var active = filtered.Where(card => !card.Archived).ToArray();
        var columns = snapshot.Statuses
            .Select((status, index) => new BoardColumnModel(
                status,
                active
                    .Where(card => string.Equals(card.Status, status, StringComparison.OrdinalIgnoreCase))
                    .Order(new BoardCardComparer(query.SortForColumn(index), snapshot.Priorities))
                    .Select(card => TimestampCard(card, query.SortForColumn(index)))
                    .ToArray(),
                index,
                query.SortForColumn(index)))
            .ToList();
        var unassignedIndex = snapshot.Statuses.Count;
        var unassigned = active
            .Where(card => card.Status is null ||
                !snapshot.Statuses.Contains(card.Status, StringComparer.OrdinalIgnoreCase))
            .Order(new BoardCardComparer(query.SortForColumn(unassignedIndex), snapshot.Priorities))
            .Select(card => TimestampCard(card, query.SortForColumn(unassignedIndex)))
            .ToArray();
        if (unassigned.Length > 0)
        {
            columns.Add(new BoardColumnModel(
                "No configured status",
                unassigned,
                unassignedIndex,
                query.SortForColumn(unassignedIndex)));
        }
        return new BoardPageModel(
            snapshot.Statuses,
            snapshot.Priorities,
            columns,
            filtered
                .Where(card => card.Archived)
                .Order(new BoardCardComparer(query.Sort, snapshot.Priorities))
                .Select(card => TimestampCard(card, query.Sort))
                .ToArray(),
            scope.ToString().ToLowerInvariant(),
            responseRevision,
            ProviderCapacity: providerCapacity,
            Query: query);
    }

    private static BoardCardModel TimestampCard(BoardCardModel card, ItemSort sort) =>
        card with
        {
            DisplayTimestampField = sort.Field == ItemSortField.Created
                ? ItemSortField.Created
                : ItemSortField.Updated
        };

    private async Task<IActionResult> ProviderCapacityProbeAsync(
        string? notice,
        CancellationToken cancellationToken,
        TrackerException? error = null)
    {
        var (_, providers) = await ProviderViewsAsync(cancellationToken);
        var model = new ProviderCapacityPageModel(
            providers,
            ProviderRevision(providers),
            notice,
            error?.Code,
            error is null ? null : SafeMessage(error));
        return Partial("Shared/_ProviderCapacity", model);
    }

    private async Task<(
        IReadOnlyList<ProviderCapacityView> Circuits,
        IReadOnlyList<ProviderCapacityView> Probes)> ProviderViewsAsync(
        CancellationToken cancellationToken)
    {
        var availability = await providerCapacity.ListAsync(cancellationToken);
        var byAgent = availability.ToDictionary(
            value => value.Agent,
            StringComparer.OrdinalIgnoreCase);
        var circuits = availability
            .Where(value => value.State != ProviderCapacityState.Available)
            .Select(ProviderCapacityView.From)
            .ToArray();
        var probes = providerCapacityProbe.SupportedAgents
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(value => value, StringComparer.OrdinalIgnoreCase)
            .Select(agent => byAgent.TryGetValue(agent, out var current)
                ? ProviderCapacityView.From(current)
                : ProviderCapacityView.Available(agent))
            .ToArray();
        return (circuits, probes);
    }

    private static string ResponseRevision(
        string snapshotRevision,
        ArchiveScope scope,
        IReadOnlyList<ProviderCapacityView> providerCapacity,
        string queryRevision)
    {
        var providers = string.Join(
            '\n',
            providerCapacity
                .OrderBy(value => value.Agent, StringComparer.OrdinalIgnoreCase)
                .Select(value =>
                    $"{value.Agent}|{value.State}|{value.Reason}|{value.Until:O}|" +
                    $"{value.Confidence}|{value.ConsecutiveFailures}"));
        var value = $"{snapshotRevision}\n{scope}\n{providers}\n{queryRevision}";
        return Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(value)));
    }

    private static string ProviderRevision(
        IReadOnlyList<ProviderCapacityView> providers)
    {
        var value = string.Join(
            '\n',
            providers
                .OrderBy(provider => provider.Agent, StringComparer.OrdinalIgnoreCase)
                .Select(provider =>
                    $"{provider.Agent}|{provider.State}|{provider.Reason}|" +
                    $"{provider.Until:O}|{provider.Confidence}|" +
                    $"{provider.ConsecutiveFailures}"));
        return Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(value)));
    }

    private async Task<ProviderCapacityView?> ProviderBlockAsync(
        WorkItemDetail item,
        string activity,
        CancellationToken cancellationToken)
    {
        if (activity != OperationalStatuses.Ready)
            return null;
        var agent = ResolvedProviderAgent(item.AgentPolicy);
        if (agent is null)
            return null;
        var availability = await providerCapacity.GetAsync(agent, cancellationToken);
        return availability is null or { State: ProviderCapacityState.Available }
            ? null
            : ProviderCapacityView.From(availability);
    }

    private string? ResolvedProviderAgent(string? agentPolicy)
    {
        var value = string.IsNullOrWhiteSpace(agentPolicy)
            ? state.Config.EffectiveWorker.DefaultAgent
            : agentPolicy;
        return string.IsNullOrWhiteSpace(value)
            ? null
            : value.Trim().ToLowerInvariant();
    }

    private string? BoardAgentKey(DashboardWorkItem item) =>
        NormalizedAgentKey(item.Claim.Agent) ??
        NormalizedAgentKey(item.Session?.Agent) ??
        ResolvedProviderAgent(item.Item.AgentPolicy);

    private static string ClaimLabel(WorkItemClaimSummary claim) => claim.State switch
    {
        ClaimOwnershipState.Unclaimed => "Unclaimed",
        ClaimOwnershipState.OwnedByCurrent => "Claimed by this Wrighty installation",
        _ => "Claimed by another Wrighty installation"
    };

    private static string? AgentLabel(WorkItemClaimSummary claim)
    {
        return AgentKey(claim) switch
        {
            "codex" => "Codex",
            "claude" => "Claude",
            "copilot" => "Copilot",
            { } agent => AgentDisplayName(agent),
            _ => null
        };
    }

    private static string? AgentKey(WorkItemClaimSummary claim)
    {
        if (claim.State == ClaimOwnershipState.Unclaimed ||
            ClaimantKinds.FromStorageValue(claim.ClaimantKind) != ClaimantKind.Agent ||
            string.IsNullOrWhiteSpace(claim.Agent))
        {
            return null;
        }

        return NormalizedAgentKey(claim.Agent);
    }

    private static string? NormalizedAgentKey(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return null;
        const int maxAgentLength = 64;
        var agent = value.Trim();
        if (agent.Any(char.IsControl)) return null;
        if (agent.Length > maxAgentLength) agent = agent[..maxAgentLength];
        return agent.ToLowerInvariant();
    }

    private static string? RecordedAgentLabel(WorkItemClaimSummary claim) =>
        claim.Agent?.Trim().ToLowerInvariant() switch
        {
            "codex" => "Codex",
            "claude" => "Claude",
            "copilot" => "Copilot",
            _ => null
        };

    private static string? ClaimantKindLabel(WorkItemClaimSummary claim)
    {
        if (claim.State == ClaimOwnershipState.Unclaimed) return null;
        return ClaimantKinds.FromStorageValue(claim.ClaimantKind) switch
        {
            ClaimantKind.Agent => "Agent",
            ClaimantKind.Human => "Human",
            ClaimantKind.Automation => "Automation",
            _ => "Unknown"
        };
    }

    private bool IsWebMutationProtected(WorkItemClaimSummary claim) =>
        claim.State != ClaimOwnershipState.Unclaimed && !IsExactWebClaim(claim);

    private bool IsExactWebClaim(WorkItemClaimSummary claim) =>
        claim.State == ClaimOwnershipState.OwnedByCurrent &&
        string.Equals(claim.ClaimantId, state.ClaimantId, StringComparison.Ordinal);

    private ClaimHandle RequiredWebHandle(string id)
    {
        var resolved = tracker.ResolveId(state.Config, id);
        if (state.TryHandle(resolved.Value, out var handle)) return handle;
        throw new TrackerException("CLAIM_TOKEN_REQUIRED",
            "This web session does not hold the claim token. Use explicit takeover to recover the claim.", 6);
    }

    private static ArchiveScope ParseScope(string? scope) => scope?.ToLowerInvariant() switch
    {
        "archived" => ArchiveScope.Archived,
        "all" => ArchiveScope.All,
        _ => ArchiveScope.Active
    };

    private static int Status(TrackerException exception) => exception.Code switch
    {
        "WORK_ITEM_NOT_FOUND" => 404,
        "CLAIM_REQUIRED" or "CLAIM_HELD" or "CLAIM_HELD_BY_LOCAL_CLAIMANT" or "CLAIM_STALE" or "CLAIM_TOKEN_REQUIRED" or
            "UPDATE_CONFLICT" or "WEB_CLAIM_GENERATION_STALE" or
            "WORKER_ITEM_NOT_PAUSED" or "RESUME_SESSION_CHANGED" or
            "SESSION_LAUNCH_NOT_ALLOWED" or "RESUME_ADDRESS_UNAVAILABLE" or
            "RESUME_WORKTREE_ABSENT" or "STATUS_MOVE_NOT_ALLOWED" => 409,
        "LOCAL_STORE_INVALID" or "CONFIG_INVALID" or
            "TERMINAL_LAUNCH_UNSUPPORTED" or "DESKTOP_SESSION_UNSUPPORTED" or
            "DESKTOP_APP_UNAVAILABLE" => 422,
        _ when exception.ExitCode == 2 => 400,
        _ => 500
    };
}
