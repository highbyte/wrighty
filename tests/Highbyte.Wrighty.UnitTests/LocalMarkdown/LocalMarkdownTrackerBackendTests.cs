using Highbyte.Wrighty;
using Highbyte.Wrighty.AgentContext;
using Highbyte.Wrighty.Backends;
using Highbyte.Wrighty.Claims;
using Highbyte.Wrighty.Configuration;
using Highbyte.Wrighty.Errors;
using Highbyte.Wrighty.Identity;
using Highbyte.Wrighty.LocalMarkdown;
using Highbyte.Wrighty.Models;
using Highbyte.Wrighty.Time;
using System.Diagnostics;
using Highbyte.Wrighty.Cli;
using Highbyte.Wrighty.Cli.Output;
using System.Text.Json;

namespace Highbyte.Wrighty.UnitTests.LocalMarkdown;

public sealed class LocalMarkdownTrackerBackendTests : IDisposable
{
    private readonly string directory = Path.Combine(
        Path.GetTempPath(),
        $"wrighty-local-tests-{Guid.NewGuid():N}");

    [Theory]
    [InlineData("Develop login feature", "develop-login-feature")]
    [InlineData("Lägg till inloggning!", "lagg-till-inloggning")]
    [InlineData("CON", "con")]
    [InlineData("🚀 東京", "item")]
    public void Slugify_is_portable_and_deterministic(string title, string expected) =>
        Assert.Equal(expected, PortableFilenameSlugger.Slugify(title));

    [Fact]
    public async Task Create_claim_edit_archive_and_unarchive_preserve_identity_and_body()
    {
        var clock = new FakeClock(new DateTimeOffset(2026, 7, 14, 10, 0, 0, TimeSpan.Zero));
        var backend = new LocalMarkdownTrackerBackend(new FakeIdentity("worker-a"), clock);
        var config = Config();
        await backend.InitializeAsync(config, false, CancellationToken.None);

        var created = await backend.CreateAsync(
            config,
            new CreateWorkItemOperation(
                new CreateWorkItemRequest(
                    "Develop login feature",
                    "Body with --- and `code`\n",
                    "Todo",
                    "P1"),
                false),
            CancellationToken.None);

        Assert.Equal("local:1", created.Id.Value);
        var original = Path.Combine(StoreRoot, "items", "001-develop-login-feature.md");
        Assert.True(File.Exists(original));
        Assert.DoesNotContain("\nid:", await File.ReadAllTextAsync(original));

        var claim = await backend.TryClaimAsync(
            config,
            created.Id,
            new AgentExecutionContext("codex", "session-1", AgentContextSource.ExplicitOption),
            CancellationToken.None);
        Assert.Equal(ClaimOutcome.Acquired, claim.Outcome);
        Assert.Equal("agent", claim.ClaimantKind);
        Assert.Contains("claimantKind: agent", await File.ReadAllTextAsync(original));

        clock.UtcNow = clock.UtcNow.AddMinutes(1);
        var updated = await backend.UpdateAsync(
            config,
            created.Id,
            new UpdateWorkItemOperation(
                new WorkItemPatch(
                    OptionalValue<string>.From("Implement authentication flow"),
                    default,
                    OptionalValue<string>.From("In Progress"),
                    default),
                false),
            CancellationToken.None);
        Assert.Equal("local:1", updated.Item.Id.Value);
        Assert.Equal("Body with --- and `code`\n", updated.Item.Body);
        Assert.False(File.Exists(original));
        Assert.True(File.Exists(Path.Combine(
            StoreRoot,
            "items",
            "001-implement-authentication-flow.md")));

        var archived = await backend.ArchiveAsync(config, created.Id, CancellationToken.None);
        Assert.True(archived.Archived);
        Assert.True(File.Exists(Path.Combine(
            StoreRoot,
            "archive",
            "001-implement-authentication-flow.md")));
        Assert.Empty(await backend.ListAsync(
            config,
            new ListWorkItemsRequest(null, null),
            CancellationToken.None));
        Assert.Single(await backend.ListAsync(
            config,
            new ListWorkItemsRequest(null, null, ArchiveScope.Archived),
            CancellationToken.None));
        Assert.Equal(
            ClaimOwnershipState.Unclaimed,
            (await backend.GetClaimOwnershipAsync(config, created.Id, CancellationToken.None)).State);

        var restored = await backend.UnarchiveAsync(config, created.Id, CancellationToken.None);
        Assert.False(restored.Archived);
        Assert.Equal("In Progress", restored.Item.Status);
        Assert.Equal("P1", restored.Item.Priority);
    }

    [Fact]
    public async Task Claims_have_one_winner_across_backend_instances()
    {
        var clock = new FakeClock(new DateTimeOffset(2026, 7, 14, 10, 0, 0, TimeSpan.Zero));
        var first = new LocalMarkdownTrackerBackend(new FakeIdentity("worker-a"), clock);
        var second = new LocalMarkdownTrackerBackend(new FakeIdentity("worker-b"), clock);
        var config = Config();
        await first.InitializeAsync(config, false, CancellationToken.None);
        var created = await first.CreateAsync(
            config,
            new CreateWorkItemOperation(
                new CreateWorkItemRequest("Contested", string.Empty, "Todo", null),
                false),
            CancellationToken.None);

        var results = await Task.WhenAll(
            first.TryClaimAsync(config, created.Id, AgentExecutionContext.None, CancellationToken.None),
            second.TryClaimAsync(config, created.Id, AgentExecutionContext.None, CancellationToken.None));

        Assert.Single(results, result => result.Outcome == ClaimOutcome.Acquired);
        Assert.Single(results, result => result.Outcome == ClaimOutcome.HeldByOther);
    }

    [Fact]
    public async Task Create_with_same_attempt_id_resumes_and_conflicting_intent_fails()
    {
        var backend = new LocalMarkdownTrackerBackend(
            new FakeIdentity("worker-a"),
            new FakeClock(new DateTimeOffset(2026, 7, 14, 10, 0, 0, TimeSpan.Zero)));
        var config = Config();
        await backend.InitializeAsync(config, false, CancellationToken.None);
        const string attemptId = "019f5c485c2b7862aeac80eb638a7b5c";
        var operation = new CreateWorkItemOperation(
            new CreateWorkItemRequest("Retry safe", "Body", "Todo", "P1"),
            false,
            attemptId);

        var first = await backend.CreateAsync(config, operation, CancellationToken.None);
        var replay = await backend.CreateAsync(config, operation, CancellationToken.None);

        Assert.Equal(first.Id, replay.Id);
        Assert.Equal(CreateDisposition.Created, first.Disposition);
        Assert.Equal(CreateDisposition.Resumed, replay.Disposition);
        Assert.Equal(attemptId, replay.CreationAttemptId);
        Assert.Single(Directory.EnumerateFiles(Path.Combine(StoreRoot, "items"), "*.md"));
        var content = await File.ReadAllTextAsync(Directory.EnumerateFiles(
            Path.Combine(StoreRoot, "items"), "*.md").Single());
        Assert.Contains("attemptId: 019f5c485c2b7862aeac80eb638a7b5c", content);
        Assert.Contains("requestHash:", content);

        var exception = await Assert.ThrowsAsync<TrackerException>(() => backend.CreateAsync(
            config,
            operation with { Request = operation.Request with { Title = "Different" } },
            CancellationToken.None));
        Assert.Equal("CREATION_ATTEMPT_CONFLICT", exception.Code);
        Assert.Single(Directory.EnumerateFiles(Path.Combine(StoreRoot, "items"), "*.md"));
    }

    [Fact]
    public async Task Unknown_frontmatter_is_preserved_during_updates()
    {
        var clock = new FakeClock(new DateTimeOffset(2026, 7, 14, 10, 0, 0, TimeSpan.Zero));
        var backend = new LocalMarkdownTrackerBackend(new FakeIdentity("worker-a"), clock);
        var config = Config();
        await backend.InitializeAsync(config, false, CancellationToken.None);
        var created = await backend.CreateAsync(
            config,
            new CreateWorkItemOperation(
                new CreateWorkItemRequest("Metadata", "Body", "Todo", null),
                false),
            CancellationToken.None);
        var path = Path.Combine(StoreRoot, "items", "001-metadata.md");
        var content = await File.ReadAllTextAsync(path);
        await File.WriteAllTextAsync(path, content.Replace(
            "claimEpoch: 0",
            "claimEpoch: 0\n# user comment\ncustomField: retained\nnested:\n  enabled: true\nsequence: [one, two]\nmultiline: |-\n  first\n  second\nunicode: räksmörgås"));
        var beforeWrite = await backend.GetAsync(config, created.Id, CancellationToken.None);
        Assert.Contains("# user comment", beforeWrite!.RawFrontmatter);

        await backend.TryClaimAsync(config, created.Id, AgentExecutionContext.None, CancellationToken.None);
        var updated = await backend.UpdateAsync(
            config,
            created.Id,
            new UpdateWorkItemOperation(WorkItemPatch.StatusOnly("Done"), false),
            CancellationToken.None);

        var written = await File.ReadAllTextAsync(path);
        Assert.Contains("customField: retained", written);
        Assert.DoesNotContain("# user comment", written);
        Assert.True(updated.Item.EffectiveFields["nested"].GetProperty("enabled").GetBoolean());
        Assert.Equal(2, updated.Item.EffectiveFields["sequence"].GetArrayLength());
        Assert.Equal("first\nsecond", updated.Item.EffectiveFields["multiline"].GetString());
        Assert.Equal("räksmörgås", updated.Item.EffectiveFields["unicode"].GetString());
        var closing = written.IndexOf("\n---\n", 4, StringComparison.Ordinal);
        Assert.Equal(written[4..(closing + 1)], updated.Item.RawFrontmatter);
    }

    [Fact]
    public async Task Custom_fields_are_read_written_deleted_filtered_and_keep_managed_order()
    {
        var backend = new LocalMarkdownTrackerBackend(
            new FakeIdentity("worker-a"),
            new FakeClock(new DateTimeOffset(2026, 7, 16, 10, 0, 0, TimeSpan.Zero)));
        var config = Config();
        await backend.InitializeAsync(config, false, CancellationToken.None);
        var created = await backend.CreateAsync(
            config,
            new CreateWorkItemOperation(
                new CreateWorkItemRequest(
                    "Fields",
                    "Body",
                    "Todo",
                    null,
                    new Dictionary<string, string?> { ["epic"] = "PLAT-3", ["owner"] = "ana" }),
                false),
            CancellationToken.None);
        var path = Path.Combine(StoreRoot, "items", "001-fields.md");
        var before = await File.ReadAllTextAsync(path);
        var statusIndex = before.IndexOf("status:", StringComparison.Ordinal);
        var epicIndex = before.IndexOf("epic:", StringComparison.Ordinal);

        var detail = await backend.GetAsync(config, created.Id, CancellationToken.None);
        Assert.Equal("PLAT-3", detail!.EffectiveFields["epic"].GetString());
        Assert.Contains("title: Fields", detail.RawFrontmatter);
        Assert.Single(await backend.ListAsync(
            config,
            new ListWorkItemsRequest(null, null, Fields: new Dictionary<string, string>
            {
                ["epic"] = "PLAT-3",
                ["owner"] = "ana"
            }),
            CancellationToken.None));
        Assert.Empty(await backend.ListAsync(
            config,
            new ListWorkItemsRequest(null, null, Fields: new Dictionary<string, string> { ["owner"] = "bo" }),
            CancellationToken.None));

        await backend.TryClaimAsync(config, created.Id, AgentExecutionContext.None, CancellationToken.None);
        var update = await backend.UpdateAsync(
            config,
            created.Id,
            new UpdateWorkItemOperation(
                new WorkItemPatch(default, default, OptionalValue<string>.From("In Progress"), default,
                    OptionalValue<IReadOnlyDictionary<string, string?>>.From(
                        new Dictionary<string, string?> { ["epic"] = null, ["estimate"] = "5" })),
                false),
            CancellationToken.None);

        Assert.Contains("field:epic", update.ChangedFields);
        Assert.Contains("field:estimate", update.ChangedFields);
        Assert.DoesNotContain("epic", update.Item.EffectiveFields.Keys);
        Assert.Equal("5", update.Item.EffectiveFields["estimate"].GetString());
        var after = await File.ReadAllTextAsync(path);
        Assert.True(after.IndexOf("status:", StringComparison.Ordinal) < after.IndexOf("owner:", StringComparison.Ordinal));
        Assert.True(statusIndex < epicIndex);
    }

    [Theory]
    [InlineData("status")]
    [InlineData("wrighty")]
    [InlineData("x-wrighty-future")]
    public void Reserved_custom_field_names_are_rejected(string name)
    {
        var exception = Assert.Throws<TrackerException>(() => WorkItemPatchValidator.Validate(
            new WorkItemPatch(default, default, default, default,
                OptionalValue<IReadOnlyDictionary<string, string?>>.From(
                    new Dictionary<string, string?> { [name] = "value" }))));

        Assert.Equal("RESERVED_FIELD_COLLISION", exception.Code);
        Assert.Contains(name, exception.Message);
    }

    [Fact]
    public async Task Import_supports_dry_run_copy_move_title_resolution_and_custom_yaml()
    {
        var clock = new FakeClock(new DateTimeOffset(2026, 7, 16, 10, 0, 0, TimeSpan.Zero));
        var backend = new LocalMarkdownTrackerBackend(new FakeIdentity("worker-a"), clock);
        var config = Config();
        await backend.InitializeAsync(config, false, CancellationToken.None);
        var sourceDirectory = Path.Combine(directory, "source");
        Directory.CreateDirectory(sourceDirectory);
        var first = Path.Combine(sourceDirectory, "first.md");
        var second = Path.Combine(sourceDirectory, "fallback-name.md");
        await File.WriteAllTextAsync(first,
            "---\ntitle: Imported title\nstate: Todo\nepic:\n  id: PLAT-3\ntags: [one, two]\n---\nBody\n");
        await File.WriteAllTextAsync(second, "# Heading title\n\nMore\n");
        var request = new LocalMarkdownImportRequest(
            [sourceDirectory],
            false,
            false,
            false,
            true,
            new Dictionary<string, string> { ["status"] = "state" },
            null);

        var dryRun = await backend.ImportAsync(config, request, CancellationToken.None);
        Assert.Equal([1, 2], dryRun.Items.Select(item => item.Id));
        Assert.Empty(Directory.EnumerateFiles(Path.Combine(StoreRoot, "items"), "*.md"));

        var imported = await backend.ImportAsync(config, request with { DryRun = false }, CancellationToken.None);
        Assert.Equal(2, imported.Items.Count);
        Assert.True(File.Exists(first));
        var importedTitleItem = imported.Items.Single(item => item.Title == "Imported title");
        var detail = await backend.GetAsync(config, new WorkItemId($"local:{importedTitleItem.Id}"), CancellationToken.None);
        Assert.Equal("Imported title", detail!.Title);
        Assert.Equal("PLAT-3", detail.EffectiveFields["epic"].GetProperty("id").GetString());
        Assert.Equal(2, detail.EffectiveFields["tags"].GetArrayLength());

        var third = Path.Combine(sourceDirectory, "third.md");
        await File.WriteAllTextAsync(third, "No heading");
        var moved = await backend.ImportAsync(
            config,
            request with { Paths = [third], DryRun = false, Move = true, FieldMappings = new Dictionary<string, string>() },
            CancellationToken.None);
        Assert.Equal(3, moved.Items.Single().Id);
        Assert.False(File.Exists(third));
        Assert.Equal("third", (await backend.GetAsync(config, new WorkItemId("local:3"), CancellationToken.None))!.Title);
    }

    [Fact]
    public async Task Import_validation_is_atomic_and_loader_suggests_import()
    {
        var backend = new LocalMarkdownTrackerBackend(new FakeIdentity("worker-a"), new FakeClock(DateTimeOffset.UtcNow));
        var config = Config();
        await backend.InitializeAsync(config, false, CancellationToken.None);
        var sourceDirectory = Path.Combine(directory, "source");
        Directory.CreateDirectory(sourceDirectory);
        await File.WriteAllTextAsync(Path.Combine(sourceDirectory, "good.md"), "# Good");
        await File.WriteAllTextAsync(Path.Combine(sourceDirectory, "bad.md"), "---\nstatus: Unknown\n---\n# Bad");

        var importError = await Assert.ThrowsAsync<TrackerException>(() => backend.ImportAsync(
            config,
            new LocalMarkdownImportRequest([sourceDirectory], false, false, false, false,
                new Dictionary<string, string>(), null),
            CancellationToken.None));
        Assert.Contains("bad.md", importError.Message);
        Assert.Contains("status", importError.Message);
        Assert.Contains("Unknown", importError.Message);
        Assert.Empty(Directory.EnumerateFiles(Path.Combine(StoreRoot, "items"), "*.md"));

        var unmanaged = Path.Combine(StoreRoot, "items", "notes.md");
        await File.WriteAllTextAsync(unmanaged, "notes");
        var loaderError = await Assert.ThrowsAsync<TrackerException>(() => backend.ListAsync(
            config,
            new ListWorkItemsRequest(null, null),
            CancellationToken.None));
        Assert.Contains("wrighty import", loaderError.Message);
        Assert.Contains(unmanaged, loaderError.Message);
    }

    [Fact]
    public async Task Import_rejects_invalid_paths_mappings_reserved_fields_and_priority()
    {
        var backend = new LocalMarkdownTrackerBackend(new FakeIdentity("worker-a"), new FakeClock(DateTimeOffset.UtcNow));
        var config = Config();
        await backend.InitializeAsync(config, false, CancellationToken.None);
        var empty = Path.Combine(directory, "empty");
        var source = Path.Combine(directory, "source");
        Directory.CreateDirectory(empty);
        Directory.CreateDirectory(source);
        var textFile = Path.Combine(source, "note.txt");
        var priorityFile = Path.Combine(source, "priority.md");
        var reservedFile = Path.Combine(source, "reserved.md");
        await File.WriteAllTextAsync(textFile, "not markdown");
        await File.WriteAllTextAsync(priorityFile, "---\npriority: Unknown\n---\n# Priority");
        await File.WriteAllTextAsync(reservedFile, "---\nwrighty: reserved\n---\n# Reserved");

        await AssertImportError([], new Dictionary<string, string>(), "At least one");
        await AssertImportError([source], new Dictionary<string, string> { ["owner"] = "author" }, "Invalid --map");
        await AssertImportError([empty], new Dictionary<string, string>(), "No Markdown files");
        await AssertImportError([Path.Combine(directory, "missing.md")], new Dictionary<string, string>(), "does not exist");
        await AssertImportError([textFile], new Dictionary<string, string>(), "is not Markdown");
        await AssertImportError([priorityFile], new Dictionary<string, string>(), "priority");
        await AssertImportError([reservedFile], new Dictionary<string, string>(), "reserved for Wrighty");

        var insideStore = Path.Combine(StoreRoot, "incoming.md");
        await File.WriteAllTextAsync(insideStore, "# Inside");
        await AssertImportError([insideStore], new Dictionary<string, string>(), "outside the Local Markdown store");

        async Task AssertImportError(
            IReadOnlyList<string> paths,
            IReadOnlyDictionary<string, string> mappings,
            string expected)
        {
            var exception = await Assert.ThrowsAsync<TrackerException>(() => backend.ImportAsync(
                config,
                new LocalMarkdownImportRequest(paths, false, false, false, false, mappings, null),
                CancellationToken.None));
            Assert.Contains(expected, exception.Message);
        }
    }

    [Fact]
    public async Task Import_recursive_archive_force_status_and_source_date_are_applied()
    {
        var clock = new FakeClock(new DateTimeOffset(2026, 7, 16, 10, 0, 0, TimeSpan.Zero));
        var backend = new LocalMarkdownTrackerBackend(new FakeIdentity("worker-a"), clock);
        var config = Config();
        await backend.InitializeAsync(config, false, CancellationToken.None);
        var nested = Path.Combine(directory, "source", "nested");
        Directory.CreateDirectory(nested);
        var source = Path.Combine(nested, "dated.MD");
        await File.WriteAllTextAsync(source,
            "---\nstatus: Invalid but overridden\nrank: P1\ndate: 2020-01-02T03:04:05Z\n---\n# Dated");

        var result = await backend.ImportAsync(
            config,
            new LocalMarkdownImportRequest(
                [Path.Combine(directory, "source")],
                true,
                true,
                false,
                false,
                new Dictionary<string, string> { ["priority"] = "rank" },
                "Done"),
            CancellationToken.None);

        var item = Assert.Single(result.Items);
        Assert.Equal("Done", item.Status);
        Assert.Equal("P1", item.Priority);
        Assert.True(File.Exists(Path.Combine(StoreRoot, "archive", "001-dated.md")));
        var content = await File.ReadAllTextAsync(item.DestinationPath);
        Assert.Contains("createdAt: 2020-01-02T03:04:05.0000000+00:00", content);
        Assert.Contains("rank: P1", content);
    }

    [Fact]
    public async Task Update_can_archive_and_rejects_archived_items()
    {
        var backend = new LocalMarkdownTrackerBackend(new FakeIdentity("worker-a"), new FakeClock(DateTimeOffset.UtcNow));
        var config = Config();
        await backend.InitializeAsync(config, false, CancellationToken.None);
        var created = await backend.CreateAsync(
            config,
            new CreateWorkItemOperation(new CreateWorkItemRequest("Archive through update", "Body", "Todo", null), false),
            CancellationToken.None);
        await backend.TryClaimAsync(config, created.Id, AgentExecutionContext.None, CancellationToken.None);

        var archived = await backend.UpdateAsync(
            config,
            created.Id,
            new UpdateWorkItemOperation(WorkItemPatch.StatusOnly("Done"), true),
            CancellationToken.None);

        Assert.True(archived.Item.Archived);
        Assert.Contains("archived", archived.ChangedFields);
        var exception = await Assert.ThrowsAsync<TrackerException>(() => backend.UpdateAsync(
            config,
            created.Id,
            new UpdateWorkItemOperation(WorkItemPatch.StatusOnly("Todo"), false),
            CancellationToken.None));
        Assert.Equal("WORK_ITEM_ARCHIVED", exception.Code);
    }

    [Fact]
    public async Task Dashboard_snapshot_reads_claims_once_and_tracks_exact_document_changes()
    {
        var clock = new FakeClock(new DateTimeOffset(2026, 7, 14, 10, 0, 0, TimeSpan.Zero));
        var backend = new LocalMarkdownTrackerBackend(new FakeIdentity("worker-a"), clock);
        var config = Config();
        await backend.InitializeAsync(config, false, CancellationToken.None);
        var first = await backend.CreateAsync(
            config,
            new CreateWorkItemOperation(
                new CreateWorkItemRequest("First", "Body", "Todo", "P1"),
                false),
            CancellationToken.None);
        await backend.CreateAsync(
            config,
            new CreateWorkItemOperation(
                new CreateWorkItemRequest("Second", "Body", "In Progress", "P0"),
                false),
            CancellationToken.None);
        await backend.TryClaimAsync(
            config,
            first.Id,
            new AgentExecutionContext("codex", "session-1", AgentContextSource.ExplicitOption),
            CancellationToken.None);

        var original = await backend.GetDashboardAsync(
            config,
            ArchiveScope.Active,
            CancellationToken.None);

        Assert.Equal(["Todo", "In Progress", "Done"], original.Statuses);
        Assert.Equal(2, original.Items.Count);
        Assert.Equal(ClaimOwnershipState.OwnedByCurrent, original.Items[0].Claim.State);
        Assert.Equal("agent", original.Items[0].Claim.ClaimantKind);
        Assert.Equal("codex", original.Items[0].Claim.AgentType);
        Assert.Equal("session-1", original.Items[0].Claim.SessionId);
        Assert.Matches("^[0-9a-f]{64}$", original.Revision);

        clock.UtcNow = clock.UtcNow.AddMinutes(61);
        var expired = await backend.GetDashboardAsync(config, ArchiveScope.Active, CancellationToken.None);
        Assert.Equal(ClaimOwnershipState.Unclaimed, expired.Items[0].Claim.State);
        Assert.NotEqual(original.Revision, expired.Revision);

        var path = Path.Combine(StoreRoot, "items", "001-first.md");
        await File.AppendAllTextAsync(path, "\n");
        var changed = await backend.GetDashboardAsync(
            config,
            ArchiveScope.Active,
            CancellationToken.None);

        Assert.NotEqual(original.Revision, changed.Revision);
    }

    [Fact]
    public async Task Expected_revision_prevents_stale_update_and_preserves_external_frontmatter()
    {
        var backend = new LocalMarkdownTrackerBackend(
            new FakeIdentity("worker-a"),
            new FakeClock(new DateTimeOffset(2026, 7, 14, 10, 0, 0, TimeSpan.Zero)));
        var config = Config();
        await backend.InitializeAsync(config, false, CancellationToken.None);
        var created = await backend.CreateAsync(
            config,
            new CreateWorkItemOperation(
                new CreateWorkItemRequest("Conflict", "Original body", "Todo", null),
                false),
            CancellationToken.None);
        await backend.TryClaimAsync(
            config,
            created.Id,
            AgentExecutionContext.None,
            CancellationToken.None);
        var loaded = await backend.GetEditableAsync(config, created.Id, CancellationToken.None);
        var path = Path.Combine(StoreRoot, "items", "001-conflict.md");
        var content = await File.ReadAllTextAsync(path);
        await File.WriteAllTextAsync(
            path,
            content.Replace("claimEpoch: 1", "claimEpoch: 1\nexternalField: retained"));

        var conflict = await Assert.ThrowsAsync<TrackerException>(() => backend.UpdateAsync(
            config,
            created.Id,
            new UpdateWorkItemOperation(
                new WorkItemPatch(default, OptionalValue<string>.From("Stale body"), default, default),
                false,
                loaded.Revision),
            CancellationToken.None));

        Assert.Equal("UPDATE_CONFLICT", conflict.Code);
        Assert.Contains("Original body", await File.ReadAllTextAsync(path));

        var current = await backend.GetEditableAsync(config, created.Id, CancellationToken.None);
        var updated = await backend.UpdateAsync(
            config,
            created.Id,
            new UpdateWorkItemOperation(
                new WorkItemPatch(default, OptionalValue<string>.From("Current body"), default, default),
                false,
                current.Revision),
            CancellationToken.None);

        Assert.Equal("Current body", updated.Item.Body);
        Assert.Contains("externalField: retained", await File.ReadAllTextAsync(path));
    }

    [Fact]
    public async Task Tracker_service_applies_archive_on_status_atomically()
    {
        var backend = new LocalMarkdownTrackerBackend(
            new FakeIdentity("worker-a"),
            new FakeClock(new DateTimeOffset(2026, 7, 14, 10, 0, 0, TimeSpan.Zero)));
        var config = Config() with { Archive = new ArchiveConfig { OnStatuses = ["Done"] } };
        await backend.InitializeAsync(config, false, CancellationToken.None);
        var service = new TrackerService(new TrackerBackendRegistry([backend]));
        var created = await service.CreateAsync(
            config,
            new CreateWorkItemRequest("Finish", string.Empty, "Todo", null),
            CancellationToken.None);
        await service.ClaimAsync(config, created.Id, AgentExecutionContext.None, CancellationToken.None);

        var result = await service.UpdateAsync(
            config,
            created.Id,
            WorkItemPatch.StatusOnly("Done"),
            CancellationToken.None);

        Assert.True(result.Item.Archived);
        Assert.Contains("archived", result.ChangedFields);
        Assert.True(File.Exists(Path.Combine(StoreRoot, "archive", "001-finish.md")));
    }

    [Fact]
    public async Task ListAsync_reads_500_physical_items_and_output_remains_compact()
    {
        var backend = new LocalMarkdownTrackerBackend(
            new FakeIdentity("worker-a"),
            new FakeClock(new DateTimeOffset(2026, 7, 14, 10, 0, 0, TimeSpan.Zero)));
        var config = Config();
        await backend.InitializeAsync(config, false, CancellationToken.None);

        for (var number = 1; number <= 500; number++)
        {
            var archived = number % 10 == 0;
            var target = Path.Combine(
                StoreRoot,
                archived ? "archive" : "items",
                $"{number:000}-scale-item-{number:000}.md");
            var status = number % 5 == 0 ? "Done" : "Todo";
            await File.WriteAllTextAsync(target, $$"""
                ---
                title: Scale item {{number:000}}
                status: {{status}}
                priority: P{{number % 4}}
                createdAt: 2026-07-14T10:00:00.0000000+00:00
                updatedAt: 2026-07-14T10:00:00.0000000+00:00
                claimEpoch: 0
                ---
                Body payload {{number}}
                """);
        }

        var all = await backend.ListAsync(
            config,
            new ListWorkItemsRequest(null, null, ArchiveScope.All),
            CancellationToken.None);
        var active = await backend.ListAsync(
            config,
            new ListWorkItemsRequest(null, null, ArchiveScope.Active),
            CancellationToken.None);
        var archivedItems = await backend.ListAsync(
            config,
            new ListWorkItemsRequest(null, null, ArchiveScope.Archived),
            CancellationToken.None);
        var done = await backend.ListAsync(
            config,
            new ListWorkItemsRequest("Done", null, ArchiveScope.All),
            CancellationToken.None);

        Assert.Equal(500, all.Count);
        Assert.Equal(450, active.Count);
        Assert.Equal(50, archivedItems.Count);
        Assert.Equal(100, done.Count);
        Assert.Equal(Enumerable.Range(1, 500), all.Select(item =>
            int.Parse(item.Id.Value["local:".Length..])).Order());

        var compactOutput = new StringWriter();
        var compactWriter = new OutputWriter(compactOutput, new StringWriter());
        await compactWriter.WriteItemsAsync(all, compact: true, json: false, FormatLocalId);
        var lines = compactOutput.ToString().Split(
            Environment.NewLine,
            StringSplitOptions.RemoveEmptyEntries);
        Assert.Equal(500, lines.Length);
        Assert.All(lines, line =>
        {
            Assert.StartsWith("#", line);
            Assert.DoesNotContain("Body payload", line);
        });

        var jsonOutput = new StringWriter();
        var jsonWriter = new OutputWriter(jsonOutput, new StringWriter());
        await jsonWriter.WriteItemsAsync(all, compact: false, json: true, FormatLocalId);
        using var document = JsonDocument.Parse(jsonOutput.ToString());
        var jsonItems = document.RootElement.GetProperty("result").EnumerateArray().ToArray();
        Assert.Equal(500, jsonItems.Length);
        Assert.All(jsonItems, item =>
        {
            Assert.False(item.TryGetProperty("body", out _));
            Assert.False(item.TryGetProperty("projectItemId", out _));
        });

        static string FormatLocalId(WorkItemId id) => $"#{id.Value["local:".Length..]}";
    }

    [Fact]
    public async Task Concurrent_cli_processes_allocate_unique_ids()
    {
        Directory.CreateDirectory(directory);
        var config = Config();
        await new TrackerConfigLoader().SaveAsync(
            Path.Combine(directory, TrackerConfigLoader.FileName),
            config,
            CancellationToken.None);
        var initializedConfig = await new TrackerConfigLoader().LoadAsync(directory, CancellationToken.None);
        await new LocalMarkdownTrackerBackend(
                new FakeIdentity("worker-a"),
                new FakeClock(DateTimeOffset.UtcNow))
            .InitializeAsync(initializedConfig, false, CancellationToken.None);

        var processes = Enumerable.Range(1, 12).Select(StartCreate).ToArray();
        var results = await Task.WhenAll(processes.Select(CompleteAsync));

        Assert.All(results, result => Assert.Equal(0, result.ExitCode));
        var files = Directory.GetFiles(Path.Combine(StoreRoot, "items"), "*.md");
        Assert.Equal(12, files.Length);
        Assert.Equal(
            Enumerable.Range(1, 12),
            files.Select(path => int.Parse(Path.GetFileName(path).Split('-', 2)[0]))
                .Order());

        Process StartCreate(int number)
        {
            var start = new ProcessStartInfo("dotnet")
            {
                WorkingDirectory = directory,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false
            };
            start.ArgumentList.Add(typeof(CliApplication).Assembly.Location);
            start.ArgumentList.Add("create");
            start.ArgumentList.Add("--title");
            start.ArgumentList.Add($"Concurrent item {number}");
            var process = new Process { StartInfo = start };
            process.Start();
            return process;
        }

        static async Task<ProcessResult> CompleteAsync(Process process)
        {
            var output = process.StandardOutput.ReadToEndAsync();
            var error = process.StandardError.ReadToEndAsync();
            await process.WaitForExitAsync();
            var result = new ProcessResult(process.ExitCode, await output, await error);
            process.Dispose();
            return result;
        }
    }

    [Fact]
    public async Task Plain_init_outside_git_defaults_to_local_markdown()
    {
        Directory.CreateDirectory(directory);
        var start = new ProcessStartInfo("dotnet")
        {
            WorkingDirectory = directory,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false
        };
        start.ArgumentList.Add(typeof(CliApplication).Assembly.Location);
        start.ArgumentList.Add("init");
        start.ArgumentList.Add("--json");
        using var process = new Process { StartInfo = start };
        process.Start();
        var output = process.StandardOutput.ReadToEndAsync();
        var error = process.StandardError.ReadToEndAsync();
        await process.WaitForExitAsync();

        Assert.Equal(0, process.ExitCode);
        Assert.Empty(await error);
        Assert.Contains("\"backend\": \"local-markdown\"", await output);
        Assert.True(File.Exists(Path.Combine(directory, TrackerConfigLoader.FileName)));
        Assert.True(Directory.Exists(Path.Combine(StoreRoot, "items")));
        Assert.True(Directory.Exists(Path.Combine(StoreRoot, "archive")));
        Assert.False(File.Exists(Path.Combine(StoreRoot, ".gitignore")));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Init_inside_git_creates_idempotent_local_gitignore(bool worktreeMarkerFile)
    {
        Directory.CreateDirectory(directory);
        var gitMarker = Path.Combine(directory, ".git");
        if (worktreeMarkerFile)
        {
            await File.WriteAllTextAsync(gitMarker, "gitdir: /example/worktree");
        }
        else
        {
            Directory.CreateDirectory(gitMarker);
        }
        var backend = new LocalMarkdownTrackerBackend(
            new FakeIdentity("worker-a"),
            new FakeClock(DateTimeOffset.UtcNow));
        var config = Config();

        var first = await backend.InitializeAsync(config, false, CancellationToken.None);
        var path = Path.Combine(StoreRoot, ".gitignore");

        Assert.Contains("created local Wrighty .gitignore", first.Actions);
        Assert.Equal(
            $"# Wrighty runtime state{Environment.NewLine}" +
            $"/.lock{Environment.NewLine}" +
            $".*.tmp{Environment.NewLine}",
            await File.ReadAllTextAsync(path));

        var second = await backend.InitializeAsync(config, false, CancellationToken.None);

        Assert.False(second.Changed);
        Assert.Empty(second.Actions);
    }

    [Fact]
    public async Task Init_preserves_existing_gitignore_and_appends_missing_rules()
    {
        Directory.CreateDirectory(Path.Combine(directory, ".git"));
        Directory.CreateDirectory(StoreRoot);
        var path = Path.Combine(StoreRoot, ".gitignore");
        await File.WriteAllTextAsync(path, $"custom-entry{Environment.NewLine}/.lock{Environment.NewLine}");
        var backend = new LocalMarkdownTrackerBackend(
            new FakeIdentity("worker-a"),
            new FakeClock(DateTimeOffset.UtcNow));

        var result = await backend.InitializeAsync(Config(), false, CancellationToken.None);
        var content = await File.ReadAllTextAsync(path);

        Assert.Contains("updated local Wrighty .gitignore", result.Actions);
        Assert.StartsWith($"custom-entry{Environment.NewLine}/.lock{Environment.NewLine}", content);
        Assert.Equal(1, content.Split("/.lock", StringSplitOptions.None).Length - 1);
        Assert.Contains($"# Wrighty runtime state{Environment.NewLine}.*.tmp", content);
    }

    [Fact]
    public async Task Check_does_not_generate_missing_local_gitignore()
    {
        Directory.CreateDirectory(Path.Combine(directory, ".git"));
        var backend = new LocalMarkdownTrackerBackend(
            new FakeIdentity("worker-a"),
            new FakeClock(DateTimeOffset.UtcNow));
        var config = Config();
        await backend.InitializeAsync(config, false, CancellationToken.None);
        var path = Path.Combine(StoreRoot, ".gitignore");
        File.Delete(path);

        var result = await backend.InitializeAsync(config, true, CancellationToken.None);

        Assert.False(result.Changed);
        Assert.False(File.Exists(path));
    }

    private string StoreRoot => Path.Combine(directory, ".wrighty");

    private TrackerConfig Config() => new()
    {
        Backend = "local-markdown",
        SourcePath = Path.Combine(directory, TrackerConfigLoader.FileName),
        LocalMarkdown = new LocalMarkdownBackendConfig()
    };

    public void Dispose()
    {
        if (Directory.Exists(directory))
        {
            Directory.Delete(directory, true);
        }
    }

    private sealed class FakeIdentity(string identity) : IWorkerIdentityProvider
    {
        public Task<string> GetIdentityAsync(CancellationToken cancellationToken) =>
            Task.FromResult(identity);
    }

    private sealed class FakeClock(DateTimeOffset utcNow) : IClock
    {
        public DateTimeOffset UtcNow { get; set; } = utcNow;
    }

    private sealed record ProcessResult(int ExitCode, string Output, string Error);
}
