using System.Globalization;
using System.Text.RegularExpressions;
using Highbyte.Wrighty.Addressing;
using Highbyte.Wrighty.AgentContext;
using Highbyte.Wrighty.Backends;
using Highbyte.Wrighty.Claims;
using Highbyte.Wrighty.Configuration;
using Highbyte.Wrighty.Errors;
using Highbyte.Wrighty.Identity;
using Highbyte.Wrighty.Models;
using Highbyte.Wrighty.Time;

namespace Highbyte.Wrighty.LocalMarkdown;

public sealed partial class LocalMarkdownTrackerBackend(
    IWorkerIdentityProvider identityProvider,
    IClock clock) : ITrackerBackend
{
    private const string GitIgnoreComment = "# Wrighty runtime state";
    private static readonly string[] GitIgnoreRules = ["/.lock", ".*.tmp"];
    private readonly LocalMarkdownWorkItemAddressResolver resolver = new();

    public string Name => "local-markdown";

    public IWorkItemAddressResolver AddressResolver => resolver;

    public async Task<BackendInitializationResult> InitializeAsync(
        TrackerConfig config,
        bool checkOnly,
        CancellationToken cancellationToken)
    {
        var paths = Paths(config);
        var actions = new List<string>();
        if (!Directory.Exists(paths.Root))
        {
            actions.Add("create local Wrighty directory");
        }

        if (!Directory.Exists(paths.Items))
        {
            actions.Add("create items directory");
        }

        if (!Directory.Exists(paths.Archive))
        {
            actions.Add("create archive directory");
        }

        if (!File.Exists(Path.Combine(paths.Root, ".lock")))
        {
            actions.Add("create store lock");
        }

        if (checkOnly)
        {
            if (actions.Count > 0)
            {
                throw new TrackerException(
                    "STORE_INITIALIZATION_REQUIRED",
                    $"Local Wrighty initialization is required: {string.Join("; ", actions)}. Run 'wrighty init'.",
                    5,
                    new Dictionary<string, object?> { ["path"] = paths.Root, ["actions"] = actions });
            }

            if (Directory.Exists(paths.Root))
            {
                await using var storeLock = await LocalStoreLock.AcquireAsync(paths.Root, cancellationToken);
                var documents = await LoadAllUnlockedAsync(config, cancellationToken);
                foreach (var document in documents)
                {
                    var canonical = CanonicalPath(config, document);
                    if (!string.Equals(document.Path, canonical, StringComparison.Ordinal))
                    {
                        actions.Add($"rename {Path.GetFileName(document.Path)} to {Path.GetFileName(canonical)}");
                    }
                }
            }

            if (actions.Count > 0)
            {
                throw new TrackerException(
                    "STORE_INITIALIZATION_REQUIRED",
                    $"Local Wrighty initialization is required: {string.Join("; ", actions)}. Run 'wrighty init'.",
                    5,
                    new Dictionary<string, object?> { ["path"] = paths.Root, ["actions"] = actions });
            }

            return new BackendInitializationResult(false, ["Local Markdown store is valid."]);
        }

        Directory.CreateDirectory(paths.Items);
        Directory.CreateDirectory(paths.Archive);
        await using (var storeLock = await LocalStoreLock.AcquireAsync(paths.Root, cancellationToken))
        {
            var documents = await LoadAllUnlockedAsync(config, cancellationToken);
            foreach (var document in documents)
            {
                var original = document.Path;
                var canonical = CanonicalPath(config, document);
                if (string.Equals(original, canonical, StringComparison.Ordinal))
                {
                    continue;
                }

                document.Path = canonical;
                await WriteUnlockedAsync(document, original, cancellationToken);
                actions.Add($"renamed {Path.GetFileName(original)} to {Path.GetFileName(canonical)}");
            }

            if (IsInsideGitWorktree(paths.Root))
            {
                var gitIgnoreAction = await EnsureGitIgnoreAsync(paths.Root, cancellationToken);
                if (gitIgnoreAction is not null)
                {
                    actions.Add(gitIgnoreAction);
                }
            }
        }

        return new BackendInitializationResult(actions.Count > 0, actions);
    }

    private static bool IsInsideGitWorktree(string path)
    {
        for (var directory = new DirectoryInfo(Path.GetFullPath(path));
             directory is not null;
             directory = directory.Parent)
        {
            var marker = Path.Combine(directory.FullName, ".git");
            if (Directory.Exists(marker) || File.Exists(marker))
            {
                return true;
            }
        }

        return false;
    }

    private static async Task<string?> EnsureGitIgnoreAsync(
        string root,
        CancellationToken cancellationToken)
    {
        var path = Path.Combine(root, ".gitignore");
        if (!File.Exists(path))
        {
            var content = string.Join(Environment.NewLine, [GitIgnoreComment, .. GitIgnoreRules]) +
                          Environment.NewLine;
            await File.WriteAllTextAsync(path, content, cancellationToken);
        return "created local Wrighty .gitignore";
        }

        var existing = await File.ReadAllTextAsync(path, cancellationToken);
        var lines = existing
            .Replace("\r\n", "\n", StringComparison.Ordinal)
            .Split('\n')
            .ToHashSet(StringComparer.Ordinal);
        var missing = GitIgnoreRules.Where(rule => !lines.Contains(rule)).ToArray();
        if (missing.Length == 0)
        {
            return null;
        }

        var separator = existing.Length == 0 || existing.EndsWith('\n')
            ? string.Empty
            : Environment.NewLine;
        var addition = string.Join(Environment.NewLine, [GitIgnoreComment, .. missing]) +
                       Environment.NewLine;
        await File.AppendAllTextAsync(path, separator + addition, cancellationToken);
        return "updated local Wrighty .gitignore";
    }

    public async Task<IReadOnlyList<WorkItemSummary>> ListAsync(
        TrackerConfig config,
        ListWorkItemsRequest request,
        CancellationToken cancellationToken)
    {
        EnsureStore(config);
        await using var storeLock = await LocalStoreLock.AcquireAsync(Paths(config).Root, cancellationToken);
        var documents = await LoadAllUnlockedAsync(config, cancellationToken);
        IEnumerable<LocalMarkdownDocument> query = documents;
        query = request.ArchiveScope switch
        {
            ArchiveScope.Active => query.Where(item => !item.Archived),
            ArchiveScope.Archived => query.Where(item => item.Archived),
            _ => query
        };
        if (!string.IsNullOrWhiteSpace(request.Status))
        {
            query = query.Where(item =>
                string.Equals(item.Status, request.Status, StringComparison.OrdinalIgnoreCase));
        }

        query = query
            .OrderBy(item => PriorityRank(config, item.Priority))
            .ThenBy(item => item.Id);
        if (request.Limit is not null)
        {
            if (request.Limit <= 0)
            {
                throw new TrackerException("ARGUMENT_INVALID", "limit must be positive.", 2);
            }

            query = query.Take(request.Limit.Value);
        }

        return query.Select(Summary).ToArray();
    }

    public async Task<WorkItemDetail?> GetAsync(
        TrackerConfig config,
        WorkItemId id,
        CancellationToken cancellationToken)
    {
        EnsureStore(config);
        await using var storeLock = await LocalStoreLock.AcquireAsync(Paths(config).Root, cancellationToken);
        var document = await FindUnlockedAsync(config, LocalMarkdownWorkItemAddressResolver.Decode(id), cancellationToken);
        return document is null ? null : Detail(document);
    }

    public async Task<CreateWorkItemResult> CreateAsync(
        TrackerConfig config,
        CreateWorkItemOperation operation,
        CancellationToken cancellationToken)
    {
        ValidateCreate(operation.Request);
        EnsureStore(config);
        var status = CanonicalStatus(config, operation.Request.Status ?? config.DefaultPickFrom);
        var priority = CanonicalPriority(config, operation.Request.Priority);
        var attemptId = CreationAttempt.NormalizeOrCreate(
            string.IsNullOrWhiteSpace(operation.CreationAttemptId) ? null : operation.CreationAttemptId);
        var resolvedRequest = operation.Request with { Status = status, Priority = priority };
        var requestHash = CreationAttempt.ComputeIntentHash(resolvedRequest, operation.ArchiveAfterCreate);
        var paths = Paths(config);
        await using var storeLock = await LocalStoreLock.AcquireAsync(paths.Root, cancellationToken);
        var existing = await LoadAllUnlockedAsync(config, cancellationToken);
        var matches = existing
            .Where(item => string.Equals(item.Creation?.AttemptId, attemptId, StringComparison.Ordinal))
            .ToArray();
        if (matches.Length > 1)
        {
            throw new TrackerException(
                "CREATION_ATTEMPT_DUPLICATE",
                $"Creation attempt '{attemptId}' is recorded by multiple local work items.",
                9,
                new Dictionary<string, object?>
                {
                    ["creationAttemptId"] = attemptId,
                    ["ids"] = matches.Select(item => $"local:{item.Id}").ToArray()
                });
        }

        if (matches.Length == 1)
        {
            var match = matches[0];
            if (!string.Equals(match.Creation!.RequestHash, requestHash, StringComparison.Ordinal))
            {
                throw new TrackerException(
                    "CREATION_ATTEMPT_CONFLICT",
                    $"Creation attempt '{attemptId}' was already used for a different request.",
                    9,
                    new Dictionary<string, object?>
                    {
                        ["creationAttemptId"] = attemptId,
                        ["id"] = $"local:{match.Id}"
                    });
            }

            var matchedDetail = Detail(match);
            return new CreateWorkItemResult(
                matchedDetail.Id,
                null,
                matchedDetail,
                attemptId,
                CreateDisposition.Resumed,
                []);
        }

        var idNumber = existing.Count == 0 ? 1 : checked(existing.Max(item => item.Id) + 1);
        var archived = operation.ArchiveAfterCreate;
        var directory = archived ? paths.Archive : paths.Items;
        var path = Path.Combine(directory, PortableFilenameSlugger.FileName(idNumber, operation.Request.Title));
        var document = LocalMarkdownDocumentCodec.Create(
            idNumber,
            path,
            archived,
            operation.Request.Title,
            operation.Request.Body,
            status,
            priority,
            new LocalCreationMetadata(1, attemptId, requestHash),
            clock.UtcNow);
        await WriteUnlockedAsync(document, originalPath: null, cancellationToken);
        var detail = Detail(document);
        return new CreateWorkItemResult(
            detail.Id,
            null,
            detail,
            attemptId,
            CreateDisposition.Created,
            []);
    }

    public async Task<UpdateWorkItemResult> UpdateAsync(
        TrackerConfig config,
        WorkItemId id,
        UpdateWorkItemOperation operation,
        CancellationToken cancellationToken)
    {
        WorkItemPatchValidator.Validate(operation.Patch);
        EnsureStore(config);
        var paths = Paths(config);
        await using var storeLock = await LocalStoreLock.AcquireAsync(paths.Root, cancellationToken);
        var document = await RequiredUnlockedAsync(config, id, cancellationToken);
        if (document.Archived)
        {
            throw Archived(id);
        }

        await EnsureOwnedUnlockedAsync(document, cancellationToken);
        var patch = operation.Patch;
        var changed = new List<string>();
        if (patch.Title.IsSpecified && !string.Equals(document.Title, patch.Title.Value, StringComparison.Ordinal))
        {
            document.Title = patch.Title.Value!;
            changed.Add("title");
        }

        if (patch.Body.IsSpecified && !string.Equals(document.Body, patch.Body.Value, StringComparison.Ordinal))
        {
            document.Body = patch.Body.Value!;
            changed.Add("body");
        }

        if (patch.Priority.IsSpecified)
        {
            var priority = CanonicalPriority(config, patch.Priority.Value);
            if (!string.Equals(document.Priority, priority, StringComparison.OrdinalIgnoreCase))
            {
                document.Priority = priority;
                changed.Add("priority");
            }
        }

        if (patch.Status.IsSpecified)
        {
            var status = CanonicalStatus(config, patch.Status.Value!);
            if (!string.Equals(document.Status, status, StringComparison.OrdinalIgnoreCase))
            {
                document.Status = status;
                changed.Add("status");
            }
        }

        if (operation.ArchiveAfterUpdate)
        {
            document.Archived = true;
            document.Claim = null;
            changed.Add("archived");
        }

        if (changed.Count == 0)
        {
            return new UpdateWorkItemResult(Detail(document), false, []);
        }

        var originalPath = document.Path;
        document.UpdatedAt = clock.UtcNow;
        document.Path = CanonicalPath(config, document);
        await WriteUnlockedAsync(document, originalPath, cancellationToken);
        return new UpdateWorkItemResult(Detail(document), true, changed);
    }

    public async Task<ClaimResult> TryClaimAsync(
        TrackerConfig config,
        WorkItemId id,
        AgentExecutionContext agentContext,
        CancellationToken cancellationToken)
    {
        EnsureStore(config);
        await using var storeLock = await LocalStoreLock.AcquireAsync(Paths(config).Root, cancellationToken);
        var document = await RequiredUnlockedAsync(config, id, cancellationToken);
        if (document.Archived)
        {
            throw Archived(id);
        }

        var worker = await identityProvider.GetIdentityAsync(cancellationToken);
        var now = clock.UtcNow;
        var current = document.Claim;
        if (current is not null && current.ExpiresAt > now)
        {
            return string.Equals(current.WorkerIdentity, worker, StringComparison.Ordinal)
                ? ClaimResult(current, ClaimOutcome.AlreadyOwned)
                : ClaimResult(current, ClaimOutcome.HeldByOther);
        }

        var claim = new LocalClaimMetadata(
            worker,
            agentContext.AgentType ?? "unknown",
            agentContext.SessionId,
            Guid.NewGuid().ToString("N"),
            now,
            now.AddMinutes(config.LeaseMinutes));
        document.ClaimEpoch++;
        document.Claim = claim;
        document.UpdatedAt = now;
        await WriteUnlockedAsync(document, document.Path, cancellationToken);
        return ClaimResult(claim, ClaimOutcome.Acquired);
    }

    public async Task<ClaimOwnershipResult> GetClaimOwnershipAsync(
        TrackerConfig config,
        WorkItemId id,
        CancellationToken cancellationToken)
    {
        EnsureStore(config);
        await using var storeLock = await LocalStoreLock.AcquireAsync(Paths(config).Root, cancellationToken);
        var document = await RequiredUnlockedAsync(config, id, cancellationToken);
        var current = document.Claim;
        if (current is null || current.ExpiresAt <= clock.UtcNow)
        {
            return new ClaimOwnershipResult(ClaimOwnershipState.Unclaimed);
        }

        var worker = await identityProvider.GetIdentityAsync(cancellationToken);
        return new ClaimOwnershipResult(
            string.Equals(current.WorkerIdentity, worker, StringComparison.Ordinal)
                ? ClaimOwnershipState.OwnedByCurrent
                : ClaimOwnershipState.HeldByOther,
            current.WorkerIdentity,
            current.ExpiresAt);
    }

    public async Task ReleaseAsync(
        TrackerConfig config,
        WorkItemId id,
        CancellationToken cancellationToken)
    {
        EnsureStore(config);
        await using var storeLock = await LocalStoreLock.AcquireAsync(Paths(config).Root, cancellationToken);
        var document = await RequiredUnlockedAsync(config, id, cancellationToken);
        var current = document.Claim;
        if (current is null || current.ExpiresAt <= clock.UtcNow)
        {
            throw new TrackerException("CLAIM_NOT_FOUND", $"Work item '{id}' does not have an active claim.", 5);
        }

        var worker = await identityProvider.GetIdentityAsync(cancellationToken);
        if (!string.Equals(current.WorkerIdentity, worker, StringComparison.Ordinal))
        {
            throw new TrackerException(
                "CLAIM_NOT_OWNER",
                $"Work item '{id}' is claimed by worker {current.WorkerIdentity}.",
                7,
                new Dictionary<string, object?>
                {
                    ["workerIdentity"] = current.WorkerIdentity,
                    ["expiresAt"] = current.ExpiresAt
                });
        }

        document.Claim = null;
        document.UpdatedAt = clock.UtcNow;
        await WriteUnlockedAsync(document, document.Path, cancellationToken);
    }

    public async Task<ArchiveWorkItemResult> ArchiveAsync(
        TrackerConfig config,
        WorkItemId id,
        CancellationToken cancellationToken)
    {
        EnsureStore(config);
        await using var storeLock = await LocalStoreLock.AcquireAsync(Paths(config).Root, cancellationToken);
        var document = await RequiredUnlockedAsync(config, id, cancellationToken);
        if (document.Archived)
        {
            return new ArchiveWorkItemResult(Detail(document), false, true);
        }

        await EnsureOwnedUnlockedAsync(document, cancellationToken);
        var originalPath = document.Path;
        document.Archived = true;
        document.Claim = null;
        document.UpdatedAt = clock.UtcNow;
        document.Path = CanonicalPath(config, document);
        await WriteUnlockedAsync(document, originalPath, cancellationToken);
        return new ArchiveWorkItemResult(Detail(document), true, true);
    }

    public async Task<ArchiveWorkItemResult> UnarchiveAsync(
        TrackerConfig config,
        WorkItemId id,
        CancellationToken cancellationToken)
    {
        EnsureStore(config);
        await using var storeLock = await LocalStoreLock.AcquireAsync(Paths(config).Root, cancellationToken);
        var document = await RequiredUnlockedAsync(config, id, cancellationToken);
        if (!document.Archived)
        {
            return new ArchiveWorkItemResult(Detail(document), false, false);
        }

        if (document.Claim is { } claim && claim.ExpiresAt > clock.UtcNow)
        {
            throw new TrackerException(
                "CLAIM_HELD",
                $"Archived work item '{id}' has an active claim by worker {claim.WorkerIdentity}.",
                6,
                new Dictionary<string, object?>
                {
                    ["workerIdentity"] = claim.WorkerIdentity,
                    ["expiresAt"] = claim.ExpiresAt
                });
        }

        var originalPath = document.Path;
        document.Claim = null;
        document.Archived = false;
        document.UpdatedAt = clock.UtcNow;
        document.Path = CanonicalPath(config, document);
        await WriteUnlockedAsync(document, originalPath, cancellationToken);
        return new ArchiveWorkItemResult(Detail(document), true, false);
    }

    private async Task EnsureOwnedUnlockedAsync(
        LocalMarkdownDocument document,
        CancellationToken cancellationToken)
    {
        var claim = document.Claim;
        var worker = await identityProvider.GetIdentityAsync(cancellationToken);
        if (claim is null || claim.ExpiresAt <= clock.UtcNow ||
            !string.Equals(claim.WorkerIdentity, worker, StringComparison.Ordinal))
        {
            throw new TrackerException(
                "CLAIM_REQUIRED",
                $"Work item 'local:{document.Id}' must be claimed by the current worker before it can be updated.",
                6);
        }
    }

    private async Task<LocalMarkdownDocument> RequiredUnlockedAsync(
        TrackerConfig config,
        WorkItemId id,
        CancellationToken cancellationToken) =>
        await FindUnlockedAsync(config, LocalMarkdownWorkItemAddressResolver.Decode(id), cancellationToken)
        ?? throw new TrackerException(
            "WORK_ITEM_NOT_FOUND",
            $"Work item '{id}' was not found in the configured tracker.",
            5,
            new Dictionary<string, object?> { ["id"] = id.Value });

    private async Task<LocalMarkdownDocument?> FindUnlockedAsync(
        TrackerConfig config,
        int id,
        CancellationToken cancellationToken) =>
        (await LoadAllUnlockedAsync(config, cancellationToken)).SingleOrDefault(item => item.Id == id);

    private async Task<IReadOnlyList<LocalMarkdownDocument>> LoadAllUnlockedAsync(
        TrackerConfig config,
        CancellationToken cancellationToken)
    {
        var paths = Paths(config);
        var documents = new List<LocalMarkdownDocument>();
        await LoadDirectoryAsync(paths.Items, archived: false);
        await LoadDirectoryAsync(paths.Archive, archived: true);
        var duplicate = documents.GroupBy(item => item.Id).FirstOrDefault(group => group.Count() > 1);
        if (duplicate is not null)
        {
            throw new TrackerException(
                "STORE_INVALID",
                $"The local tracker contains duplicate work-item ID {duplicate.Key}.",
                3,
                new Dictionary<string, object?>
                {
                    ["id"] = duplicate.Key,
                    ["paths"] = duplicate.Select(item => item.Path).ToArray()
                });
        }

        return documents;

        async Task LoadDirectoryAsync(string directory, bool archived)
        {
            if (!Directory.Exists(directory))
            {
                return;
            }

            foreach (var path in Directory.EnumerateFiles(directory, "*.md").Order())
            {
                var match = ItemFileName().Match(Path.GetFileName(path));
                if (!match.Success ||
                    !int.TryParse(match.Groups["number"].Value, NumberStyles.None,
                        CultureInfo.InvariantCulture, out var id) || id <= 0)
                {
                    throw new TrackerException(
                        "WORK_ITEM_DOCUMENT_INVALID",
                        $"Markdown file '{path}' does not use the required numeric-title filename format.",
                        5,
                        new Dictionary<string, object?> { ["path"] = path, ["reason"] = "filename" });
                }

                var content = await File.ReadAllTextAsync(path, cancellationToken);
                var document = LocalMarkdownDocumentCodec.Parse(id, path, archived, content);
                _ = CanonicalStatus(config, document.Status);
                _ = CanonicalPriority(config, document.Priority);
                documents.Add(document);
            }
        }
    }

    private static async Task WriteUnlockedAsync(
        LocalMarkdownDocument document,
        string? originalPath,
        CancellationToken cancellationToken)
    {
        var destination = document.Path;
        Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
        if (originalPath is not null &&
            !string.Equals(originalPath, destination, StringComparison.Ordinal) &&
            File.Exists(destination))
        {
            throw new TrackerException(
                "STORE_INVALID",
                $"Cannot move work item because '{destination}' already exists.",
                3);
        }

        var temporary = Path.Combine(
            Path.GetDirectoryName(destination)!,
            $".{Path.GetFileName(destination)}.{Guid.NewGuid():N}.tmp");
        try
        {
            await File.WriteAllTextAsync(
                temporary,
                LocalMarkdownDocumentCodec.Serialize(document),
                cancellationToken);
            File.Move(temporary, destination, overwrite: originalPath == destination);
            if (originalPath is not null &&
                !string.Equals(originalPath, destination, StringComparison.Ordinal))
            {
                File.Delete(originalPath);
            }
        }
        finally
        {
            if (File.Exists(temporary))
            {
                File.Delete(temporary);
            }
        }
    }

    private static WorkItemSummary Summary(LocalMarkdownDocument document) => new(
        LocalMarkdownWorkItemAddressResolver.FromNumber(document.Id),
        document.Title,
        null,
        document.Status,
        document.Priority,
        document.Archived);

    private static WorkItemDetail Detail(LocalMarkdownDocument document) => new(
        LocalMarkdownWorkItemAddressResolver.FromNumber(document.Id),
        document.Title,
        document.Body,
        null,
        document.Status,
        document.Priority,
        document.Archived);

    private static ClaimResult ClaimResult(LocalClaimMetadata claim, ClaimOutcome outcome) => new(
        outcome,
        claim.WorkerIdentity,
        claim.ExpiresAt,
        claim.ClaimAttemptId,
        claim.AgentType,
        claim.SessionId);

    private static string CanonicalPath(TrackerConfig config, LocalMarkdownDocument document)
    {
        var paths = Paths(config);
        return Path.Combine(
            document.Archived ? paths.Archive : paths.Items,
            PortableFilenameSlugger.FileName(document.Id, document.Title));
    }

    private static string CanonicalStatus(TrackerConfig config, string status) =>
        config.LocalMarkdown!.Statuses.SingleOrDefault(item =>
            string.Equals(item, status, StringComparison.OrdinalIgnoreCase))
        ?? throw new TrackerException(
            "ARGUMENT_INVALID",
            $"Unknown local tracker status '{status}'.",
            2);

    private static string? CanonicalPriority(TrackerConfig config, string? priority) =>
        priority is null
            ? null
            : config.LocalMarkdown!.Priorities.SingleOrDefault(item =>
                string.Equals(item, priority, StringComparison.OrdinalIgnoreCase))
              ?? throw new TrackerException(
                  "ARGUMENT_INVALID",
                  $"Unknown local tracker priority '{priority}'.",
                  2);

    private static int PriorityRank(TrackerConfig config, string? priority)
    {
        if (priority is null)
        {
            return int.MaxValue;
        }

        for (var index = 0; index < config.LocalMarkdown!.Priorities.Count; index++)
        {
            if (string.Equals(config.LocalMarkdown.Priorities[index], priority,
                    StringComparison.OrdinalIgnoreCase))
            {
                return index;
            }
        }

        return int.MaxValue;
    }

    private static void ValidateCreate(CreateWorkItemRequest request)
    {
        if (string.IsNullOrWhiteSpace(request.Title) || request.Title.Length > 256 ||
            request.Title.Contains('\r') || request.Title.Contains('\n'))
        {
            throw new TrackerException(
                "ARGUMENT_INVALID",
                "title must be a non-empty single line of at most 256 characters.",
                2);
        }
    }

    private static LocalStorePaths Paths(TrackerConfig config)
    {
        var local = config.LocalMarkdown
            ?? throw new TrackerException("CONFIG_INVALID", "localMarkdown configuration is missing.", 3);
        var configDirectory = config.SourcePath is null
            ? Environment.CurrentDirectory
            : Path.GetDirectoryName(Path.GetFullPath(config.SourcePath))!;
        var root = Path.GetFullPath(local.Path, configDirectory);
        return new LocalStorePaths(
            root,
            Path.Combine(root, "items"),
            Path.Combine(root, "archive"));
    }

    private static void EnsureStore(TrackerConfig config)
    {
        var paths = Paths(config);
        if (!Directory.Exists(paths.Items) || !Directory.Exists(paths.Archive))
        {
            throw new TrackerException(
                "STORE_NOT_FOUND",
                $"The local Wrighty store '{paths.Root}' is not initialized. Run 'wrighty init'.",
                3,
                new Dictionary<string, object?> { ["path"] = paths.Root });
        }
    }

    private static TrackerException Archived(WorkItemId id) => new(
        "WORK_ITEM_ARCHIVED",
        $"Work item '{id}' is archived. Unarchive it before modifying or claiming it.",
        7,
        new Dictionary<string, object?> { ["id"] = id.Value });

    private sealed record LocalStorePaths(string Root, string Items, string Archive);

    [GeneratedRegex(@"^(?<number>[0-9]+)-[a-z0-9]+(?:-[a-z0-9]+)*\.md$",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex ItemFileName();
}
