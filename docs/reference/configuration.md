# Configuration

## Choose a backend

`wrighty init` can bootstrap a tracker from any directory. A Git checkout is optional. Select a
backend explicitly when desired. Without `--backend`, initialization uses GitHub when an `origin`
GitHub remote is detected and otherwise creates a local Markdown tracker.

The local Markdown backend coordinates processes sharing one filesystem. Independent Git clones,
Git pushes, Dropbox/OneDrive synchronization, and similar replication do **not** provide
distributed claim arbitration. Use the GitHub backend when agents on different computers must
coordinate. The local Markdown backend requires no external service or additional executable.

## Initialize the Local Markdown backend

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

## Initialize the GitHub backend

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

Linking a repository is distinct from setting the Project's **Default repository**. The default
controls which repository GitHub preselects when you create an issue from a Project view. When
`wrighty init` creates a Project, it reports the configured repository and asks you to open the
Project menu, choose **Settings**, select that repository under **Default repository**, and save.
GitHub's supported Project APIs can link the repository but cannot configure or verify this
setting. Projects remain capable of containing items from multiple repositories; GitHub does not
offer a single-repository restriction.

For GitHub, `wrighty init` creates **Wrighty policy - execution** (`Manual only`, `Automatic allowed`) and
**Wrighty policy - agent** (`Repository default`, `Claude`, `Codex`, `Copilot`) as authoritative
single-select policy fields. It also maintains **Wrighty claim - agent** and claimant-type
projections plus the creation-recovery text field. **Wrighty dispatch - state**,
**Wrighty dispatch - not before**, **Wrighty dispatch - agent**, and **Wrighty dispatch - detail** fields present recovery state
without becoming authority. The optional forensics projections (**Wrighty claim - session ID**,
**Wrighty claim - claimant**, **Wrighty claim - workspace path**) are no longer created by init;
a Project that already has them keeps receiving their values. Existing compatible fields are
reused, missing canonical options are added, and the local node-ID cache is refreshed. Duplicate
names, ambiguous options, or incompatible field types are reported without being guessed.

Policy and presentation field names are configurable under `github`; every Wrighty-managed Project
field name must be distinct. A current-schema configuration may omit those mappings to receive the
canonical defaults. Unknown settings—including former field-mapping names—are rejected instead of
being ignored or migrated. Only exact `Automatic allowed` authorizes a worker; unset execution is
manual-only, and an unset agent policy means Repository default.

Initialization provisions only the `wrighty:dispatch-state=...` lifecycle labels. A Project with
the former field schema is rejected and must be replaced; initialization never migrates it.

Before any mutating initialization, Wrighty completes read-only discovery and prints the resolved
backend, repository or local store, Project reuse or creation choice, configuration path, planned
actions, common override flags, and any manual GitHub follow-up such as setting the Default
repository or deleting `View 1`. Interactive use continues only after an explicit `y` response;
the default response is No. JSON and redirected-input runs fail with
`INIT_CONFIRMATION_REQUIRED` unless `--yes` approves the complete plan. `wrighty init --check`
remains read-only and never prompts or requires `--yes`. For a new configuration, the common
overrides also show how to select the other backend: GitHub to Local Markdown or Local Markdown to
GitHub.

### Choose a local default agent

During interactive first-time initialization, Wrighty discovers the supported agent CLIs on the
current machine. If one or more are installed, it offers them as choices for
`worker.defaultAgent`; pressing Enter selects the first installed agent alphabetically. **None**
leaves the repository without a default. If none are installed, initialization reports that fact
and leaves the setting unset.

Use `--default-agent` when the choice must be explicit:

```shell
wrighty init --default-agent claude
wrighty init --default-agent auto
wrighty init --default-agent none
```

`claude`, `codex`, and `copilot` require that CLI to be installed locally. `auto` succeeds only
when exactly one supported CLI is installed, so an unattended setup never makes an ambiguous
choice. `none` clears an existing default. On an existing configuration, interactive
initialization reports the configured default without prompting; pass the option to change it.
Non-interactive, JSON, and `--yes` initialization leave the default unchanged when the option is
absent.

The configuration remains portable: `worker.defaultAgent` records a vendor name, never an
executable path. Every host independently discovers and validates its local installation before a
worker claims an item. `wrighty init --check --json` reports every supported agent with
`installed`, `executable`, and `readiness` fields without changing configuration.

```json
{
  "agent": "codex",
  "supported": true,
  "installed": true,
  "executable": "/opt/homebrew/bin/codex",
  "readiness": "unknown"
}
```

The default GitHub plan creates one neutral **Wrighty task** form under
`.github/ISSUE_TEMPLATE`. It adds the issue to the configured Project without selecting an agent
or authorizing unattended execution, and tells users that a Project maintainer reviews the task,
sets Wrighty policy - agent when needed, and changes Wrighty policy - execution to Automatic allowed. Wrighty also creates
a managed `config.yml` with
`blank_issues_enabled: false`. GitHub still shows a maintainer-only blank option to users with
Write, Maintain, or Admin access; other users are directed through the Wrighty forms.
`--skip-issue-forms` opts out of both the forms and chooser configuration. Wrighty leaves the files
uncommitted; review, commit, and push them to the repository's default branch before GitHub can
offer them. In an interactive run, Wrighty asks whether to stage, commit, and push the generated or
refreshed forms.
The default answer is No. For unattended setup, `--yes --publish-issue-forms` explicitly requests
publication; `--yes` alone never pushes. The generated commit contains only Wrighty's managed
template paths and does not consume unrelated staged changes. If push fails after commit, Wrighty
reports `PARTIAL_ISSUE_FORM_PUBLISH` and the exact retry command. Existing compatible files are
reused. An otherwise unchanged Wrighty-generated form is refreshed when the configured Project
changes; genuinely customized or conflicting files are reported without being overwritten.
Customized, unrelated, and marker-free issue-template files are preserved and reported.

When `wrighty init` creates a GitHub Project, GitHub also creates an initial table named
`View 1`. Wrighty queries the Project's views, creates and verifies `Wrighty Board` and
`Wrighty Attention`, and reports the results. `Wrighty Board` is created with the card fields
Priority, `Wrighty dispatch - state`, `Wrighty policy - context approval`, and
`Wrighty claim - agent` preselected; `Wrighty Attention` is a table filtered to
`wrighty-dispatch---state:"Needs attention"` (the filter bar's dash-joined qualifier form of the
field name). GitHub does not expose a supported API for
deleting or reordering Project views, so Wrighty leaves `View 1` unchanged. If you want
`Wrighty Board` to be the Project's only and default view, open `View 1`, choose its view menu,
and delete it manually.

For an existing Project, normal initialization preserves every view. Use
`wrighty init --create-view` to explicitly create `Wrighty Board` and `Wrighty Attention` when
missing. Because the views API supports shown fields and filters only at creation, a view that
already exists is reused as-is and the manual adjustment recipe is reported instead.
`wrighty init --check` queries and validates views without writing. Optional manual enhancement:
setting the board's **Slice by** to `Wrighty dispatch - state` adds a per-state count panel.

## Adopting settings on a rerun

`wrighty init` is safe to rerun in an initialized repository: it validates the backend
resources and is also how that repository adopts a setting a later Wrighty version introduced.
Interactive reruns offer the settings the configuration has no opinion on yet, and write the
ones you accept.

Three rules keep a rerun from surprising you:

- **Only undecided settings are offered.** A value the configuration already records — true or
  false — is a decision, so it is never re-asked and never overwritten. To change one, edit
  `.wrighty.json`.
- **Backend-specific questions follow the configured backend**, not the folder. A Local
  Markdown store in a repository with a GitHub remote is not asked GitHub-only questions.
- **Every write is announced first.** Accepted settings appear in the initialization plan
  (`set worker.usageFailure.allowCrossAgentHandoff = true in the configuration`) before the
  confirmation prompt, and in the reported actions afterwards.

`--yes`, `--json`, redirected input, and `--check` make no offers and therefore adopt nothing.

## Configuration file

The CLI searches the current directory and its parents for `.wrighty.json`. During
first-time setup it writes the file in the current directory unless `--config` is supplied. The
file contains no credentials and should normally be committed so different machines use the same
tracker configuration. For the GitHub backend, authentication remains in `gh`.

`.wrighty.json` holds per-repository tracker configuration. Personal preferences that should not
travel with the repository — such as the symbolic GitHub host label — live in a separate
user-scoped settings file managed with `wrighty config user`; see
[User settings](user-settings.md).

Configuration examples are available for the
[GitHub backend](../../.wrighty.github.example.json) and the
[local Markdown backend](../../.wrighty.local-markdown.example.json). Copy the relevant file
to `.wrighty.json` and replace its example values when configuring manually. The examples show the
commonly-edited settings and enable automatic archiving when Status becomes `Done` (use an empty
`archive.onStatuses` array to disable that behavior); the [Settings reference](#settings-reference)
below lists every field. The GitHub Project field-name mappings are omitted from the example
because `wrighty init` provisions those fields with the default names shown in the reference.
Running `wrighty init` is preferred because it also creates or validates the backend resources.

### Inspect and safely change repository policy

`wrighty config show` provides one effective view of both user and repository configuration.
Scoped views and mutations remain under `config user` and `config repository`:

```shell
wrighty config show
wrighty config show --json
wrighty config user show
wrighty config user host set workstation-alpha
wrighty config user host clear

wrighty config repository show
wrighty config repository show --json
wrighty config repository check
```

Every view prints the absolute source path and whether the file exists. The aggregate view remains
useful outside an initialized repository: it shows user configuration and reports the repository
configuration as not found. An explicit missing `--config` path is an error. Repository path
resolution reports whether the file came from upward discovery, `--config`, or
`WRIGHTY_CONFIG_PATH`.

The repository view reports effective values, marking values supplied by Wrighty's defaults. Its
JSON form additionally reports stored values, the raw-file SHA-256 revision, schema version, edit
mode, and the process boundary at which each setting takes effect. Supported policy groups have
typed commands:

```shell
wrighty config repository workflow set-defaults \
  --pick-from Todo --pick-to "In Progress" --finish-to Done
wrighty config repository archive set --on-status Done
wrighty config repository worker set \
  --default-agent codex --workspace-mode worktree
wrighty config repository completion set \
  --commit inspect --integration none
wrighty config repository web set --protect-non-human-claims true
```

Use `--dry-run` to inspect a typed diff, `--json` for structured output, `--config PATH` for an
explicit file, and `--revision SHA256` when a caller needs an exact compare-and-save boundary.
Every mutation reloads and validates the whole file. If the bytes changed after they were read,
Wrighty returns `CONFIG_CONFLICT` and writes nothing. Comments and trailing commas remain readable,
but a save canonicalizes the JSON and therefore requires both a prior preview and `--yes`.

Backend identity, Local Markdown storage, GitHub repository/Project identity, field mappings, and
`schemaVersion` are inspection-only in ordinary configuration commands. Change those through
`wrighty init` or a purpose-built migration, not a general settings form. Unknown properties and
schema versions newer than this Wrighty build fail closed so an older editor cannot erase newer
settings. Existing valid unversioned files are schema version 1; the first canonical typed save
writes `"schemaVersion": 1`.

Repository configuration is a startup snapshot for continuous workers and `wrighty web`. One-shot
commands read a saved change on their next invocation, but running workers, the current web
process, and already-started or retained agent sessions are not hot-reconfigured. `wrighty status`
and the web operations console compare registered local-worker revisions with the stored file and
state which processes require a restart.

| Change | Effective boundary | Ordinary settings behavior |
| --- | --- | --- |
| User host label | Next operation that reloads user settings | Saved immediately; retained sessions are unchanged. |
| Workflow/archive defaults | New worker process | Save with a restart warning. |
| Worker agent, workspace, or completion policy | New worker process | Save with worker revision-drift reporting. |
| Web claim protection | New web process | The saving web process continues on its active snapshot. |
| Lease or Local workflow vocabulary | Guarded/quiescent migration | Displayed, but not editable by the ordinary commands. |
| Backend, store, repository, Project, field mappings, schema | Initialization or migration | Read-only with initialization guidance. |
| Web bind, host, port, authentication, public URL | Current invocation only | Never persisted in repository or user configuration. |

### Settings reference

Every setting and its default is listed below. Deeper semantics for the worktree and completion
templates live in [Autonomous worker mode](worker.md#branches-worktrees-and-the-workspace-lifecycle).

#### Top level

| Setting | Default | Description |
| --- | --- | --- |
| `schemaVersion` | `1` | Configuration format version. Newer versions fail closed; ordinary settings commands cannot change it. |
| `backend` | `github` | Tracker backend: `github` or `local-markdown`. |
| `defaultPickFrom` | `Todo` | Status the pick/start workflow moves an item from. |
| `defaultPickTo` | `In Progress` | Status an item moves to when picked up for work. |
| `defaultFinishTo` | `Done` | Status `finish` sets unless `--status` is supplied. |
| `leaseMinutes` | `60` | Claim lease duration; a fenced claim must be renewed before it expires. |
| `archive.onStatuses` | `[]` | Statuses that auto-archive an item on reaching them. Empty disables auto-archiving. |
| `web.protectNonHumanClaims` | `true` | Local Markdown dashboard only: block editing an item held by a non-human claim until an explicit takeover. |
| `localMarkdown` | — | Local Markdown backend settings (below). Required when `backend` is `local-markdown`. |
| `github` | — | GitHub backend settings (below). Required when `backend` is `github`. |
| `worker` | — | Autonomous worker settings (below). |

#### `worker`

| Setting | Default | Description |
| --- | --- | --- |
| `worker.defaultAgent` | (none) | Repository-default vendor (`claude`, `codex`, or `copilot`) when neither `--agent` nor an item preference resolves one. Each worker host must have that vendor CLI installed; Wrighty never falls back to another vendor. |
| `worker.workspaceMode` | `current` | Default workspace behavior: `current`, `shared`, or `worktree`. Overridden by `--workspace-mode`. |
| `worker.worktreeRoot` | `{repoParent}/{repo}.worktrees` | Template directory that receives worktrees. Placeholders: `{repo}`, `{repoParent}`, `{home}`, `{repoPathHash}`. |
| `worker.branchFormat` | `wrighty-worker/{id}-{title}` | Template for the worker branch name. Placeholders: `{id}`, `{number}`, `{title}`, `{unique}`, `{agent}`, `{date}`. A format without `{unique}` gets a uniqueness suffix only if the name would otherwise collide. |
| `worker.worktreeNameFormat` | `{id}-{title}` | Template for the worktree directory name (same placeholders as `branchFormat`). |
| `worker.handoverComment` | `full` | GitHub only. Controls the single rolling [status comment](worker.md#github-status-comment) posted on `needs-attention`/retained-worktree runs: `full` (includes the branch and the host label, and the workspace path when `shareLocalPaths` is enabled), `minimal` (omits local machine details, keeps the branch), or `off`. Ignored by Local Markdown. |
| `worker.shareLocalPaths` | `false` | GitHub only. Privacy-preserving default: the absolute workspace path (which embeds the OS username) is **not** published to any GitHub surface — the claim-marker JSON, the Project workspace-path field, or the status comment (which uses path-free `wrighty` commands instead). The path stays in the machine-local work-item runtime store, which is the only place Wrighty reads it from, so resume on the recording host is unaffected and a published path is never acted on (see [claims](claims.md#who-may-write-a-claim-event)). Set to `true` only when every collaborator with repository access is trusted to see local machine paths. The published host label defaults to `anonymous`; set a symbolic one with `wrighty config user host set`. |
| `worker.useWorkerQueue` | `true` | The pick-from status (`defaultPickFrom`, `"Worker queue"` by default) is the worker queue: placing an item there authorizes automatic execution and, on GitHub, cycles context approval through Needs review to Approved for a fresh cutoff. Wrighty-surface moves apply the writes immediately; a running worker repairs missing authority for items that arrived by GitHub board drag. Moving out through Wrighty revokes execution only, because content approval remains valid until an edit or explicit reset. Worker-owned status moves never trigger the rule, and an explicitly patched execution policy wins. **Keep a dedicated queue status**: pointing at a general-purpose `Todo` authorizes everything already there. GitHub `init` inserts a missing configured pick-from option second without reordering existing options; `init --check` reports a missing configured option rather than creating one. Set `false` to keep execution and context approval as separate explicit edits. Existing pre-release `Agent queue` configurations remain valid; rename the Project option or Local Markdown status plus affected item statuses to adopt the new default name. |
| `worker.agentPermissions` | `workspace` | Permission profile requested when the worker spawns a headless agent: `workspace` (least privilege that still completes tracked work) or `full` (the vendor's unrestricted mode). See [Spawned-agent permissions](worker.md#spawned-agent-permissions) for what each vendor actually enforces. |
| `worker.desktopSessions.claude` | `off` | Local Markdown dashboard only. Set to `experimental` to expose Claude's undocumented local Desktop resume link. It remains visibly labeled experimental and keeps the human-supervised ownership rules. |
| `worker.agents` | — | Per-agent overrides keyed by vendor name (below). |
| `worker.completion` | — | Completion policy (below). |
| `worker.usageFailure` | — | Bounded recovery policy for subscription usage exhaustion and temporary rate limiting. Defaults to same-agent retry. |

#### `worker.agents.<vendor>`

| Setting | Default | Description |
| --- | --- | --- |
| `worker.agents.<vendor>.permissions` | inherits `worker.agentPermissions` | `workspace` or `full` for one vendor (`claude`, `codex`, or `copilot`). |

```jsonc
"worker": {
  "agentPermissions": "workspace",
  "agents": {
    "claude": { "permissions": "full" }
  }
}
```

An unrecognized profile name, or an override for an unsupported vendor, fails configuration
loading with `CONFIG_INVALID`. Guessing what an unreadable value meant would decide how much
privilege an unattended agent receives.

#### Experimental Claude Desktop sessions

Claude's recorded-session Desktop link is disabled unless the repository opts in explicitly:

```jsonc
"worker": {
  "desktopSessions": {
    "claude": "experimental"
  }
}
```

This enables only the fixed `claude://resume?session=<uuid>` address. It does not make Claude's
CLI/Desktop history contract supported, inject a Wrighty claim into Desktop, or remove the
single-client hand-back warning. Use `off` or remove the setting to disable it again.

#### `worker.continuation`

How a waiting item continues when a trusted author replies to it or uses an explicit control
reaction. An item that ends `needs-attention` keeps its recorded agent session; a reply from an
author named in `github.trustedCommentAuthors` queues that session again, so the agent carries on
with what they wrote instead of waiting for someone to run a command.

There is no enable switch. Continuation already requires automatic execution, a resumable session
recorded on this host, intact context approval, and a non-empty trusted-author list — naming an
author is the opt-in. It never applies to a manual item, an item whose approval has lapsed, or a
session recorded on another machine.

| Setting | Default | Description |
| --- | --- | --- |
| `worker.continuation.trigger` | `any-trusted-comment` | `any-trusted-comment` continues on any reply from a trusted author. `command-only` requires the reply's first line to be exactly `worker.continuation.command`, which suits a team where conversational replies should not spend an agent turn. |
| `worker.continuation.command` | `/wrighty continue` | The control command for `command-only`. Matched as a whole first line and never interpreted from prose, so discussing the command cannot start a run. Any remaining body is still task context. |
| `worker.continuation.resumeReaction` | `rocket` (🚀) | GitHub reaction on the current unresolved Wrighty status comment that resumes the retained session without adding information. The actor must be in `github.trustedCommentAuthors`. |
| `worker.continuation.completionReaction` | `hooray` (🎉) | GitHub reaction on the current unresolved Wrighty status comment that asks the retained agent to verify the approved work and call the ordinary `wrighty finish` command. It does not directly finish or archive the item. |
| `worker.continuation.maxAutomaticContinuations` | `10` | Automatic continuations one session may spend. Reaching it never finishes, archives, or restarts anything: the item stays `needs-attention` until you act. The count belongs to the session — a fresh run starts at zero, and resuming an item yourself neither spends nor resets it. |
| `worker.continuation.cooldownSeconds` | `30` | Minimum gap between automatic continuations, measured from the last queue so a burst of replies cannot bypass it. |
| `worker.continuation.debounceSeconds` | `10` | How long a reply must settle before it is acted on, so editing a comment straight after posting means the agent reads the edited text. A reply younger than this is reconsidered on a later poll, not discarded. |

The status comment itself says what is sufficient and where each control belongs: a trusted reply
alone continues with that reply as new context, while 🚀 and 🎉 are accepted only on the Wrighty
status comment. A reaction on the user's reply is inert. Wrighty accepts a configured reaction only
on the strict status comment for the current waiting run, strictly after that comment's latest
update, from a trusted author, and only once. If the newest trusted reactions express both controls
at the same instant, neither is accepted until the ambiguity is resolved. GitHub does not advance
an issue or comment timestamp when a reaction is added, so Wrighty polls the cached status comment
directly while the session waits; it does not repeatedly page the whole discussion. Reaction checks
run at most once a minute per waiting report. Wrighty conditionally revalidates both the comment and
its reactions, so unchanged responses do not consume GitHub's primary REST rate-limit quota.

#### `worker.usageFailure`

| Setting | Default | Description |
| --- | --- | --- |
| `worker.usageFailure.action` | `retry` | `retry` schedules the recorded same-agent session; `needs-attention` stops automatic recovery. `handoff` hands the work to the first available configured fallback agent as a new session in the retained workspace. |
| `worker.usageFailure.initialRetryMinutes` | `30` | First fallback delay when the provider supplies neither an exact reset nor `Retry-After`. |
| `worker.usageFailure.backoffMultiplier` | `2` | Multiplier applied to each later fallback attempt. Must be at least `1`. |
| `worker.usageFailure.maxRetryHours` | `6` | Maximum fallback delay. |
| `worker.usageFailure.maxAttempts` | `5` | Maximum scheduled attempts before the item moves to `needs-attention`. |
| `worker.sessionReportMode` | — | Legacy compatibility setting. `off`, `completed`, and `all` are still accepted so old configuration files load, but they no longer change behavior. Every terminal run is stored locally; GitHub shows the current run in the single rolling `worker.handoverComment`. Remove this setting when convenient. |
| `worker.context.maxDiscussionComments` | `100` | Maximum discussion entries requiring an approval decision on one item, whether or not they end up included. Exceeding it refuses the launch. |
| `worker.context.maxEntryCharacters` | `20000` | Maximum characters in a single discussion entry. |
| `worker.context.maxTotalCharacters` | `100000` | Maximum characters in the whole [approved context](worker.md#launch-preflight) — title, body, and every included entry. |
| `worker.usageFailure.resetGraceMinutes` | `2` | Grace added to an exact provider reset before bounded jitter. |
| `worker.usageFailure.allowCrossAgentHandoff` | `false` | Opt-in: with `action: "retry"`, hand the work to a fallback agent once same-agent retries are exhausted instead of stopping at needs-attention. Interactive `wrighty init` offers this (defaulting to yes) when more than one supported agent is installed, including on a rerun over an existing configuration — see [adopting settings on a rerun](#adopting-settings-on-a-rerun). |
| `worker.usageFailure.fallbacks` | Claude/Codex/Copilot ordered defaults | Ordered handoff targets per source agent. Listing fallbacks never opts an item into handoff by itself — handoff requires `action: "handoff"` or `allowCrossAgentHandoff: true`. |

#### `worker.completion`

| Setting | Default | Description |
| --- | --- | --- |
| `worker.completion.policy` | `agent` | Who decides an item is done. `agent`: the agent calls finish when it judges the approved task satisfied. `user-confirmed`: it may not finish on its own — it reports the work it believes complete and stops, and the item waits until a person accepts that work. Where items carry a discussion, the acceptance is an ordinary reply, not a command, and a later run reads it as approved context and finishes the item. Where they do not — the Local Markdown store has no comments — nothing about who decides changes, because a person already advances every paused item there: continuing one means editing its body, which replaces what the paused session was given and so only ever proceeds under a run you start yourself. The policy changes only when the agent stops, and you finish the item with `wrighty finish` when you are satisfied. |
| `worker.completion.commit` | `inspect` | Worktree mode only. `inspect`: the agent leaves changes uncommitted for review; `agent`: the agent commits before finishing. |
| `worker.completion.integration` | `none` | Guidance rendered after finish: `none`, `merge-local`, or `push-pr`. Wrighty never executes merge, push, or PR creation. |

#### `localMarkdown`

| Setting | Default | Description |
| --- | --- | --- |
| `localMarkdown.path` | `.wrighty` | Directory holding item Markdown files and `.wrighty-runtime-v1.json`. |
| `localMarkdown.statuses` | `["Todo", "In Progress", "Done"]` | Allowed workflow statuses. |
| `localMarkdown.priorities` | `["P0", "P1", "P2", "P3"]` | Allowed priorities. |

#### `github`

| Setting | Default | Description |
| --- | --- | --- |
| `github.repository` | (required) | Target repository as `owner/repo`. |
| `github.projectOwner` | repository owner | Owner (user or org) of the GitHub Project. |
| `github.projectNumber` | (required) | GitHub Project (v2) number. |
| `github.linkRepository` | `true` | Link the repository to the Project during `wrighty init`. |
| `github.trustedCommentAuthors` | `[]` | GitHub logins whose comments count as approved without moving the context-approval field. See the warning below. |
| `github.contextApprovers` | `[]` | GitHub logins authorized to decide individual comment revisions with `+1` (include) and `-1` (exclude) reactions. |
| `github.statusField` | `Status` | Project field name for workflow status. |
| `github.priorityField` | `Priority` | Project field name for priority. |
| `github.executionPolicyField` | `Wrighty policy - execution` | Authoritative Project field for `Manual only` or `Automatic allowed`. |
| `github.agentPolicyField` | `Wrighty policy - agent` | Authoritative Project field for repository-default or item-specific routing. |
| `github.contextApprovalField` | `Wrighty policy - context approval` | Authoritative Project field for `Needs review` or `Approved`; approval controls which issue content an unattended agent may receive. |
| `github.dispatchStateField` | `Wrighty dispatch - state` | Display-only Project field for the pending dispatch category. |
| `github.dispatchNotBeforeField` | `Wrighty dispatch - not before` | Display-only Project text field for the full ISO-8601 retry timestamp. |
| `github.dispatchAgentField` | `Wrighty dispatch - agent` | Display-only Project field for the agent expected to act on retained recovery. |
| `github.dispatchDetailField` | `Wrighty dispatch - detail` | Display-only short, sanitized recovery summary. |
| `github.claimAgentField` | `Wrighty claim - agent` | Project field for the recorded agent. |
| `github.claimantTypeField` | `Wrighty claim - claimant type` | Project field for the claimant kind. |
| `github.claimantField` | `Wrighty claim - claimant` | Project field for the claimant id. |
| `github.claimSessionIdField` | `Wrighty claim - session ID` | Project field for the recorded session id. |
| `github.claimWorkspacePathField` | `Wrighty claim - workspace path` | Project field for the recorded workspace path. |
| `github.creationAttemptIdField` | `Wrighty creation - attempt ID` | Project field used for retry-safe creation reconciliation. |
| `github.claimHistoryLimit` | `10` | Maximum claim-history comments retained per item. |
| `github.gitHubHost` | `github.com` | GitHub host; set for GitHub Enterprise Server. |

### Context approvers

`github.contextApprovers` is the committed allowlist for reaction decisions. A configured
approver's `+1` reaction includes that exact comment revision; `-1` excludes it. A later comment
edit invalidates the decision because its reaction predates the new revision. Decisions from every
other login are ignored, and conflicting or incompletely readable authorization fails closed.

```json
{ "github": { "contextApprovers": ["your-login"] } }
```

`wrighty init` accepts `--context-approver <login>`, repeatably, and offers the authenticated login
during interactive GitHub setup. Commit the configured list so every worker applies the same
authorization policy. Base title/body approval remains a separate Project-field decision.

### Trusted comment authors

`github.trustedCommentAuthors` removes one step from the ordinary loop. Without it, answering an
agent's question means writing the comment *and* moving the context-approval field so the batch
cutoff covers it. Naming yourself means your comments count as decided when you write them.

```json
{ "github": { "trustedCommentAuthors": ["your-login"] } }
```

`wrighty init` accepts `--trusted-comment-author <login>`, repeatably, and offers your authenticated
login interactively during GitHub setup.

> **Naming an author also accepts every edit made to that author's comments.** GitHub lets anyone
> with write access edit another user's comment, and an edit does not change the comment's author.
> So a collaborator can rewrite a trusted author's comment and the new text is approved
> automatically, without anyone reviewing it. On a solo repository there is nobody who can do this;
> on a shared one, this setting trusts everyone you have granted write access.

Scope and limits:

- **Comments only.** Title and body still require the approval field. A body edit supersedes what a
  running session already holds, which is a change someone should see rather than one to wave
  through.
- **Wrighty's own comments are unaffected** — they are excluded from task context regardless.
- **Commit the file.** The approved-context digest is reproducible across machines only while they
  agree on the trusted set.
- Matching is case-insensitive. An empty or absent list is the default and changes nothing.

## Validate configuration

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
This pre-release schema is intentionally fresh-start only. If `wrighty init --check` reports
`PROJECT_SCHEMA_UNSUPPORTED`, create a new Project rather than renaming or copying old fields.
