using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using Highbyte.Wrighty.Addressing;
using Highbyte.Wrighty.AgentContext;
using Highbyte.Wrighty.Backends;
using Highbyte.Wrighty.Claims;
using Highbyte.Wrighty.Configuration;
using Highbyte.Wrighty.Errors;
using Highbyte.Wrighty.Identity;
using Highbyte.Wrighty.Importing;
using Highbyte.Wrighty.Models;
using Highbyte.Wrighty.Time;

namespace Highbyte.Wrighty.LocalMarkdown;

public sealed partial class LocalMarkdownTrackerBackend(
    IWorkerIdentityProvider identityProvider,
    IClock clock,
    Func<string, CancellationToken, Task>? afterMutationLockAcquired = null) : ITrackerBackend, ITrackerDashboardBackend, ILocalMarkdownImportBackend
{
    private const string GitIgnoreComment = "# Wrighty runtime state";
    private static readonly string[] GitIgnoreRules =
        ["/.lock", ".*.tmp", $"/{LocalRuntimeStateStore.FileName}"];
    private static readonly UTF8Encoding StrictUtf8 = new(false, true);
    private readonly LocalMarkdownWorkItemAddressResolver resolver = new();

    private sealed record PlannedImport(
        LocalMarkdownImportItem Item,
        LocalMarkdownDocument Document);

    public string Name => "local-markdown";

    public IWorkItemAddressResolver AddressResolver => resolver;

    public async Task<BackendInitializationResult> InitializeAsync(
        TrackerConfig config,
        bool checkOnly,
        CancellationToken cancellationToken)
    {
        var paths = Paths(config);
        var actions = GetRequiredInitializationActions(paths);

        if (checkOnly)
        {
            return await ValidateInitializationAsync(
                config, paths, actions, cancellationToken);
        }

        await InitializeStoreAsync(config, paths, actions, cancellationToken);
        return new BackendInitializationResult(actions.Count > 0, actions);
    }

    private static List<string> GetRequiredInitializationActions(LocalStorePaths paths)
    {
        var actions = new List<string>();
        AddMissingDirectoryAction(paths.Root, "create local Wrighty directory", actions);
        AddMissingDirectoryAction(paths.Items, "create items directory", actions);
        AddMissingDirectoryAction(paths.Archive, "create archive directory", actions);
        if (!File.Exists(Path.Combine(paths.Root, ".lock")))
        {
            actions.Add("create store lock");
        }

        return actions;
    }

    private static void AddMissingDirectoryAction(
        string path,
        string action,
        ICollection<string> actions)
    {
        if (!Directory.Exists(path))
        {
            actions.Add(action);
        }
    }

    private async Task<BackendInitializationResult> ValidateInitializationAsync(
        TrackerConfig config,
        LocalStorePaths paths,
        List<string> actions,
        CancellationToken cancellationToken)
    {
        ThrowIfInitializationRequired(paths.Root, actions);
        await using var storeLock = await LocalStoreLock.AcquireAsync(paths.Root, cancellationToken);
        var documents = await LoadAllUnlockedAsync(config, cancellationToken, allowLegacyClaimMetadata: true);
        AddRenameActions(config, documents, actions);
        AddMigrationActions(documents, actions);
        ThrowIfInitializationRequired(paths.Root, actions);
        return new BackendInitializationResult(false, ["Local Markdown store is valid."]);
    }

    private static void ThrowIfInitializationRequired(string root, IReadOnlyCollection<string> actions)
    {
        if (actions.Count == 0)
        {
            return;
        }

        throw new TrackerException(
            "STORE_INITIALIZATION_REQUIRED",
            $"Local Wrighty initialization is required: {string.Join("; ", actions)}. Run 'wrighty init'.",
            5,
            new Dictionary<string, object?> { ["path"] = root, ["actions"] = actions });
    }

    private void AddRenameActions(
        TrackerConfig config,
        IEnumerable<LocalMarkdownDocument> documents,
        ICollection<string> actions)
    {
        foreach (var document in documents)
        {
            var canonical = CanonicalPath(config, document);
            if (!string.Equals(document.Path, canonical, StringComparison.Ordinal))
            {
                actions.Add($"rename {Path.GetFileName(document.Path)} to {Path.GetFileName(canonical)}");
            }
        }
    }

    private async Task InitializeStoreAsync(
        TrackerConfig config,
        LocalStorePaths paths,
        ICollection<string> actions,
        CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(paths.Items);
        Directory.CreateDirectory(paths.Archive);
        await using (var storeLock = await LocalStoreLock.AcquireAsync(paths.Root, cancellationToken))
        {
            await MigrateLegacyClaimMetadataAsync(config, paths, actions, cancellationToken);
            await RenameDocumentsAsync(config, actions, cancellationToken);
            await AddGitIgnoreActionAsync(paths.Root, actions, cancellationToken);
        }
    }

    private static void AddMigrationActions(
        IEnumerable<LocalMarkdownDocument> documents,
        ICollection<string> actions)
    {
        var count = documents.Count(document => document.HasLegacyClaimMetadata);
        if (count > 0)
        {
            actions.Add(
                $"migrate legacy claim frontmatter from {count} document(s) to {LocalRuntimeStateStore.FileName}");
        }
    }

    private async Task MigrateLegacyClaimMetadataAsync(
        TrackerConfig config,
        LocalStorePaths paths,
        ICollection<string> actions,
        CancellationToken cancellationToken)
    {
        var documents = await LoadAllUnlockedAsync(config, cancellationToken, allowLegacyClaimMetadata: true);
        var legacy = documents.Where(document => document.HasLegacyClaimMetadata).ToArray();
        if (legacy.Length == 0)
        {
            return;
        }

        var state = await LocalRuntimeStateStore.LoadUnlockedAsync(paths.Root, cancellationToken);
        var now = clock.UtcNow;
        foreach (var document in legacy)
        {
            if (document.LegacyClaim is { } claim)
            {
                MigrateLegacyClaim(state, document, claim, now);
            }

            document.RemoveLegacyClaimMetadata();
            await WriteUnlockedAsync(document, document.Path, cancellationToken);
        }

        await LocalRuntimeStateStore.SaveUnlockedAsync(paths.Root, state, cancellationToken);
        actions.Add(
            $"migrated legacy claim frontmatter from {legacy.Length} document(s) to {LocalRuntimeStateStore.FileName}");
    }

    private static void MigrateLegacyClaim(
        LocalRuntimeState state,
        LocalMarkdownDocument document,
        LegacyLocalClaim claim,
        DateTimeOffset now)
    {
        var active = claim.State == "active" && claim.ExpiresAt > now;
        if (claim.Version != 2)
        {
            if (active)
            {
                throw new TrackerException("CLAIM_FORMAT_UNSUPPORTED",
                    $"Work item 'local:{document.Id}' has an active pre-v2 claim. Release active claims with " +
                    "the previous Wrighty version before upgrading, and do not mix Wrighty versions.", 6);
            }

            return;
        }

        var record = new LocalClaimRecord(
            claim.WorkerIdentity,
            claim.ClaimantId,
            claim.ClaimToken,
            claim.AgentType,
            claim.SessionId,
            claim.ClaimedAt,
            claim.ExpiresAt,
            claim.ClaimantKind,
            claim.WorkspacePath);
        if (active)
        {
            state.Claims[document.Id] = record;
        }
        else
        {
            state.PreserveSession(document.Id, record, now);
        }
    }

    private async Task RenameDocumentsAsync(
        TrackerConfig config,
        ICollection<string> actions,
        CancellationToken cancellationToken)
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
    }

    private static async Task AddGitIgnoreActionAsync(
        string root,
        ICollection<string> actions,
        CancellationToken cancellationToken)
    {
        if (!IsInsideGitWorktree(root))
        {
            return;
        }

        var action = await EnsureGitIgnoreAsync(root, cancellationToken);
        if (action is not null)
        {
            actions.Add(action);
        }
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
        return FilterAndSort(config, documents, request).Select(Summary).ToArray();
    }

    private static IEnumerable<LocalMarkdownDocument> FilterAndSort(
        TrackerConfig config,
        IReadOnlyList<LocalMarkdownDocument> documents,
        ListWorkItemsRequest request)
    {
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

        if (request.Fields is { Count: > 0 })
        {
            foreach (var field in request.Fields)
            {
                LocalMarkdownReservedFields.ValidateCustomFieldName(field.Key);
                query = query.Where(item => string.Equals(
                    item.CustomFieldScalar(field.Key), field.Value, StringComparison.Ordinal));
            }
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

        return query;
    }

    public async Task<WorkItemOperationalSnapshot?> GetOperationalAsync(
        TrackerConfig config,
        WorkItemId id,
        CancellationToken cancellationToken)
    {
        EnsureStore(config);
        var paths = Paths(config);
        await using var storeLock = await LocalStoreLock.AcquireAsync(paths.Root, cancellationToken);
        var document = await FindUnlockedAsync(
            config, LocalMarkdownWorkItemAddressResolver.Decode(id), cancellationToken);
        if (document is null)
        {
            return null;
        }

        var state = await LocalRuntimeStateStore.LoadUnlockedAsync(paths.Root, cancellationToken);
        var worker = await identityProvider.GetIdentityAsync(cancellationToken);
        return Snapshot(document, state, worker, clock.UtcNow);
    }

    public async Task<IReadOnlyList<WorkItemOperationalSnapshot>> ListOperationalAsync(
        TrackerConfig config,
        ListWorkItemsRequest request,
        CancellationToken cancellationToken)
    {
        EnsureStore(config);
        var paths = Paths(config);
        await using var storeLock = await LocalStoreLock.AcquireAsync(paths.Root, cancellationToken);
        var documents = await LoadAllUnlockedAsync(config, cancellationToken);
        var state = await LocalRuntimeStateStore.LoadUnlockedAsync(paths.Root, cancellationToken);
        var worker = await identityProvider.GetIdentityAsync(cancellationToken);
        var now = clock.UtcNow;
        return FilterAndSort(config, documents, request)
            .Select(document => Snapshot(document, state, worker, now))
            .ToArray();
    }

    private static WorkItemOperationalSnapshot Snapshot(
        LocalMarkdownDocument document,
        LocalRuntimeState state,
        string worker,
        DateTimeOffset now) => new(
        Detail(document),
        ClaimSummary(state, document.Id, worker, now),
        SessionRecord(state, document.Id, worker));

    private static AgentSessionRecord? SessionRecord(
        LocalRuntimeState state,
        int documentId,
        string worker)
    {
        var claim = state.Claim(documentId);
        var record = state.Session(documentId);
        if (claim is not null && (claim.HasAddress || record is null))
        {
            // The run outcome is recorded on the session sidecar, not the live claim; attach it
            // only when it belongs to the same recorded session as the active claim.
            var sameSession = record is not null &&
                string.Equals(record.SessionId, claim.SessionId, StringComparison.Ordinal);
            return new AgentSessionRecord(
                claim.AgentType,
                claim.SessionId,
                claim.WorkspacePath,
                claim.ExpiresAt,
                string.Equals(claim.WorkerIdentity, worker, StringComparison.Ordinal),
                claim.Branch ?? record?.Branch,
                sameSession ? record!.Outcome : null,
                sameSession ? record!.FinalMessage : null,
                sameSession ? record!.EndedAt : null);
        }

        if (record is null)
        {
            return null;
        }

        return new AgentSessionRecord(
            record.AgentType,
            record.SessionId,
            record.WorkspacePath,
            record.LastClaimExpiresAt ?? record.UpdatedAt,
            string.Equals(record.WorkerIdentity, worker, StringComparison.Ordinal),
            record.Branch,
            record.Outcome,
            record.FinalMessage,
            record.EndedAt);
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

    public async Task<LocalMarkdownImportResult> ImportAsync(
        TrackerConfig config,
        LocalMarkdownImportRequest request,
        CancellationToken cancellationToken)
    {
        EnsureStore(config);
        ValidateImportRequest(request);
        var paths = Paths(config);
        var sources = ResolveAndValidateImportSources(paths, request);
        await using var storeLock = await LocalStoreLock.AcquireAsync(paths.Root, cancellationToken);
        var existing = await LoadAllUnlockedAsync(
            config,
            cancellationToken,
            excludedPaths: request.InPlace ? sources : null);
        var nextId = existing.Count == 0 ? 1 : checked(existing.Max(item => item.Id) + 1);
        var planned = await PlanImportsAsync(
            config, request, paths, sources, nextId, cancellationToken);
        var items = planned.Select(value => value.Item).ToArray();
        if (request.DryRun)
        {
            return new LocalMarkdownImportResult(true, request.Move || request.InPlace, items);
        }

        await CommitImportsAsync(paths, request, planned, cancellationToken);
        return new LocalMarkdownImportResult(false, request.Move || request.InPlace, items);
    }

    private static void ValidateImportRequest(LocalMarkdownImportRequest request)
    {
        if (request.Paths.Count == 0)
        {
            throw new TrackerException("ARGUMENT_INVALID", "At least one import path is required.", 2);
        }

        if (request.InPlace && request.Move)
        {
            throw new TrackerException(
                "ARGUMENT_INVALID",
                "--in-place already uses verified move semantics and cannot be combined with --move.",
                2);
        }

        var invalidMapping = request.FieldMappings.FirstOrDefault(mapping =>
            mapping.Key is not ("status" or "priority") ||
            string.IsNullOrWhiteSpace(mapping.Value));
        if (invalidMapping.Key is not null)
        {
            throw new TrackerException(
                "ARGUMENT_INVALID",
                $"Invalid --map '{invalidMapping.Key}={invalidMapping.Value}'; only status=<source-key> and priority=<source-key> are supported.",
                2);
        }
    }

    private static string[] ResolveAndValidateImportSources(
        LocalStorePaths paths,
        LocalMarkdownImportRequest request)
    {
        var sources = MarkdownImportPlanner.DiscoverPaths(
                request.Paths,
                request.Recursive)
            .Select(Path.GetFullPath)
            .Distinct(StringComparer.Ordinal)
            .Order(StringComparer.Ordinal)
            .ToArray();
        if (sources.Length == 0)
        {
            throw new TrackerException("ARGUMENT_INVALID", "No Markdown files were found to import.", 2);
        }

        var root = Path.GetFullPath(paths.Root) + Path.DirectorySeparatorChar;
        if (!request.InPlace && sources.Any(source => source.StartsWith(root, StringComparison.Ordinal)))
        {
            throw new TrackerException(
                "IMPORT_SOURCE_OUTSIDE_ALLOWED_SCOPE",
                "Import sources must be outside the Local Markdown store unless --in-place is explicit.",
                2);
        }

        if (request.InPlace)
        {
            ValidateInPlaceSources(paths, sources);
        }

        return sources;
    }

    private static void ValidateInPlaceSources(
        LocalStorePaths paths,
        IEnumerable<string> sources)
    {
        var items = Path.GetFullPath(paths.Items) + Path.DirectorySeparatorChar;
        var archive = Path.GetFullPath(paths.Archive) + Path.DirectorySeparatorChar;
        foreach (var source in sources)
        {
            ValidateInPlaceScope(source, items, archive);
            RejectManagedInPlaceSource(source, archive);
        }
    }

    private static void ValidateInPlaceScope(
        string source,
        string items,
        string archive)
    {
        if (!source.StartsWith(items, StringComparison.Ordinal) &&
            !source.StartsWith(archive, StringComparison.Ordinal))
        {
            throw new TrackerException(
                "IMPORT_SOURCE_OUTSIDE_ALLOWED_SCOPE",
                $"In-place import source '{source}' must be below the configured items or archive directory.",
                2,
                new Dictionary<string, object?> { ["path"] = source });
        }
    }

    private static void RejectManagedInPlaceSource(string source, string archive)
    {
        var match = ItemFileName().Match(Path.GetFileName(source));
        if (!match.Success ||
            !int.TryParse(
                match.Groups["number"].Value,
                NumberStyles.None,
                CultureInfo.InvariantCulture,
                out var id) ||
            id <= 0)
        {
            return;
        }

        try
        {
            var bytes = File.ReadAllBytes(source);
            _ = LocalMarkdownDocumentCodec.Parse(
                id,
                source,
                source.StartsWith(archive, StringComparison.Ordinal),
                StrictUtf8.GetString(bytes),
                Revision(bytes));
            throw new TrackerException(
                "IMPORT_SOURCE_ALREADY_TRACKED",
                $"In-place import source '{source}' is already a valid Wrighty work item.",
                2,
                new Dictionary<string, object?>
                {
                    ["path"] = source,
                    ["id"] = $"local:{id}"
                });
        }
        catch (TrackerException exception)
            when (exception.Code == "WORK_ITEM_DOCUMENT_INVALID")
        {
            // A numeric filename alone does not make an invalid document managed.
        }
    }

    private async Task<List<PlannedImport>> PlanImportsAsync(
        TrackerConfig config,
        LocalMarkdownImportRequest request,
        LocalStorePaths paths,
        IEnumerable<string> sources,
        int nextId,
        CancellationToken cancellationToken)
    {
        var planned = new List<PlannedImport>();
        foreach (var sourcePath in sources)
        {
            planned.Add(await PlanImportAsync(
                config, request, paths, sourcePath, nextId, cancellationToken));
            nextId = checked(nextId + 1);
        }

        return planned;
    }

    private async Task<PlannedImport> PlanImportAsync(
        TrackerConfig config,
        LocalMarkdownImportRequest request,
        LocalStorePaths paths,
        string sourcePath,
        int id,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var content = StrictUtf8.GetString(await File.ReadAllBytesAsync(sourcePath, cancellationToken));
        var source = LocalMarkdownDocumentCodec.ParseImportSource(sourcePath, content);
        var title = SourceScalar(source.Metadata, "title") ?? FirstHeading(source.Body) ??
                    Path.GetFileNameWithoutExtension(sourcePath);
        if (string.IsNullOrWhiteSpace(title))
        {
            throw ImportInvalid(sourcePath, "Could not resolve a title from frontmatter, H1, or filename.");
        }

        var statusKey = request.FieldMappings.GetValueOrDefault("status") ?? "status";
        var priorityKey = request.FieldMappings.GetValueOrDefault("priority") ?? "priority";
        var status = ResolveImportStatus(config, request, source, sourcePath, statusKey);
        var priority = ResolveImportPriority(config, source, sourcePath, priorityKey);
        var archived = request.InPlace
            ? sourcePath.StartsWith(
                Path.GetFullPath(paths.Archive) + Path.DirectorySeparatorChar,
                StringComparison.Ordinal)
            : request.Archive;
        if (request.InPlace && request.Archive && !archived)
        {
            throw new TrackerException(
                "ARGUMENT_INVALID",
                $"--archive conflicts with the lifecycle directory of in-place source '{sourcePath}'.",
                2);
        }
        var destination = Path.Combine(
            archived ? paths.Archive : paths.Items,
            PortableFilenameSlugger.FileName(id, title));
        var createRequest = new CreateWorkItemRequest(title, source.Body, status, priority);
        var document = LocalMarkdownDocumentCodec.Create(
            id,
            destination,
            archived,
            title,
            source.Body,
            status,
            priority,
            new LocalCreationMetadata(
                1,
                Guid.NewGuid().ToString("D"),
                CreationAttempt.ComputeIntentHash(createRequest, request.Archive)),
            ResolveImportCreatedAt(source));
        document.UpdatedAt = clock.UtcNow;
        CopyImportedCustomFields(sourcePath, source, document);

        return new PlannedImport(
            new LocalMarkdownImportItem(sourcePath, id, destination, title, status, priority),
            document);
    }

    private static string ResolveImportStatus(
        TrackerConfig config,
        LocalMarkdownImportRequest request,
        LocalMarkdownImportSource source,
        string sourcePath,
        string statusKey)
    {
        var sourceStatus = request.ForceStatus ?? SourceScalar(source.Metadata, statusKey) ?? config.DefaultPickFrom;
        try
        {
            return CanonicalStatus(config, sourceStatus);
        }
        catch (TrackerException exception) when (exception.Code == "ARGUMENT_INVALID")
        {
            var field = request.ForceStatus is null ? statusKey : "--force-status";
            throw ImportInvalid(sourcePath, $"Frontmatter field '{field}' has unsupported value '{sourceStatus}'.");
        }
    }

    private static string? ResolveImportPriority(
        TrackerConfig config,
        LocalMarkdownImportSource source,
        string sourcePath,
        string priorityKey)
    {
        var sourcePriority = SourceScalar(source.Metadata, priorityKey);
        try
        {
            return CanonicalPriority(config, sourcePriority);
        }
        catch (TrackerException exception) when (exception.Code == "ARGUMENT_INVALID")
        {
            throw ImportInvalid(
                sourcePath,
                $"Frontmatter field '{priorityKey}' has unsupported value '{sourcePriority}'.");
        }
    }

    private DateTimeOffset ResolveImportCreatedAt(LocalMarkdownImportSource source)
    {
        var createdAtText = SourceScalar(source.Metadata, "createdAt") ?? SourceScalar(source.Metadata, "date");
        return DateTimeOffset.TryParse(
            createdAtText,
            CultureInfo.InvariantCulture,
            DateTimeStyles.AssumeUniversal,
            out var parsedDate)
            ? parsedDate.ToUniversalTime()
            : clock.UtcNow;
    }

    private static void CopyImportedCustomFields(
        string sourcePath,
        LocalMarkdownImportSource source,
        LocalMarkdownDocument document)
    {
        foreach (var pair in source.Metadata.Children)
        {
            var name = (pair.Key as YamlDotNet.RepresentationModel.YamlScalarNode)?.Value
                       ?? throw ImportInvalid(sourcePath, "Frontmatter keys must be scalar.");
            if (!LocalMarkdownReservedFields.IsReserved(name))
            {
                document.SetCustomFieldNode(name, pair.Value);
                continue;
            }

            if (!LocalMarkdownReservedFields.ManagedKeys.Contains(name, StringComparer.Ordinal))
            {
                throw ImportInvalid(sourcePath, $"Frontmatter field '{name}' is reserved for Wrighty.");
            }
        }
    }

    private static async Task CommitImportsAsync(
        LocalStorePaths paths,
        LocalMarkdownImportRequest request,
        IReadOnlyList<PlannedImport> planned,
        CancellationToken cancellationToken)
    {
        var staging = Path.Combine(paths.Root, $".import-{Guid.NewGuid():N}.tmp");
        var committed = new List<string>();
        var backups = new Dictionary<string, string>(StringComparer.Ordinal);
        Directory.CreateDirectory(staging);
        try
        {
            await StageImportsAsync(staging, request.Archive, planned, cancellationToken);
            if (request.InPlace)
            {
                BackupImportSources(staging, planned, backups);
                DeleteImportSources(planned);
            }
            PublishImports(staging, planned, committed);
            if (request.Move) DeleteImportSources(planned);
        }
        catch
        {
            RollbackPublishedImports(committed);
            RestoreImportSources(backups);
            throw;
        }
        finally
        {
            if (Directory.Exists(staging)) Directory.Delete(staging, recursive: true);
        }
    }

    private static async Task StageImportsAsync(
        string staging,
        bool archive,
        IEnumerable<PlannedImport> planned,
        CancellationToken cancellationToken)
    {
        foreach (var value in planned)
        {
            var staged = Path.Combine(staging, Path.GetFileName(value.Item.DestinationPath));
            await File.WriteAllTextAsync(
                staged,
                LocalMarkdownDocumentCodec.Serialize(value.Document),
                StrictUtf8,
                cancellationToken);
            _ = LocalMarkdownDocumentCodec.Parse(
                value.Item.Id,
                staged,
                archive,
                await File.ReadAllTextAsync(staged, cancellationToken),
                string.Empty);
        }
    }

    private static void PublishImports(
        string staging,
        IEnumerable<PlannedImport> planned,
        List<string> committed)
    {
        foreach (var item in planned.Select(value => value.Item))
        {
            Directory.CreateDirectory(Path.GetDirectoryName(item.DestinationPath)!);
            var staged = Path.Combine(staging, Path.GetFileName(item.DestinationPath));
            File.Move(staged, item.DestinationPath, overwrite: false);
            committed.Add(item.DestinationPath);
        }
    }

    private static void DeleteImportSources(IEnumerable<PlannedImport> planned)
    {
        foreach (var source in planned.Select(value => value.Item.SourcePath))
        {
            File.Delete(source);
        }
    }

    private static void BackupImportSources(
        string staging,
        IEnumerable<PlannedImport> planned,
        IDictionary<string, string> backups)
    {
        var index = 0;
        foreach (var source in planned.Select(value => value.Item.SourcePath))
        {
            var backup = Path.Combine(staging, $".source-{index++}.bak");
            File.Copy(source, backup, overwrite: false);
            backups.Add(source, backup);
        }
    }

    private static void RestoreImportSources(
        IReadOnlyDictionary<string, string> backups)
    {
        foreach (var pair in backups)
        {
            if (!File.Exists(pair.Key) && File.Exists(pair.Value))
            {
                Directory.CreateDirectory(Path.GetDirectoryName(pair.Key)!);
                File.Copy(pair.Value, pair.Key, overwrite: false);
            }
        }
    }

    private static void RollbackPublishedImports(IEnumerable<string> committed)
    {
        foreach (var destination in committed.Where(File.Exists))
        {
            File.Delete(destination);
        }
    }

    private static List<string> ResolveImportPaths(
        IReadOnlyList<string> inputs,
        bool recursive)
    {
        var files = new List<string>();
        foreach (var input in inputs)
        {
            if (File.Exists(input))
            {
                if (!string.Equals(Path.GetExtension(input), ".md", StringComparison.OrdinalIgnoreCase))
                {
                    throw new TrackerException("ARGUMENT_INVALID", $"Import file '{input}' is not Markdown.", 2);
                }

                files.Add(input);
            }
            else if (Directory.Exists(input))
            {
                files.AddRange(Directory.EnumerateFiles(
                        input,
                        "*",
                        recursive ? SearchOption.AllDirectories : SearchOption.TopDirectoryOnly)
                    .Where(path => string.Equals(
                        Path.GetExtension(path), ".md", StringComparison.OrdinalIgnoreCase)));
            }
            else
            {
                throw new TrackerException("ARGUMENT_INVALID", $"Import path '{input}' does not exist.", 2);
            }
        }

        return files;
    }

    private static string? SourceScalar(
        YamlDotNet.RepresentationModel.YamlMappingNode metadata,
        string key)
    {
        foreach (var pair in metadata.Children)
        {
            if (pair.Key is not YamlDotNet.RepresentationModel.YamlScalarNode { Value: { } name } ||
                !string.Equals(name, key, StringComparison.Ordinal)) continue;
            return pair.Value is YamlDotNet.RepresentationModel.YamlScalarNode scalar
                ? scalar.Value
                : throw new TrackerException(
                    "RESERVED_FIELD_COLLISION",
                    $"Imported frontmatter field '{key}' collides with a Wrighty-managed field and must be scalar.",
                    2,
                    new Dictionary<string, object?> { ["field"] = key });
        }

        return null;
    }

    private static string? FirstHeading(string body)
    {
        foreach (var line in body.Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n'))
        {
            if (line.StartsWith("# ", StringComparison.Ordinal) && line[2..].Trim() is { Length: > 0 } title)
            {
                return title;
            }
        }

        return null;
    }

    private static TrackerException ImportInvalid(string path, string message) => new(
        "WORK_ITEM_DOCUMENT_INVALID",
        $"Cannot import '{path}': {message}",
        5,
        new Dictionary<string, object?> { ["path"] = path });

    public async Task<DashboardSnapshot> GetDashboardAsync(
        TrackerConfig config,
        ArchiveScope archiveScope,
        CancellationToken cancellationToken)
    {
        EnsureStore(config);
        var paths = Paths(config);
        await using var storeLock = await LocalStoreLock.AcquireAsync(paths.Root, cancellationToken);
        var documents = await LoadAllUnlockedAsync(config, cancellationToken);
        var state = await LocalRuntimeStateStore.LoadUnlockedAsync(paths.Root, cancellationToken);
        var worker = await identityProvider.GetIdentityAsync(cancellationToken);
        var now = clock.UtcNow;
        var items = documents
            .Where(document => archiveScope switch
            {
                ArchiveScope.Active => !document.Archived,
                ArchiveScope.Archived => document.Archived,
                _ => true
            })
            .OrderBy(document => StatusRank(config, document.Status))
            .ThenBy(document => PriorityRank(config, document.Priority))
            .ThenBy(document => document.Id)
            .Select(document => new DashboardWorkItem(
                Summary(document),
                ClaimSummary(state, document.Id, worker, now),
                SessionRecord(state, document.Id, worker)?.HasRecordedWorktree ?? false))
            .ToArray();
        var revisionInput = string.Join('\n', [
            archiveScope.ToString(),
            .. config.LocalMarkdown!.Statuses,
            "--priorities--",
            .. config.LocalMarkdown.Priorities,
            "--documents--",
            .. documents
                .OrderBy(document => document.Id)
                .Select(document => $"{document.Id}:{document.Archived}:{document.Revision}"),
            "--visible-claims--",
            .. items.Select(item => $"{item.Item.Id.Value}:{item.Claim.State}:{item.Claim.ExpiresAt:O}")
        ]);
        return new DashboardSnapshot(
            config.LocalMarkdown.Statuses,
            config.LocalMarkdown.Priorities,
            items,
            Revision(StrictUtf8.GetBytes(revisionInput)));
    }

    public async Task<EditableWorkItem> GetEditableAsync(
        TrackerConfig config,
        WorkItemId id,
        CancellationToken cancellationToken)
    {
        EnsureStore(config);
        var paths = Paths(config);
        await using var storeLock = await LocalStoreLock.AcquireAsync(paths.Root, cancellationToken);
        var document = await RequiredUnlockedAsync(config, id, cancellationToken);
        var state = await LocalRuntimeStateStore.LoadUnlockedAsync(paths.Root, cancellationToken);
        var worker = await identityProvider.GetIdentityAsync(cancellationToken);
        return new EditableWorkItem(
            Detail(document),
            document.Revision,
            ClaimSummary(state, document.Id, worker, clock.UtcNow));
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
        foreach (var field in operation.Request.Fields ?? new Dictionary<string, string?>())
        {
            LocalMarkdownReservedFields.ValidateCustomFieldName(field.Key);
            document.SetCustomField(field.Key, field.Value);
        }
        document.AutomationEligible = operation.Request.AutomationEligible;
        document.PreferredAgent = operation.Request.PreferredAgent;
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
        await PauseAfterLockAsync("update", cancellationToken);
        var document = await RequiredUnlockedAsync(config, id, cancellationToken);
        if (document.Archived)
        {
            throw Archived(id);
        }

        var state = await LocalRuntimeStateStore.LoadUnlockedAsync(paths.Root, cancellationToken);
        await EnsureOwnedUnlockedAsync(state, document.Id, operation.ClaimHandle, cancellationToken);
        ValidateExpectedRevision(id, document, operation.ExpectedRevision);
        var changed = new List<string>();
        ApplyPatch(config, document, operation, changed);
        if (changed.Count == 0)
        {
            return new UpdateWorkItemResult(Detail(document), false, []);
        }

        if (changed.Contains("archived", StringComparer.Ordinal))
        {
            state.PreserveSession(document.Id, state.Claim(document.Id), clock.UtcNow);
            state.Claims.Remove(document.Id);
            await LocalRuntimeStateStore.SaveUnlockedAsync(paths.Root, state, cancellationToken);
        }

        var originalPath = document.Path;
        document.UpdatedAt = clock.UtcNow;
        document.Path = CanonicalPath(config, document);
        await WriteUnlockedAsync(document, originalPath, cancellationToken);
        return new UpdateWorkItemResult(Detail(document), true, changed);
    }

    private static void ValidateExpectedRevision(
        WorkItemId id,
        LocalMarkdownDocument document,
        string? expectedRevision)
    {
        if (expectedRevision is null || CryptographicOperations.FixedTimeEquals(
                Encoding.ASCII.GetBytes(document.Revision),
                Encoding.ASCII.GetBytes(expectedRevision)))
        {
            return;
        }

        throw new TrackerException(
            "UPDATE_CONFLICT",
            $"Work item '{id}' changed after it was loaded.",
            9,
            new Dictionary<string, object?>
            {
                ["id"] = id.Value,
                ["currentRevision"] = document.Revision
            });
    }

    private static void ApplyPatch(
        TrackerConfig config,
        LocalMarkdownDocument document,
        UpdateWorkItemOperation operation,
        ICollection<string> changed)
    {
        var patch = operation.Patch;
        ApplyTitle(document, patch.Title, changed);
        ApplyBody(document, patch.Body, changed);
        ApplyPriority(config, document, patch.Priority, changed);
        ApplyStatus(config, document, patch.Status, changed);
        ApplyCustomFields(document, patch.Fields, changed);
        if (patch.AutomationEligible.IsSpecified &&
            document.AutomationEligible != patch.AutomationEligible.Value)
        {
            document.AutomationEligible = patch.AutomationEligible.Value;
            changed.Add("wrighty-auto");
        }
        if (patch.PreferredAgent.IsSpecified &&
            !string.Equals(document.PreferredAgent, patch.PreferredAgent.Value,
                StringComparison.OrdinalIgnoreCase))
        {
            document.PreferredAgent = patch.PreferredAgent.Value;
            changed.Add("wrighty-agent");
        }
        if (patch.WorkerState.IsSpecified &&
            !string.Equals(document.WorkerState, patch.WorkerState.Value,
                StringComparison.OrdinalIgnoreCase))
        {
            document.WorkerState = patch.WorkerState.Value;
            changed.Add("wrighty-worker-state");
        }
        ApplyArchive(document, operation.ArchiveAfterUpdate, changed);
    }

    private static void ApplyTitle(
        LocalMarkdownDocument document,
        OptionalValue<string> title,
        ICollection<string> changed)
    {
        if (title.IsSpecified && !string.Equals(document.Title, title.Value, StringComparison.Ordinal))
        {
            document.Title = title.Value!;
            changed.Add("title");
        }
    }

    private static void ApplyBody(
        LocalMarkdownDocument document,
        OptionalValue<string> body,
        ICollection<string> changed)
    {
        if (body.IsSpecified && !string.Equals(document.Body, body.Value, StringComparison.Ordinal))
        {
            document.Body = body.Value!;
            changed.Add("body");
        }
    }

    private static void ApplyPriority(
        TrackerConfig config,
        LocalMarkdownDocument document,
        OptionalValue<string?> priorityPatch,
        ICollection<string> changed)
    {
        if (priorityPatch.IsSpecified)
        {
            var priority = CanonicalPriority(config, priorityPatch.Value);
            if (!string.Equals(document.Priority, priority, StringComparison.OrdinalIgnoreCase))
            {
                document.Priority = priority;
                changed.Add("priority");
            }
        }
    }

    private static void ApplyStatus(
        TrackerConfig config,
        LocalMarkdownDocument document,
        OptionalValue<string> statusPatch,
        ICollection<string> changed)
    {
        if (statusPatch.IsSpecified)
        {
            var status = CanonicalStatus(config, statusPatch.Value!);
            if (!string.Equals(document.Status, status, StringComparison.OrdinalIgnoreCase))
            {
                document.Status = status;
                changed.Add("status");
            }
        }
    }

    private static void ApplyCustomFields(
        LocalMarkdownDocument document,
        OptionalValue<IReadOnlyDictionary<string, string?>> fieldsPatch,
        ICollection<string> changed)
    {
        if (fieldsPatch.IsSpecified)
        {
            foreach (var field in fieldsPatch.Value ?? new Dictionary<string, string?>())
            {
                LocalMarkdownReservedFields.ValidateCustomFieldName(field.Key);
                var before = document.CustomFieldScalar(field.Key);
                if (!string.Equals(before, field.Value, StringComparison.Ordinal) ||
                    (field.Value is null && document.CustomFields.ContainsKey(field.Key)))
                {
                    document.SetCustomField(field.Key, field.Value);
                    changed.Add($"field:{field.Key}");
                }
            }
        }
    }

    private static void ApplyArchive(
        LocalMarkdownDocument document,
        bool archive,
        ICollection<string> changed)
    {
        if (archive)
        {
            document.Archived = true;
            document.WorkerState = null;
            changed.Add("archived");
        }
    }

    public Task<ClaimResult> TryClaimAsync(TrackerConfig config, WorkItemId id,
        AgentExecutionContext agentContext, CancellationToken cancellationToken) =>
        TryClaimAsync(config, id, agentContext, cancellationToken, null);

    public async Task<ClaimResult> TryClaimAsync(
        TrackerConfig config,
        WorkItemId id,
        AgentExecutionContext agentContext,
        CancellationToken cancellationToken,
        string? expectedClaimToken)
    {
        EnsureStore(config);
        var paths = Paths(config);
        await using var storeLock = await LocalStoreLock.AcquireAsync(paths.Root, cancellationToken);
        var document = await RequiredUnlockedAsync(config, id, cancellationToken);
        if (document.Archived)
        {
            throw Archived(id);
        }

        var state = await LocalRuntimeStateStore.LoadUnlockedAsync(paths.Root, cancellationToken);
        var worker = await identityProvider.GetIdentityAsync(cancellationToken);
        var now = clock.UtcNow;
        var current = state.Claim(document.Id);
        if (current is not null && current.ExpiresAt > now)
        {
            if (!string.Equals(current.WorkerIdentity, worker, StringComparison.Ordinal))
                return ClaimResult(current, ClaimOutcome.HeldByOther, false);
            var claimantId = ResolveClaimantId(agentContext, generateForAgent: false);
            if (!string.Equals(current.ClaimantId, claimantId, StringComparison.Ordinal))
                return ClaimResult(current, ClaimOutcome.HeldByLocalClaimant, true);
            if (expectedClaimToken is null)
                throw ClaimError("CLAIM_TOKEN_REQUIRED", id, current, true,
                    "The current claimant must present its claim token; use takeover for lost-token recovery.");
            if (!FixedEquals(current.ClaimToken, expectedClaimToken))
                throw ClaimError("CLAIM_STALE", id, current, true, "The supplied claim token is stale.");
            return ClaimResult(current, ClaimOutcome.AlreadyOwned, true);
        }

        state.PreserveSession(document.Id, current, now);
        var claimantIdForClaim = ResolveClaimantId(agentContext, generateForAgent: true);
        var claim = new LocalClaimRecord(
            worker,
            claimantIdForClaim,
            Guid.NewGuid().ToString("N"),
            agentContext.AgentType,
            agentContext.SessionId,
            now,
            now.AddMinutes(config.LeaseMinutes),
            ClaimantKinds.ToStorageValue(agentContext.EffectiveClaimantKind));
        state.Claims[document.Id] = claim;
        await LocalRuntimeStateStore.SaveUnlockedAsync(paths.Root, state, cancellationToken);
        return ClaimResult(claim, ClaimOutcome.Acquired, true);
    }

    public async Task<ClaimResult> TakeoverAsync(
        TrackerConfig config,
        WorkItemId id,
        AgentExecutionContext claimantContext,
        string? currentClaimToken,
        CancellationToken cancellationToken)
    {
        EnsureStore(config);
        var paths = Paths(config);
        await using var storeLock = await LocalStoreLock.AcquireAsync(paths.Root, cancellationToken);
        await PauseAfterLockAsync("takeover", cancellationToken);
        var document = await RequiredUnlockedAsync(config, id, cancellationToken);
        if (document.Archived) throw Archived(id);
        var state = await LocalRuntimeStateStore.LoadUnlockedAsync(paths.Root, cancellationToken);
        var current = state.Claim(document.Id);
        if (current is null || current.ExpiresAt <= clock.UtcNow)
            throw new TrackerException(
                "CLAIM_NOT_FOUND",
                $"Work item '{id}' has no active claim. Takeover is no longer possible after " +
                $"the prior claim expires or is released. Continue with: " +
                $"wrighty worker --item {id.Value} --yes",
                5);
        var worker = await identityProvider.GetIdentityAsync(cancellationToken);
        if (!string.Equals(current.WorkerIdentity, worker, StringComparison.Ordinal))
            throw ClaimError("CLAIM_NOT_OWNER", id, current, false, "Another Wrighty installation owns this claim.");
        var claimantId = ResolveClaimantId(claimantContext, generateForAgent: true);
        if (string.Equals(current.ClaimantId, claimantId, StringComparison.Ordinal) &&
            currentClaimToken is not null && FixedEquals(current.ClaimToken, currentClaimToken))
            return ClaimResult(current, ClaimOutcome.AlreadyOwned, true);

        var now = clock.UtcNow;
        state.PreserveSession(document.Id, current, now);
        var replacement = new LocalClaimRecord(
            worker, claimantId, Guid.NewGuid().ToString("N"),
            claimantContext.AgentType ?? current.AgentType,
            claimantContext.SessionId ?? current.SessionId, now, now.AddMinutes(config.LeaseMinutes),
            ClaimantKinds.ToStorageValue(claimantContext.EffectiveClaimantKind),
            current.WorkspacePath);
        state.Claims[document.Id] = replacement;
        await LocalRuntimeStateStore.SaveUnlockedAsync(paths.Root, state, cancellationToken);
        return ClaimResult(replacement, ClaimOutcome.TakenOver, true);
    }

    public Task<ClaimResult> RenewClaimAsync(
        TrackerConfig config,
        WorkItemId id,
        ClaimHandle claimHandle,
        string? workspacePath,
        string? sessionId,
        CancellationToken cancellationToken) =>
        RenewClaimAsync(config, id, claimHandle, workspacePath, sessionId, branch: null,
            cancellationToken);

    public async Task<ClaimResult> RenewClaimAsync(
        TrackerConfig config,
        WorkItemId id,
        ClaimHandle claimHandle,
        string? workspacePath,
        string? sessionId,
        string? branch,
        CancellationToken cancellationToken)
    {
        EnsureStore(config);
        var paths = Paths(config);
        await using var storeLock = await LocalStoreLock.AcquireAsync(paths.Root, cancellationToken);
        var document = await RequiredUnlockedAsync(config, id, cancellationToken);
        var state = await LocalRuntimeStateStore.LoadUnlockedAsync(paths.Root, cancellationToken);
        var current = state.ActiveClaim(document.Id, clock.UtcNow);
        if (current is null)
            throw new TrackerException("CLAIM_EXPIRED", $"Work item '{id}' no longer has an active claim.", 6);
        await EnsureOwnedUnlockedAsync(state, document.Id, claimHandle, cancellationToken);
        var now = clock.UtcNow;
        var renewed = current with
        {
            ExpiresAt = now.AddMinutes(config.LeaseMinutes),
            AgentType = claimHandle.Claimant.AgentType ?? current.AgentType,
            SessionId = sessionId ?? current.SessionId,
            ClaimantKind = ClaimantKinds.ToStorageValue(claimHandle.Claimant.EffectiveClaimantKind),
            WorkspacePath = workspacePath ?? current.WorkspacePath,
            Branch = branch ?? current.Branch
        };
        state.Claims[document.Id] = renewed;
        state.PreserveSession(document.Id, renewed, now);
        await LocalRuntimeStateStore.SaveUnlockedAsync(paths.Root, state, cancellationToken);
        return ClaimResult(renewed, ClaimOutcome.AlreadyOwned, true);
    }

    public async Task<ClaimOwnershipResult> GetClaimOwnershipAsync(
        TrackerConfig config,
        WorkItemId id,
        CancellationToken cancellationToken)
    {
        EnsureStore(config);
        var paths = Paths(config);
        await using var storeLock = await LocalStoreLock.AcquireAsync(paths.Root, cancellationToken);
        var document = await RequiredUnlockedAsync(config, id, cancellationToken);
        var state = await LocalRuntimeStateStore.LoadUnlockedAsync(paths.Root, cancellationToken);
        var current = state.ActiveClaim(document.Id, clock.UtcNow);
        if (current is null)
        {
            return new ClaimOwnershipResult(ClaimOwnershipState.Unclaimed);
        }

        var worker = await identityProvider.GetIdentityAsync(cancellationToken);
        return new ClaimOwnershipResult(
            string.Equals(current.WorkerIdentity, worker, StringComparison.Ordinal)
                ? ClaimOwnershipState.OwnedByCurrent
                : ClaimOwnershipState.HeldByOther,
            current.WorkerIdentity,
            current.ExpiresAt,
            current.ClaimantId,
            current.AgentType,
            current.SessionId,
            current.ClaimantKind,
            string.Equals(current.WorkerIdentity, worker, StringComparison.Ordinal),
            current.WorkspacePath);
    }

    public async Task<AgentSessionRecord?> GetAgentSessionAsync(
        TrackerConfig config,
        WorkItemId id,
        CancellationToken cancellationToken)
    {
        EnsureStore(config);
        var paths = Paths(config);
        await using var storeLock = await LocalStoreLock.AcquireAsync(paths.Root, cancellationToken);
        var document = await RequiredUnlockedAsync(config, id, cancellationToken);
        var state = await LocalRuntimeStateStore.LoadUnlockedAsync(paths.Root, cancellationToken);
        var worker = await identityProvider.GetIdentityAsync(cancellationToken);
        return SessionRecord(state, document.Id, worker);
    }

    public async Task RecordRunOutcomeAsync(
        TrackerConfig config,
        WorkItemId id,
        RunOutcome outcome,
        string? finalMessage,
        DateTimeOffset endedAt,
        CancellationToken cancellationToken)
    {
        EnsureStore(config);
        var paths = Paths(config);
        await using var storeLock = await LocalStoreLock.AcquireAsync(paths.Root, cancellationToken);
        var document = await RequiredUnlockedAsync(config, id, cancellationToken);
        var state = await LocalRuntimeStateStore.LoadUnlockedAsync(paths.Root, cancellationToken);
        state.RecordRunOutcome(document.Id, outcome, finalMessage, endedAt);
        await LocalRuntimeStateStore.SaveUnlockedAsync(paths.Root, state, cancellationToken);
    }

    public async Task ReleaseAsync(
        TrackerConfig config,
        WorkItemId id,
        CancellationToken cancellationToken)
    {
        EnsureStore(config);
        var paths = Paths(config);
        await using var storeLock = await LocalStoreLock.AcquireAsync(paths.Root, cancellationToken);
        var document = await RequiredUnlockedAsync(config, id, cancellationToken);
        var state = await LocalRuntimeStateStore.LoadUnlockedAsync(paths.Root, cancellationToken);
        var current = state.ActiveClaim(document.Id, clock.UtcNow);
        if (current is null)
        {
            throw new TrackerException("CLAIM_NOT_FOUND", $"Work item '{id}' does not have an active claim.", 5);
        }

        throw ClaimError("CLAIM_TOKEN_REQUIRED", id, current, true,
            "Release requires --claimant-id and --claim-token.");
    }

    public async Task ReleaseAsync(TrackerConfig config, WorkItemId id, ClaimHandle claimHandle,
        bool overrideClaimant, CancellationToken cancellationToken)
    {
        EnsureStore(config);
        var paths = Paths(config);
        await using var storeLock = await LocalStoreLock.AcquireAsync(paths.Root, cancellationToken);
        await PauseAfterLockAsync(overrideClaimant ? "overrideRelease" : "release", cancellationToken);
        var document = await RequiredUnlockedAsync(config, id, cancellationToken);
        var state = await LocalRuntimeStateStore.LoadUnlockedAsync(paths.Root, cancellationToken);
        var current = state.ActiveClaim(document.Id, clock.UtcNow);
        if (current is null)
            throw new TrackerException("CLAIM_NOT_FOUND", $"Work item '{id}' does not have an active claim.", 5);
        var worker = await identityProvider.GetIdentityAsync(cancellationToken);
        if (!string.Equals(current.WorkerIdentity, worker, StringComparison.Ordinal))
            throw ClaimError("CLAIM_NOT_OWNER", id, current, false, "Another Wrighty installation owns this claim.");
        if (!overrideClaimant) await EnsureOwnedUnlockedAsync(state, document.Id, claimHandle, cancellationToken);
        var now = clock.UtcNow;
        state.PreserveSession(document.Id, current, now);
        state.Claims.Remove(document.Id);
        await LocalRuntimeStateStore.SaveUnlockedAsync(paths.Root, state, cancellationToken);
        if (document.WorkerState is not null)
        {
            document.WorkerState = null;
            document.UpdatedAt = now;
            await WriteUnlockedAsync(document, document.Path, cancellationToken);
        }
    }

    public async Task RequeueAsync(
        TrackerConfig config,
        WorkItemId id,
        ClaimHandle claimHandle,
        CancellationToken cancellationToken)
    {
        EnsureStore(config);
        var paths = Paths(config);
        await using var storeLock = await LocalStoreLock.AcquireAsync(paths.Root, cancellationToken);
        var document = await RequiredUnlockedAsync(config, id, cancellationToken);
        var state = await LocalRuntimeStateStore.LoadUnlockedAsync(paths.Root, cancellationToken);
        var current = state.ActiveClaim(document.Id, clock.UtcNow);
        if (current is null)
            throw new TrackerException(
                "CLAIM_NOT_FOUND",
                $"Work item '{id}' does not have an active claim to requeue.",
                5);
        await EnsureOwnedUnlockedAsync(state, document.Id, claimHandle, cancellationToken);
        if (!document.AutomationEligible)
            throw new TrackerException(
                "WORKER_ITEM_INELIGIBLE",
                $"Work item '{id}' must have wrighty-auto=true before it can be queued.",
                5);
        if (!string.Equals(document.Status, config.DefaultPickTo,
                StringComparison.OrdinalIgnoreCase))
            throw new TrackerException(
                "WORKER_ITEM_INELIGIBLE",
                $"Work item '{id}' must have status '{config.DefaultPickTo}' before it can be queued.",
                5);
        if (string.IsNullOrWhiteSpace(current.AgentType) ||
            string.IsNullOrWhiteSpace(current.SessionId) ||
            string.IsNullOrWhiteSpace(current.WorkspacePath))
            throw new TrackerException(
                "RESUME_ADDRESS_UNAVAILABLE",
                $"Work item '{id}' does not have a complete agent session to queue.",
                5);

        var requeuedAt = clock.UtcNow;
        state.PreserveSession(document.Id, current, requeuedAt);
        state.Claims.Remove(document.Id);
        await LocalRuntimeStateStore.SaveUnlockedAsync(paths.Root, state, cancellationToken);
        document.WorkerState = WorkerDispatchStates.Queued;
        document.UpdatedAt = requeuedAt;
        await WriteUnlockedAsync(document, document.Path, cancellationToken);
    }

    public async Task QueuePausedAsync(
        TrackerConfig config,
        WorkItemId id,
        CancellationToken cancellationToken)
    {
        EnsureStore(config);
        var paths = Paths(config);
        await using var storeLock = await LocalStoreLock.AcquireAsync(paths.Root, cancellationToken);
        var document = await RequiredUnlockedAsync(config, id, cancellationToken);
        if (document.Archived)
            throw Archived(id);
        if (!string.Equals(document.WorkerState, WorkerDispatchStates.NeedsAttention,
                StringComparison.OrdinalIgnoreCase))
            throw new TrackerException(
                "WORKER_ITEM_NOT_PAUSED",
                $"Work item '{id}' is no longer waiting for attention.",
                6);
        if (!document.AutomationEligible)
            throw new TrackerException(
                "WORKER_ITEM_INELIGIBLE",
                $"Work item '{id}' must have wrighty-auto=true before it can be queued.",
                5);
        if (!string.Equals(document.Status, config.DefaultPickTo,
                StringComparison.OrdinalIgnoreCase))
            throw new TrackerException(
                "WORKER_ITEM_INELIGIBLE",
                $"Work item '{id}' must have status '{config.DefaultPickTo}' before it can be queued.",
                5);

        var state = await LocalRuntimeStateStore.LoadUnlockedAsync(paths.Root, cancellationToken);
        var worker = await identityProvider.GetIdentityAsync(cancellationToken);
        var now = clock.UtcNow;
        var activeClaim = state.ActiveClaim(document.Id, now);
        if (activeClaim is not null &&
            !string.Equals(activeClaim.WorkerIdentity, worker, StringComparison.Ordinal))
            throw ClaimError(
                "CLAIM_NOT_OWNER",
                id,
                activeClaim,
                false,
                "Another Wrighty installation owns this claim.");

        var recordedClaim = state.Claim(document.Id);
        var recordedSession = state.Session(document.Id);
        var recordedWorker = recordedClaim?.WorkerIdentity ?? recordedSession?.WorkerIdentity;
        if (!string.IsNullOrWhiteSpace(recordedWorker) &&
            !string.Equals(recordedWorker, worker, StringComparison.Ordinal))
            throw new TrackerException(
                "RESUME_ADDRESS_NOT_LOCAL",
                $"Work item '{id}' has a recorded session from another Wrighty installation.",
                5);

        var agentType = recordedClaim?.AgentType ?? recordedSession?.AgentType;
        var sessionId = recordedClaim?.SessionId ?? recordedSession?.SessionId;
        var workspacePath = recordedClaim?.WorkspacePath ?? recordedSession?.WorkspacePath;
        if (string.IsNullOrWhiteSpace(agentType) ||
            string.IsNullOrWhiteSpace(sessionId) ||
            string.IsNullOrWhiteSpace(workspacePath))
            throw new TrackerException(
                "RESUME_ADDRESS_UNAVAILABLE",
                $"Work item '{id}' does not have a complete agent session to queue.",
                5);

        state.Sessions[document.Id] = new LocalSessionRecord(
            worker,
            agentType,
            sessionId,
            workspacePath,
            now,
            recordedClaim?.ExpiresAt ?? recordedSession?.LastClaimExpiresAt);
        state.Claims.Remove(document.Id);
        await LocalRuntimeStateStore.SaveUnlockedAsync(paths.Root, state, cancellationToken);
        document.WorkerState = WorkerDispatchStates.Queued;
        document.UpdatedAt = now;
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
            throw Archived(id);
        }

        throw new TrackerException("CLAIM_TOKEN_REQUIRED", $"Archive of '{id}' requires --claimant-id and --claim-token.", 6);
    }

    public async Task<ArchiveWorkItemResult> ArchiveAsync(
        TrackerConfig config, WorkItemId id, ClaimHandle claimHandle, CancellationToken cancellationToken)
    {
        EnsureStore(config);
        var paths = Paths(config);
        await using var storeLock = await LocalStoreLock.AcquireAsync(paths.Root, cancellationToken);
        await PauseAfterLockAsync("archive", cancellationToken);
        var document = await RequiredUnlockedAsync(config, id, cancellationToken);
        if (document.Archived) throw Archived(id);
        var state = await LocalRuntimeStateStore.LoadUnlockedAsync(paths.Root, cancellationToken);
        await EnsureOwnedUnlockedAsync(state, document.Id, claimHandle, cancellationToken);
        var now = clock.UtcNow;
        state.PreserveSession(document.Id, state.Claim(document.Id), now);
        state.Claims.Remove(document.Id);
        await LocalRuntimeStateStore.SaveUnlockedAsync(paths.Root, state, cancellationToken);
        var originalPath = document.Path;
        document.Archived = true;
        document.WorkerState = null;
        document.UpdatedAt = now;
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
        var paths = Paths(config);
        await using var storeLock = await LocalStoreLock.AcquireAsync(paths.Root, cancellationToken);
        var document = await RequiredUnlockedAsync(config, id, cancellationToken);
        if (!document.Archived)
        {
            return new ArchiveWorkItemResult(Detail(document), false, false);
        }

        var state = await LocalRuntimeStateStore.LoadUnlockedAsync(paths.Root, cancellationToken);
        if (state.ActiveClaim(document.Id, clock.UtcNow) is { } claim)
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

        if (state.Claims.Remove(document.Id))
        {
            await LocalRuntimeStateStore.SaveUnlockedAsync(paths.Root, state, cancellationToken);
        }

        var originalPath = document.Path;
        document.Archived = false;
        document.UpdatedAt = clock.UtcNow;
        document.Path = CanonicalPath(config, document);
        await WriteUnlockedAsync(document, originalPath, cancellationToken);
        return new ArchiveWorkItemResult(Detail(document), true, false);
    }

    private async Task EnsureOwnedUnlockedAsync(
        LocalRuntimeState state,
        int documentId,
        ClaimHandle? handle,
        CancellationToken cancellationToken)
    {
        var claim = state.Claim(documentId);
        var worker = await identityProvider.GetIdentityAsync(cancellationToken);
        var id = LocalMarkdownWorkItemAddressResolver.FromNumber(documentId);
        if (claim is null || claim.ExpiresAt <= clock.UtcNow)
            throw new TrackerException("CLAIM_REQUIRED", $"Work item '{id}' must have an active claim before it can be updated.", 6);
        if (!string.Equals(claim.WorkerIdentity, worker, StringComparison.Ordinal))
            throw ClaimError("CLAIM_HELD", id, claim, false, "Another Wrighty installation owns this claim.");
        if (handle is null || string.IsNullOrWhiteSpace(handle.ClaimToken))
            throw ClaimError("CLAIM_TOKEN_REQUIRED", id, claim, true, "A claim token is required for this mutation.");
        if (!string.Equals(claim.ClaimantId, handle.ClaimantId, StringComparison.Ordinal) ||
            !FixedEquals(claim.ClaimToken, handle.ClaimToken!))
            throw ClaimError("CLAIM_STALE", id, claim, true, "This claimant handle is stale; the item may have been taken over.");
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
        CancellationToken cancellationToken,
        bool allowLegacyClaimMetadata = false,
        IReadOnlyCollection<string>? excludedPaths = null)
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
                if (excludedPaths?.Contains(Path.GetFullPath(path), StringComparer.Ordinal) == true)
                {
                    continue;
                }
                var match = ItemFileName().Match(Path.GetFileName(path));
                if (!match.Success ||
                    !int.TryParse(match.Groups["number"].Value, NumberStyles.None,
                        CultureInfo.InvariantCulture, out var id) || id <= 0)
                {
                    throw new TrackerException(
                        "WORK_ITEM_DOCUMENT_INVALID",
                        $"Markdown file '{path}' does not use the required numeric-title filename format. Use 'wrighty import --in-place {path}' to normalize this unmanaged document.",
                        5,
                        new Dictionary<string, object?> { ["path"] = path, ["reason"] = "filename" });
                }

                var bytes = await File.ReadAllBytesAsync(path, cancellationToken);
                var content = StrictUtf8.GetString(bytes);
                var document = LocalMarkdownDocumentCodec.Parse(
                    id,
                    path,
                    archived,
                    content,
                    Revision(bytes),
                    allowLegacyClaimMetadata);
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
            var content = LocalMarkdownDocumentCodec.Serialize(document);
            var bytes = StrictUtf8.GetBytes(content);
            await File.WriteAllBytesAsync(temporary, bytes, cancellationToken);
            File.Move(temporary, destination, overwrite: originalPath == destination);
            if (originalPath is not null &&
                !string.Equals(originalPath, destination, StringComparison.Ordinal))
            {
                File.Delete(originalPath);
            }

            document.Revision = Revision(bytes);
            document.RawFrontmatter = LocalMarkdownDocumentCodec.SerializeFrontmatter(document.Metadata);
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
        document.Archived,
        document.AutomationEligible,
        document.PreferredAgent,
        document.WorkerState);

    private static WorkItemDetail Detail(LocalMarkdownDocument document) => new(
        LocalMarkdownWorkItemAddressResolver.FromNumber(document.Id),
        document.Title,
        document.Body,
        null,
        document.Status,
        document.Priority,
        document.Archived,
        document.CustomFields,
        document.RawFrontmatter,
        document.AutomationEligible,
        document.PreferredAgent,
        WorkerState: document.WorkerState);

    private static ClaimResult ClaimResult(LocalClaimRecord claim, ClaimOutcome outcome, bool takeoverAvailable) => new(
        outcome,
        claim.WorkerIdentity,
        claim.ExpiresAt,
        null,
        claim.AgentType,
        claim.SessionId,
        claim.ClaimantKind,
        claim.ClaimantId,
        outcome is ClaimOutcome.Acquired or ClaimOutcome.AlreadyOwned or ClaimOutcome.TakenOver
            ? claim.ClaimToken
            : null,
        takeoverAvailable,
        claim.WorkspacePath);

    private static string ResolveClaimantId(AgentExecutionContext context, bool generateForAgent)
    {
        if (!string.IsNullOrWhiteSpace(context.ClaimantId)) return context.ClaimantId;
        if (context.EffectiveClaimantKind == ClaimantKind.Human) return "human-cli";
        if (context.EffectiveClaimantKind == ClaimantKind.Automation)
            throw new TrackerException("ARGUMENT_INVALID", "Automation requires an explicit claimant ID.", 2);
        if (context.EffectiveClaimantKind == ClaimantKind.Agent && generateForAgent)
            return $"agent:{Guid.NewGuid():N}";
        return generateForAgent ? $"claimant:{Guid.NewGuid():N}" : string.Empty;
    }

    private Task PauseAfterLockAsync(string operation, CancellationToken cancellationToken) =>
        afterMutationLockAcquired?.Invoke(operation, cancellationToken) ?? Task.CompletedTask;

    private static bool FixedEquals(string left, string right)
    {
        var leftHash = SHA256.HashData(Encoding.UTF8.GetBytes(left));
        var rightHash = SHA256.HashData(Encoding.UTF8.GetBytes(right));
        return CryptographicOperations.FixedTimeEquals(leftHash, rightHash);
    }

    private static TrackerException ClaimError(
        string code, WorkItemId id, LocalClaimRecord claim, bool sameInstallation, string message) =>
        new(code, $"{message} Work item '{id}' is claimed by {Short(claim.ClaimantId)} until {claim.ExpiresAt:O}.", 6,
            new Dictionary<string, object?>
            {
                ["id"] = id.Value,
                ["workerIdentity"] = claim.WorkerIdentity,
                ["claimantId"] = Short(claim.ClaimantId),
                ["claimantKind"] = claim.ClaimantKind,
                ["agentType"] = claim.AgentType,
                ["expiresAt"] = claim.ExpiresAt,
                ["sameInstallation"] = sameInstallation,
                ["takeoverAvailable"] = sameInstallation
            });

    private static string Short(string value) => value.Length <= 12 ? value : $"{value[..12]}…";

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

    private static int StatusRank(TrackerConfig config, string status)
    {
        for (var index = 0; index < config.LocalMarkdown!.Statuses.Count; index++)
        {
            if (string.Equals(config.LocalMarkdown.Statuses[index], status,
                    StringComparison.OrdinalIgnoreCase))
            {
                return index;
            }
        }

        return int.MaxValue;
    }

    private static WorkItemClaimSummary ClaimSummary(
        LocalRuntimeState state,
        int documentId,
        string worker,
        DateTimeOffset now)
    {
        var claim = state.Claim(documentId);
        if (claim is not null && claim.ExpiresAt > now)
        {
            return new WorkItemClaimSummary(
                string.Equals(claim.WorkerIdentity, worker, StringComparison.Ordinal)
                    ? ClaimOwnershipState.OwnedByCurrent
                    : ClaimOwnershipState.HeldByOther,
                claim.WorkerIdentity,
                claim.ExpiresAt,
                claim.AgentType,
                claim.SessionId,
                claim.ClaimantKind,
                claim.ClaimantId,
                string.Equals(claim.WorkerIdentity, worker, StringComparison.Ordinal),
                claim.WorkspacePath);
        }

        if (claim is not null)
        {
            return new WorkItemClaimSummary(
                ClaimOwnershipState.Unclaimed,
                claim.WorkerIdentity,
                claim.ExpiresAt,
                claim.AgentType,
                claim.SessionId,
                claim.ClaimantKind,
                claim.ClaimantId,
                false,
                claim.WorkspacePath);
        }

        if (state.Session(documentId) is { } record)
        {
            return new WorkItemClaimSummary(
                ClaimOwnershipState.Unclaimed,
                record.WorkerIdentity,
                record.LastClaimExpiresAt,
                record.AgentType,
                record.SessionId,
                WorkspacePath: record.WorkspacePath);
        }

        return new WorkItemClaimSummary(ClaimOwnershipState.Unclaimed);
    }

    private static string Revision(ReadOnlySpan<byte> content) =>
        Convert.ToHexString(SHA256.HashData(content)).ToLowerInvariant();

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
