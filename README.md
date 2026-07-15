# Wrighty

Wrighty coordinates work between developers and locally running
[AI coding agents supported by Wrighty](#supported-ai-agents). It is designed for agents operating
from terminals, IDEs, or desktop apps that need to discover, claim, update, and finish shared work
through a predictable CLI.

Pluggable local Markdown and GitHub backends provide compact listing, durable backend-neutral IDs,
deterministic claims, claim-aware editing, and archiving.

Wrighty is useful when:

- one or more local AI agents need a shared, claim-aware backlog while working in a project;
- developers want that backlog stored as human-readable files alongside the source code;
- developers and agents working from different machines need to coordinate through GitHub without
  claiming the same work; or
- scripts and agent workflows need a stable CLI with compact and JSON output instead of
  backend-specific tracker operations.

Day-to-day commands are backend-neutral: choose local Markdown for work on one shared filesystem
or GitHub when coordination spans machines.

## Install

On macOS ARM64 or Linux x64/ARM64, install Wrighty from the shared Highbyte Homebrew tap:

```shell
brew install highbyte/tap/wrighty
```

On Windows x64/ARM64, add the shared Highbyte Scoop bucket once and install Wrighty:

```powershell
scoop bucket add highbyte https://github.com/highbyte/scoop-bucket
scoop install highbyte/wrighty
```

Verify the installation:

```shell
wrighty --help
```

## Configure

### Choose a backend

`wrighty init` can bootstrap a tracker from any directory. A Git checkout is optional. Select a
backend explicitly when desired. Without `--backend`, initialization uses GitHub when an `origin`
GitHub remote is detected and otherwise creates a local Markdown tracker.

The local Markdown backend coordinates processes sharing one filesystem. Independent Git clones,
Git pushes, Dropbox/OneDrive synchronization, and similar replication do **not** provide
distributed claim arbitration. Use the GitHub backend when agents on different computers must
coordinate. The local Markdown backend requires no external service or additional executable.

### Initialize the Local Markdown backend

Create a tracker using the default `.wrighty/` path and workflow values:

```shell
wrighty init --backend local-markdown
```

Choose another path and workflow values during first-time bootstrap when needed:

```shell
wrighty init --backend local-markdown \
  --local-path work-items \
  --status Todo --status "In Progress" --status Done \
  --priority P0 --priority P1 --priority P2
```

### Initialize the GitHub backend

The GitHub backend requires the [GitHub CLI](https://cli.github.com/) installed and authenticated
with repository and Projects permissions. Wrighty delegates authentication and API transport to
`gh`; it never reads or stores a GitHub token itself.

Inside a checkout whose `origin` is a GitHub repository:

```shell
wrighty init
```

From another directory, specify the repository explicitly:

```shell
wrighty init --repository highbyte/wrighty
```

When no Project is selected, `init` reuses one exact Project named
`Wrighty - OWNER/REPOSITORY` or creates it. Select an existing Project explicitly
when needed:

```shell
wrighty init \
  --repository highbyte/wrighty \
  --project-owner highbyte \
  --project-number 10
```

Use `--project-title` to choose a different title during first-time bootstrap, `--remote` to
discover from a remote other than `origin`, and `--no-link-repository` to opt out of repository
linking. Explicit repository and Project options never depend on the current directory being a Git
repository.

For same-owner repositories, initialization links the Project from the repository's Projects tab.
GitHub does not permit this link when Project and repository owners differ; the operational tracker
configuration can still identify them separately.

For GitHub, `wrighty init` creates **Current agent type** as a single-select field and
**Current session ID** and **Creation attempt ID** as text fields, repairs missing standard agent
options, and refreshes the local node-ID cache. Existing compatible fields are reused. Duplicate
names or incompatible field types are reported without being changed.

### Configuration file

The CLI searches the current directory and its parents for `.wrighty.json`. During
first-time setup it writes the file in the current directory unless `--config` is supplied. The
file contains no credentials and should normally be committed so different machines use the same
tracker configuration. For the GitHub backend, authentication remains in `gh`.

Complete configuration examples are available for the
[GitHub backend](.wrighty.github.example.json) and the
[local Markdown backend](.wrighty.local-markdown.example.json). Copy the relevant file
to `.wrighty.json` and replace its example values when configuring manually. Both
examples show every setting supported by that backend and enable automatic archiving when Status
becomes `Done`; use an empty `archive.onStatuses` array to disable that behavior. Running
`wrighty init` is preferred because it also creates or validates the backend resources.

`defaultPickFrom`, `defaultPickTo`, and `defaultFinishTo` control the composite agent workflows.
`finish` uses `defaultFinishTo` unless `--status` is supplied.

### Validate configuration

Initialization is idempotent. With an existing valid configuration, matching target options act
as assertions and conflicting values fail before any write. `--project-title` and `--remote` are
first-bootstrap options. An invalid existing configuration is reported and never overwritten.

Initialize or validate the selected backend after creating or changing the configuration:

```shell
wrighty init
wrighty init --check
```

For GitHub, `wrighty init --check` performs authoritative, read-only repository, Project-link,
access, and schema validation without changing GitHub, the configuration, or the local cache.

## Work-item IDs and creation

The common CLI treats IDs as opaque backend references. The GitHub backend emits durable IDs in
the form `github:owner/repository#42` and accepts all of these equivalent inputs:

```text
42
#42
owner/repository#42
github:owner/repository#42
https://github.com/owner/repository/issues/42
```

Shorthand resolves within the configured repository. Explicit references to another repository
are rejected. Human and compact output use `#42`; JSON uses both canonical `id` and `displayId`.
GitHub node IDs and Project item IDs are internal and never appear in the public JSON contract.

The local backend emits `local:42`, accepts `42`, `#42`, and its generated filename/path, and uses
`#42` for human output. Canonical IDs never contain an absolute path or title slug.

For GitHub, `create` creates a real issue, adds it to the configured Project, and assigns the
requested status and priority. Locally it atomically allocates the next numeric prefix and writes
one Markdown document. An omitted status uses `defaultPickFrom`. Use `--body-file -` to read
multiline markdown from stdin. `--body` and `--body-file` are mutually exclusive.

Every create has a Creation attempt ID. Supply one explicitly when an agent must recover after a
hard interruption:

```shell
wrighty create --creation-attempt-id 019f5c485c2b7862aeac80eb638a7b5c \
  --title "Example" --body-file description.md --priority P1
```

UUID `N` and `D` forms are accepted and normalized to 32 lowercase hexadecimal characters. When
the option is omitted, the CLI generates an ID and reports it in human and JSON output. Repeating
the same command with the same ID returns the original item with disposition `resumed`. Local
Markdown stores the ID and request hash in YAML frontmatter.

Agents should generate the ID before create so it survives an ambiguous creation result:

```shell
wrighty creation-attempt new --json
wrighty create --creation-attempt-id ID --title "Example" --json
```

GitHub cannot make issue creation and Project updates transactional. Before allocating an issue,
the backend creates a temporary `sit-create-<attempt-id>` repository label and includes it in the
issue-creation request. This label bridges a lost HTTP response. The durable ID is then written to
the Project's **Creation attempt ID** field. After Status, Priority, optional archive, and the final
read have succeeded, the temporary label is removed from the issue and deleted from the repository.
The issue body remains exactly user-authored and contains no hidden tracker marker.

Retry-safe GitHub creation requires repository permission to apply labels during issue creation.
If that cannot be established, `create` returns `GITHUB_PERMISSION_REQUIRED` before allocating an
issue. A later-stage failure returns `PARTIAL_CREATE` with the canonical ID, Creation attempt ID,
and failed stage. Retry the same request with the same Creation attempt ID; do not generate a new
one. Duplicate evidence is reported without closing, deleting, or otherwise modifying either issue.

The GitHub Project is authoritative tracked-item state. If someone removes a completed item from
the Project after its temporary label has been cleaned up, the Creation attempt ID is no longer
discoverable from that repository issue alone.

## Moving and editing

`move` and `edit` require an active claim owned by the current tracker installation. They never
claim, renew, release, or transfer ownership automatically:

```shell
wrighty claim 42
wrighty move 42 "In Progress"
wrighty edit 42 --title "Revised title" --priority P0
wrighty edit 42 --body-file work-item.md --status Done
wrighty edit 42 --clear-priority
wrighty release 42
```

`edit` supports title, markdown body, status, and priority. Use `--body-file -` to read the new
body from standard input. An empty `--body` clears the body, while `--clear-priority` clears the
configured Project priority. `move ID STATUS` uses the same mutation pipeline as
`edit ID --status STATUS`.

All requested values are validated before the first write. GitHub cannot atomically update an
issue and several Project fields, so the tool applies issue title/body first, priority second,
and workflow status last. Claim ownership is checked again immediately before every physical
write. A failure after any successful field returns `PARTIAL_UPDATE` with applied and pending
fields; successful writes are not rolled back and the claim is retained. Retrying the same patch
while still owning the claim is convergent because already matching fields are skipped.

An edit whose values already match succeeds without mutating GitHub, but ownership is still
required. GitHub leases are best-effort coordination rather than fenced locks: ownership checks
narrow, but cannot eliminate, the expired-lease-mid-write race.

The local backend performs ownership verification and document replacement under the same store
lock, so cooperating local processes cannot write through an expired claim after another process
has acquired it.

Complete claimed work with `wrighty finish ID --json`. It moves the item to `defaultFinishTo`
(or `--status`), honors archive-on-status, and releases the claim. A retry after success returns
`already-finished`; `PARTIAL_FINISH` instructs the caller to retry the same command.

## Archiving

Archived is a lifecycle state separate from workflow Status. Moving an item to `Done` leaves it
active by default so humans can review completed work. Archive explicitly when it should disappear
from normal lists and `pick`:

```shell
wrighty claim 42
wrighty archive 42
wrighty list --archived
wrighty unarchive 42
```

Archiving an active item requires the current claim and releases that claim. Retrying archive is
an idempotent no-op. Unarchive restores the previous Status and Priority and requires no active
claim. `get` finds both states; normal `list` and `pick` use active items only.

Locally, archive moves the Markdown document between `items/` and `archive/` atomically. On GitHub,
it uses the native Projects v2 archived-item state; it neither closes the issue nor removes it from
the Project.

Automatic archiving is opt-in and applies to statuses written through this CLI:

```json
{
  "archive": {
    "onStatuses": ["Done"]
  }
}
```

The default is an empty list. GitHub status changes made externally require a GitHub built-in
workflow if they should also archive automatically.

`claim` and `pick` automatically attach an agent type and session ID when invoked by a current
Codex, Claude Code, or GitHub Copilot CLI session. Use explicit values on surfaces that do not
expose a supported signal:

```shell
wrighty claim 42 --agent-type codex --session-id 019f5c48-5c2b-7862-aeac-80eb638a7b5c
wrighty pick --no-agent-context
```

The equivalent environment variables are `WRIGHTY_AGENT_TYPE`,
`WRIGHTY_SESSION_ID`, and `WRIGHTY_NO_AGENT_CONTEXT`. Explicit CLI
options take precedence, followed by tracker-specific environment variables and automatic
detection. Conflicting vendor signals are never guessed; the command continues with a warning.

The project can also be packed and installed as a .NET tool whose command is `wrighty`:

```shell
dotnet pack src/Highbyte.Wrighty.Cli
dotnet tool install --global --add-source src/Highbyte.Wrighty.Cli/bin/Release Highbyte.Wrighty.Tool
```

## Claims

On GitHub, a claim is a versioned, machine-readable issue comment. Project fields are not used for
claims because their writes are last-writer-wins. Competing clients create comments and resolve the
earliest active server-created comment as the winner.

Locally, current claim metadata lives in work-item frontmatter. Claim comparison and replacement
occur while holding the store-wide lock, so exactly one cooperating local process wins.

Claims may also contain optional `agentType` and `sessionId` correlation metadata. These fields
are informational and never affect ownership, arbitration, release permission, or cleanup.
Session IDs are published into comments with the same visibility as their issue. Use
`--no-agent-context` if that correlation metadata should not be published.

The winning claim is projected into the **Current agent type** and **Current session ID** Project
fields. Acquisition and `AlreadyOwned` results reconcile the fields; release clears them. A
claim made with `--no-agent-context` also clears them. The issue comment remains authoritative:
Project field writes are display-only and a projection failure never rolls back a claim or
changes its owner. An expired claim can remain visible in the Project fields until a later
claim-related operation reconciles the item.

Inactive claim history is bounded by `claimHistoryLimit` in `.wrighty.json`. The
default is `10` comments per issue; set it to `0` to remove inactive claim comments immediately.
Cleanup is best-effort and never changes active claims.

See [Claim protocol v1](docs/design/claim-protocol-v1.md) for the wire format and resolution
rules.

## Agent skills

The package contains a narrow, explicit-opt-in `wrighty` Agent Skill shared by Codex,
Claude Code, and GitHub Copilot:

```shell
wrighty skill install --agent codex
wrighty skill install --agent claude
wrighty skill install --agent copilot
wrighty skill install --agent all
```

Project scope is the default. It resolves to the Git root when available and otherwise the current
directory. Use `--project-dir PATH` to choose another project or `--scope user` for a personal
installation. Codex and Copilot share `.agents/skills/wrighty`; Claude uses
`.claude/skills/wrighty`. An `all` installation creates those two physical copies.

Validate or update installed mechanics with:

```shell
wrighty skill check --agent all
wrighty skill check --agent all --check-tracker
wrighty skill update --agent all
```

Update copies assets bundled with the running `wrighty`; it never downloads skill content. It
preserves a customized `description`. Modified tool-owned mechanics produce `SKILL_MODIFIED`
unless `--force` is explicit. All skill operations support `--json`.

### Supported AI agents

Install the skill for the coding agent first. The table lists the currently supported agent
surfaces and how to invoke Wrighty:

| Coding agent | Activation | Example |
|---|---|---|
| Codex Desktop | Explicit only | `/wrighty Pick the next available item, implement it, run its tests, and finish it.` or the equivalent `$wrighty ...` |
| Codex CLI or IDE extension | Explicit only | `$wrighty Pick the next available item, implement it, run its tests, and finish it.` |
| Claude Code | Explicit only | `/wrighty Pick the next available item, implement it, run its tests, and finish it.` |
| GitHub Copilot CLI or an IDE surface that exposes skill commands | Automatic or explicit | `/wrighty Work on tracker item #42 and finish it when complete.` |
| GitHub Copilot coding agent or another surface without a skill slash command | Automatic, or named in the prompt | `Use the wrighty skill to work on tracker item #42 and finish it when complete.` |

Codex Desktop accepts both `/wrighty` and `$wrighty` as explicit
invocations. Codex also exposes installed skills through `/skills`; selecting this skill inserts
its `$wrighty` mention. The `$` form is the portable explicit form across Codex
surfaces. The Codex installation sets `allow_implicit_invocation: false`, and the Claude
installation sets `disable-model-invocation: true`. Consequently, neither agent should activate
this skill merely because a prompt happens to resemble tracker work. Use an explicit form shown
above.

Copilot may select the skill automatically by matching the prompt against the `description` in
`SKILL.md`. The bundled description is intentionally narrow. Prompts that explicitly mention
**Wrighty**, the **Wrighty CLI**, or a **tracker item** and ask to list, inspect,
create, pick, claim, edit, move, finish, archive, or release work are eligible. Generic requests
such as “work on issue 42”, “list GitHub issues”, “update the backlog”, or “finish this task” are
not intended to trigger it.

More examples:

```text
# Codex Desktop
/wrighty Pick the next available item and implement it.
$wrighty Work on tracker item #42. Inspect it before making changes.

# Codex CLI or IDE extension
$wrighty Work on tracker item #42. Inspect it before making changes.
$wrighty Create a tracker item titled "Add retry telemetry" with priority P1.

# Claude Code
/wrighty Pick the next available item and implement it.
/wrighty Archive tracker item #42.

# GitHub Copilot
/wrighty Show the available tracker items.
Use the wrighty skill to claim tracker item #42 and update its priority to P0.
```

Slash-command availability is a feature of the coding-agent surface, not of the Wrighty CLI. If a
Copilot surface does not expose `/wrighty`, name the skill in the prompt as in the
table. After installing or updating, use `wrighty skill check --agent AGENT --check-tracker` to
verify both the skill files and the `wrighty` executable.

The skill tells agents to mutate tracker state only through the CLI and branch on structured error
codes. A skill is guidance, not a sandbox; use host permissions or hooks when bypass prevention
must be mechanically enforced.

## Storage and version control

### Local Markdown backend

The default local setup creates:

```text
.wrighty/
├── items/
├── archive/
└── .lock
```

Local paths are resolved relative to `.wrighty.json`. The configured `items/` and `archive/`
directories contain the authoritative work-item content. Each item is a human-readable Markdown
file with YAML frontmatter and a filename such as `001-develop-login-feature.md`. The numeric
prefix is the identity; editing the title renames the file without changing `local:1`.

Frontmatter contains title, status, priority, timestamps, claim epoch, and optional current claim
metadata; the rest of the file is the Markdown body. Unknown frontmatter fields are preserved
during application updates. The store-wide lock coordinates processes sharing the same
filesystem.

Commit the authoritative work-item documents under the configured `items/` and `archive/`
directories; they preserve the backlog, completed work, and their history with the repository. Do
not ignore the entire local tracker directory.

When a local Markdown store is inside a Git worktree, a mutating `wrighty init` creates a
`.gitignore` in the tracker root with these rules:

```gitignore
# Wrighty runtime state
/.lock
.*.tmp
```

The rules ignore the store-wide runtime lock and interrupted atomic-write temporary files at any
level below the tracker root. Existing `.gitignore` content is preserved; initialization appends
only missing tracker rules. Repeated initialization is idempotent. Outside a Git worktree no
`.gitignore` is created, and `wrighty init --check` never creates or changes one. The generated
`.gitignore` should itself be committed.

If a parent `.gitignore` excludes the entire tracker directory, the nested rules cannot make its
work-item documents visible to Git; remove that parent exclusion. Git records and transports local
tracker state, but does not provide distributed claim or ID allocation. A solo developer using
multiple machines should finish or release claims, commit and push tracker changes, and pull before
mutating the tracker elsewhere. Teams or concurrent agents on different machines should use the
GitHub backend.

Active local claims are stored temporarily in item frontmatter, so claiming and releasing an item
changes its Markdown file. Prefer committing the item after `finish` or `release`, when transient
worker and session metadata has been removed.

### GitHub backend

Issues and Project fields are authoritative for the GitHub backend; no local work-item directory
is created. Only regenerable state is stored locally:

- opaque GitHub project, field, and option node IDs, including agent-context projection fields;
- a per-install UUID used to derive a privacy-preserving 12-character worker identity.

No GitHub work-item IDs, content, creation results, or claim state are cached locally. Invalid node
IDs are discarded and rediscovered once. The machine-local cache must not be committed.

Set `WRIGHTY_CACHE_DIR` to override the cache directory. This is useful for
isolating worker identities during integration tests; normal installations should leave it
unset.

## Development

The implementation is guided by the
[original design](docs/design/agent-facing-work-item-tracker-cli.md) and the related public
design documents in [`docs/design/`](docs/design/).

### Prerequisites

- [.NET 10 SDK](https://dotnet.microsoft.com/download/dotnet/10.0) for building and running the
  .NET test suite.
- Python 3 for the package-manifest tests.
- An authenticated [GitHub CLI](https://cli.github.com/) session and a disposable issue and
  Project only for live GitHub integration testing.

### Build and run

For repeated development use, source the activation script once in the current Bash or Zsh
session. It builds the Debug artifact, defines a temporary `wrighty` shell function that invokes
the built DLL directly, and prepends the artifact directory to `PATH`:

```shell
source scripts/activate-development-cli.sh
wrighty --help
wrighty list --compact
```

The command works from any directory and avoids `dotnet run` and project evaluation on each
invocation. The `PATH` change is inherited by agent CLIs started from the activated shell and by
their child command shells. For example:

```shell
source scripts/activate-development-cli.sh
claude
```

For a local Claude Desktop session on macOS, fully quit Claude first, then launch a new application
process from the activated terminal so it inherits the modified `PATH`:

```shell
source scripts/activate-development-cli.sh
open -n -a "Claude"
```

In the new Desktop session, ask Claude to verify the development command before using Wrighty:

```shell
command -v wrighty
wrighty --help
```

The agent session or desktop application must be started after activation; an already-running
process cannot receive the changed environment. Desktop applications launched independently from
the Dock or Finder and new terminal sessions do not inherit it. Remove the function and restore
the original `PATH` with:

```shell
wrighty_deactivate
```

Set `WRIGHTY_DEV_CONFIGURATION=Release` before sourcing to use a Release build. Set
`WRIGHTY_DEV_NO_BUILD=1` to reuse an existing artifact without building first.

For one-off commands without activation:

```shell
dotnet build Wrighty.slnx
dotnet run --project src/Highbyte.Wrighty.Cli -- init
dotnet run --project src/Highbyte.Wrighty.Cli -- list --compact
dotnet run --project src/Highbyte.Wrighty.Cli -- get 42
dotnet run --project src/Highbyte.Wrighty.Cli -- creation-attempt new --json
dotnet run --project src/Highbyte.Wrighty.Cli -- create --title "Example" --body-file description.md --priority P1 \
  --creation-attempt-id 019f5c485c2b7862aeac80eb638a7b5c
dotnet run --project src/Highbyte.Wrighty.Cli -- claim 42
dotnet run --project src/Highbyte.Wrighty.Cli -- move 42 "In Progress"
dotnet run --project src/Highbyte.Wrighty.Cli -- edit 42 --priority P0 --body-file description.md
dotnet run --project src/Highbyte.Wrighty.Cli -- pick
dotnet run --project src/Highbyte.Wrighty.Cli -- finish 42
dotnet run --project src/Highbyte.Wrighty.Cli -- archive 42
dotnet run --project src/Highbyte.Wrighty.Cli -- list --archived
dotnet run --project src/Highbyte.Wrighty.Cli -- unarchive 42
dotnet run --project src/Highbyte.Wrighty.Cli -- release 42
```

Every command except help supports `--json`. `list` additionally supports `--compact`, `--status`,
`--limit`, `--archived`, and `--include-archived`.

### Test

```shell
dotnet test Wrighty.slnx
python3 -m unittest discover -s tests/PackageManagerManifestTests -p 'test_*.py'
```

Unit tests and local filesystem integration tests do not call GitHub. Live GitHub protocol and
archive validation requires an authenticated `gh` session and a disposable issue/project; it is
intentionally not part of the normal test run.

The package-manifest tests exercise Homebrew and Scoop generation locally and do not access either
companion repository.

### GitHub integration fixture

The repository includes a setup script for the disposable personal GitHub Project and issue used
by live integration tests:

```shell
scripts/setup-github-integration-fixture.sh
```

The normal mode is idempotent: it reuses the exact configured Project and issue when present,
ensures the Status and Priority fields, runs `wrighty init` to link the repository and reconcile
managed fields, adds the issue to the Project, sets it to Todo/P1, clears current-agent
projections, and validates the resulting schema. It rewrites the tracked test-only
`.wrighty.integration-fixture.json` with the selected Project number. This leaves
`.wrighty.json` available for a real tracker configuration in this repository.

To discard all fixture history and recreate it from scratch:

```shell
scripts/setup-github-integration-fixture.sh --recreate
```

`--recreate` permanently deletes Projects with the exact fixture title and every real issue from
the configured repository contained in those Projects, including issues created by integration
tests. It also deletes issues with either the exact fixture title or the
`wrighty-fixture` label. Deleting an issue deletes its claim comments. The flag is
intentionally destructive and non-interactive so it can be used by automated integration setup.
Run `--help` for repository, owner, and title overrides.

### Persistent GitHub pagination fixture

Live validation across GitHub's real 100-item Project page boundary uses a separate persistent
fixture. Its seed workflow and read-only test are deliberately independent. Neither is part of
`dotnet test Wrighty.slnx`.

The seed script defaults to a dedicated private repository named
`OWNER/wrighty-scale-fixture`, a private Project named
`Wrighty Pagination Fixture`, and 101 deterministically titled issues:

```shell
scripts/seed-github-pagination-fixture.sh
```

Initial seeding is mutating and may take several minutes because requests are serialized with a
delay. It creates the private repository only when absent, reuses or creates the exact Project,
creates only missing labelled issues, repairs missing Project membership, and configures one
final-page sentinel as `In Progress`/`P1`. It generates the ignored local configuration file
`.github-pagination-fixture.json`.

When the repository, Project, 101 issues, membership, fields, and sentinel are already valid, a
normal rerun performs validation reads without recreating them. Validate without permitting any
repair using:

```shell
scripts/seed-github-pagination-fixture.sh --check
```

Unexpected extra or duplicate fixtures stop with an error and are never deleted automatically.
`--recreate` explicitly deletes only the exact fixture Project and issues carrying the fixture
label, then rebuilds them; it never deletes the private repository. Run `--help` for owner,
repository, item-count, pacing, and configuration overrides.

The live xUnit project is excluded from the solution and skips unless explicitly enabled. After
the fixture has been seeded and validated, run only the read-only pagination test with:

```shell
WRIGHTY_RUN_GITHUB_LIVE=1 \
WRIGHTY_GITHUB_LIVE_CONFIG="$PWD/.github-pagination-fixture.json" \
dotnet test tests/Highbyte.Wrighty.GitHubLiveTests \
  --filter Category=GitHubLivePagination
```

Set `WRIGHTY_GITHUB_LIVE_ITEM_COUNT` when the seed used a count other than 101. The test
does not seed, repair, edit, or delete GitHub resources. It verifies the expected item count, real
page-request count, direct field lookup, and discovery of the final-page sentinel.

### Release

Publishing a GitHub release triggers the release workflow. Its tag must be a semantic version,
optionally prefixed with `v` (for example, `v0.1.0-alpha`). The workflow uses that version to
publish self-contained, single-file `wrighty` CLI builds for `win-x64`, `win-arm64`,
`linux-x64`, `linux-arm64`, and `osx-arm64`.

For each runtime, the release receives a `wrighty-<version>-<rid>.zip` asset containing the
executable and bundled skill files, plus a matching `.zip.sha256` file in conventional
`<sha256>  <filename>` format.

The release workflow updates `highbyte/homebrew-tap` and `highbyte/scoop-bucket` after it has
published and verified the release checksums. Its manual-dispatch mode builds the same per-runtime
ZIP and checksum artifacts without creating a release or updating either package-manager
repository. Before publishing a release, configure the `PACKAGE_MANAGER_TOKEN` repository secret
with Contents read/write access to both companion repositories.

## License

Wrighty is licensed under the [MIT License](LICENSE).
