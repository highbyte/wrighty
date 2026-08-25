# Storage and filesystem reference

This page is the centralized reference for directories and files Wrighty creates, manages, or
reads as durable operational input. Run `wrighty config show` to see the runtime-resolvable core
paths for the current tracker and installation, whether each location exists, its backend
applicability, and its lifecycle. The web console shows the same inventory on **Settings**. Agent
skill installations and vendor-owned session stores are included in the reference below but not in
that runtime inventory because their active agent, scope, or session is invocation-specific.

The inventory does not include the installed `wrighty` executable and package-manager files, Git's
own `.git/` data, or ordinary files an agent creates while doing project work. Paths selected
explicitly by options such as `--config`, `--token-file`, `--manifest`, and
`--existing-workspace` replace the corresponding defaults below.

See the [item metadata reference](../item-metadata/README.md) for field tables, authority
boundaries, and deterministic Local Markdown and GitHub examples.

## Quick reference

The lifecycle matters more than the parent directory. In particular, Wrighty's cache root also
contains installation identity and session/runtime records that are not harmless to delete.

Directory cells are intentionally blank after the first entry in a group. The directory
identifiers used in the table are defined below. In a normal repository layout, `<tracker-root>`
and `<repository-root>` are the same directory because `.wrighty.json` is stored at the Git
worktree root. They are named separately because an explicitly selected configuration can live
elsewhere: paths in that configuration follow `<tracker-root>`, while generated GitHub repository
files follow `<repository-root>`.

| Directory identifier | Resolves to |
| --- | --- |
| `<tracker-root>` | Directory containing the effective `.wrighty.json`, after repository discovery, `--config`, or `WRIGHTY_CONFIG_PATH` resolution. Normally the same as `<repository-root>`. |
| `<repository-root>` | Top level of the current Git worktree. Normally the same as `<tracker-root>`; Wrighty writes GitHub Issue Forms relative to this directory. |
| `<local-store>` | Effective `localMarkdown.path`, resolved relative to `<tracker-root>`; defaults to `<tracker-root>/.wrighty`. |
| `<user-config-root>` | macOS: `~/Library/Application Support/wrighty`; Linux: `$XDG_CONFIG_HOME/wrighty` or `~/.config/wrighty`; Windows: `%APPDATA%\wrighty`. `WRIGHTY_CONFIG_DIR` overrides it. |
| `<cache/state-root>` | macOS: `~/Library/Caches/wrighty`; Linux: `$XDG_CACHE_HOME/wrighty` or `~/.cache/wrighty`; Windows: `%LOCALAPPDATA%\wrighty\cache`. `WRIGHTY_CACHE_DIR` overrides it. |
| `<managed-web-root>` | macOS/Linux: `~/.wrighty/webui`; Windows: `%LOCALAPPDATA%\Wrighty\webui`. An explicit `--token-file` replaces the managed token path. |
| `<temporary-root>` | Operating system temporary directory returned to Wrighty for the current process. |
| `<user-or-project>` | User home directory for a user-scoped skill installation, or the selected project root for a project-scoped installation. |
| `{repo-parent}` and `{repo}` | Parent directory and directory name of the Git repository; these form the default worker-worktree root. |

Other angle-bracket components, such as `<guid>`, `<session-id>`, and `<workspace-hash>`, are
runtime-specific filename placeholders rather than additional base directories.

| Directory | File or pattern | Lifecycle | Backend | Commit? | If removed | Details |
| --- | --- | --- | --- | --- | --- | --- |
| `<tracker-root>/` | `.wrighty.json` | Repository configuration | Both | Yes | Wrighty no longer discovers the tracker | [Repository configuration](#repository-configuration) |
|  | `.wrighty.json.edit.lock` | Temporary process lock | Both | No | Recreated; never remove during a typed configuration edit | [Repository configuration](#repository-configuration) |
|  | `..wrighty.json.<guid>.tmp` | Atomic-write temporary file | Both | No | Normally safe only when no configuration write is running | [Repository configuration](#repository-configuration) |
| `<local-store>/` | `items/*.md` | Authoritative work items | Local Markdown | Yes | Active work items are lost | [Local Markdown store](#local-markdown-store) |
|  | `archive/*.md` | Authoritative work items | Local Markdown | Yes | Archived work items are lost | [Local Markdown store](#local-markdown-store) |
|  | `.gitignore` | Generated repository content | Local Markdown | Yes | Runtime files may appear as untracked | [Version control](#local-markdown-version-control) |
|  | `.wrighty-runtime-v1.json` | Machine-local authoritative runtime state | Local Markdown | No | Claims and retained session/resume information are lost | [Runtime sidecar](../item-metadata/local-markdown-backend.md#runtime-sidecar) |
|  | `.lock` | Temporary process lock | Local Markdown | No | Recreated; never remove while Wrighty is using the store | [Local Markdown store](#local-markdown-store) |
|  | `.*.tmp` | Atomic-write temporary files | Local Markdown | No | Normally safe only when no Wrighty process is running | [Version control](#local-markdown-version-control) |
| `<repository-root>/.github/ISSUE_TEMPLATE/` | `wrighty-task.yml` | Generated repository content | GitHub | Yes | The Wrighty task Issue Form disappears | [GitHub repository files](#github-repository-files) |
|  | `config.yml` | Generated repository content | GitHub | Yes | GitHub's issue chooser behavior may change | [GitHub repository files](#github-repository-files) |
| `<tracker-root>/.wrighty-imports/` | `local-markdown-to-<repository>-project-<number>.json` | Durable import progress | GitHub import | Usually no | Interrupted imports cannot safely resume from the recorded attempts | [Import manifests](#import-manifests) |
| `{repo-parent}/{repo}.worktrees/` by default | `<worktree-name>/…` | Active or retained workspaces | Both | No | Uncommitted agent work may be lost | [Worker worktrees](#worker-worktrees) |
| `<user-config-root>/` | `settings-v2.json` | Authoritative user configuration | Both | No | Host label and execution-profile mappings are lost | [User configuration](#user-configuration) |
|  | `settings-v1.json` | Legacy user configuration | Both | No | Older Wrighty versions lose their retained settings | [User configuration](#user-configuration) |
|  | `settings-v2.json.<guid>.tmp` | Atomic-write temporary file | Both | No | Normally safe only when no user-settings write is running | [User configuration](#user-configuration) |
| `<cache/state-root>/` | `nodes-v1.json` | Regenerable cache | GitHub | No | Project metadata is rediscovered | [Installation state and cache](#installation-state-and-cache) |
|  | `identity-v1.json` | Installation identity | Both | No | Wrighty generates a different installation identity | [Installation state and cache](#installation-state-and-cache) |
|  | `work-item-runtime-v1.json` | Machine-local runtime state | GitHub | No | Session, workspace, failure, and deferred-dispatch records are lost | [Installation state and cache](#installation-state-and-cache) |
|  | `provider-capacity-v1.json` | Regenerable operational cache | Both | No | Provider capacity and cooldown state is rediscovered | [Installation state and cache](#installation-state-and-cache) |
|  | `provider-capacity-v1.lock` | Temporary process lock | Both | No | Recreated; never remove while Wrighty is updating provider state | [Installation state and cache](#installation-state-and-cache) |
|  | `sessions-v1.json` | Legacy runtime state | GitHub | No | Pre-migration session records may be lost | [Legacy files](#legacy-files) |
|  | `provider-availability-v1.json` | Legacy operational cache | Both | No | Pre-migration provider state may be lost | [Legacy files](#legacy-files) |
|  | `*.tmp` | Atomic-write temporary files | Both | No | Normally safe only when no Wrighty process is running | [Installation state and cache](#installation-state-and-cache) |
| `<cache/state-root>/worker-instances-v1/` | `<configuration-hash>/<run-id>.json` | Machine-local runtime state | Both | No | Worker liveness and configuration-drift observations disappear until processes register again | [Installation state and cache](#installation-state-and-cache) |
|  | `<configuration-hash>/<run-id>.stop.json` | Temporary machine-local control request | Both | No | A pending cooperative drain/interrupt request is lost | [Installation state and cache](#installation-state-and-cache) |
| `<cache/state-root>/worker-interruptions-v1/` | `<run-id>-<item-hash>.json` | Temporary interruption-recovery breadcrumb | Both | No | An incomplete interrupted-run finalizer is harder to diagnose; item and claim state remain authoritative | [Installation state and cache](#installation-state-and-cache) |
| `<cache/state-root>/handoff-v1/` | `<work-item>-<hash>.md` | Machine-local runtime artifacts | Both | No | Operator-inspectable handoff packets are lost | [Installation state and cache](#installation-state-and-cache) |
| `<cache/state-root>/copilot-shares-v1/` | `<session-id>.md` | Machine-local session exports | Both | No | Copilot-to-other-agent handoff context is lost | [Installation state and cache](#installation-state-and-cache) |
| `<managed-web-root>/<tracker>-<hash>/` | `token` | Credential | Both | No | Persistent web links stop authenticating until a token is recreated | [Web credentials](#web-credentials) |
|  | `token.lock` | Credential-write process lock | Both | No | Recreated; never remove during token creation or rotation | [Web credentials](#web-credentials) |
|  | `.token.<guid>.tmp` | Credential-write temporary file | Both | No | Normally safe only when no token creation or rotation is running | [Web credentials](#web-credentials) |
| `<temporary-root>/wrighty-workspace-locks/<user-hash>/` | `<workspace-hash>.lock` | Temporary process lock | Both | No | Recreated; never remove while a worker is using the workspace | [Workspace locks](#workspace-locks) |
| `<user-or-project>/.agents/skills/wrighty/` or `<user-or-project>/.claude/skills/wrighty/` | `SKILL.md` | Installed integration asset | Both | Scope-dependent | The corresponding agent loses the Wrighty skill | [Agent skills](#agent-skills) |
|  | `.wrighty-skill.json` | Installed integration metadata | Both | Scope-dependent | Wrighty can no longer identify the installed skill version | [Agent skills](#agent-skills) |
|  | `references/**` | Installed integration assets | Both | Scope-dependent | The installed skill loses bundled reference material | [Agent skills](#agent-skills) |
| `~/.claude/projects/**/` | `<session-id>.jsonl` | Vendor-owned session data | Both | No | Claude session resume/handoff may become unavailable | [External vendor session files](#external-vendor-session-files) |
| `~/.codex/sessions/**/` | `rollout-*-<session-id>.jsonl` | Vendor-owned session data | Both | No | Codex session resume/handoff may become unavailable | [External vendor session files](#external-vendor-session-files) |

## Repository configuration

`.wrighty.json` is portable tracker identity and policy. The CLI searches the current directory and
its parents unless `--config` or `WRIGHTY_CONFIG_PATH` selects another file. Relative paths inside
the configuration, including `localMarkdown.path`, resolve from the configuration file's directory.
The file normally belongs in version control and never contains credentials. See
[Configuration](configuration.md#configuration-file).

Atomic saves can briefly create `.<configuration-name>.<guid>.tmp` beside the file. Wrighty removes
that file after replacement or a failed write whenever possible. Typed edits also hold
`<configuration-name>.edit.lock` for the duration of the edit; the operating system removes the
lock file when Wrighty closes it.

## Local Markdown store

The default Local Markdown configuration points to `.wrighty/` beside `.wrighty.json`:

```text
.wrighty/
├── items/
├── archive/
├── .gitignore
├── .lock
└── .wrighty-runtime-v1.json
```

The configured `items/` and `archive/` directories contain authoritative UTF-8 Markdown work-item
documents. The numeric filename prefix is the identity; editing the title may rename the file
without changing an ID such as `local:1`.

Live claims, recorded agent sessions, normalized run failures, and exact deferred-dispatch timers
are machine-local runtime state in `.wrighty-runtime-v1.json`. This sidecar is authoritative for
those local runtime facts and is not a disposable cache. Deleting it does not delete Markdown work
items, but it does lose claims and retained session/resume information that cannot be reconstructed
from those documents.

The store-wide `.lock` coordinates processes sharing the same filesystem. Git synchronization does
not provide distributed claim or ID allocation; use the GitHub backend for concurrent installations.
The [Local Markdown metadata reference](../item-metadata/local-markdown-backend.md) defines the
document and sidecar formats. The [work-item reference](work-items.md#importing-and-adopting)
describes how to import unmanaged Markdown rather than copying it directly into `items/`.

### Local Markdown version control

A mutating `wrighty init` inside a Git worktree creates or extends `.wrighty/.gitignore` with:

```gitignore
# Wrighty runtime state
/.lock
.*.tmp
/.wrighty-runtime-v1.json
```

Commit `.wrighty/.gitignore`, `items/`, and `archive/`. Do not ignore the entire tracker directory.
Outside a Git worktree no nested `.gitignore` is created, and `wrighty init --check` never writes
one.

## GitHub repository files

GitHub issues, Project fields, and authoritative claim comments compose the remote work item; no
local work-item directory is created. See the
[GitHub metadata reference](../item-metadata/github-backend.md).

When repository discovery matches the configured GitHub repository, `wrighty init` can generate:

- `.github/ISSUE_TEMPLATE/wrighty-task.yml`, the Wrighty task Issue Form; and
- `.github/ISSUE_TEMPLATE/config.yml`, GitHub's issue-template chooser configuration.

These are ordinary repository files and are published only through the explicit initialization
workflow. Wrighty refuses to overwrite conflicting content it does not recognize as generated.

### Import manifests

A whole-store Local Markdown to GitHub import defaults to
`.wrighty-imports/local-markdown-to-<repository>-project-<number>.json` beside `.wrighty.json`.
The manifest records retry-safe creation attempts and source-to-destination mappings. `--manifest`
selects another file. Keep a manifest until the import has completed and been verified; it may
contain work-item titles and identifiers and normally should not be committed.

## User configuration

Wrighty stores authoritative per-user settings in `settings-v2.json`:

| Platform | Default directory |
| --- | --- |
| macOS | `~/Library/Application Support/wrighty` |
| Linux | `$XDG_CONFIG_HOME/wrighty`, or `~/.config/wrighty` |
| Windows | `%APPDATA%\wrighty` |

`WRIGHTY_CONFIG_DIR` overrides the directory. `settings-v1.json` may remain beside the current file
after migration so an older Wrighty can still read its schema. Atomic writes briefly use
`settings-v2.json.<guid>.tmp`. See [User settings](user-settings.md).

## Installation state and cache

The installation root is selected independently of `.wrighty/`:

| Platform | Default cache/state root |
| --- | --- |
| macOS | `~/Library/Caches/wrighty` |
| Linux | `$XDG_CACHE_HOME/wrighty`, or `~/.cache/wrighty` |
| Windows | `%LOCALAPPDATA%\wrighty\cache` |

`WRIGHTY_CACHE_DIR` overrides the complete root. The historical name “cache root” does not mean
every child is safely disposable:

- `nodes-v1.json` contains regenerable GitHub Project, field, and option node IDs. Stale IDs are
  discarded and rediscovered once.
- `identity-v1.json` contains a generated UUID used to derive Wrighty's privacy-preserving
  installation ID. It contains no credential, but deleting it creates a new logical installation
  and affects attribution and same-installation recovery.
- `work-item-runtime-v1.json` contains GitHub-backed session/workspace addresses, bounded prior
  session lineage, normalized failures, and exact deferred retry/handoff decisions. GitHub work-item
  content and authoritative claims are not cached here.
- `provider-capacity-v1.json` and `provider-capacity-v1.lock` hold sanitized provider capacity,
  cooldown, and probe coordination.
- `worker-instances-v1/<configuration-hash>/<run-id>.json` contains worker heartbeat, process
  identity, host kind, current item/agent, cooperative-control capabilities, invocation summary,
  and configuration revision. A sibling `<run-id>.stop.json` is an identity-bound drain or
  interruption request consumed only by that exact run and removed on clean exit.
- `worker-interruptions-v1/<run-id>-<item-hash>.json` is a bounded, non-secret breadcrumb written
  before an operator/host interruption terminates an agent process. Successful finalization removes
  it. It contains neither claim tokens nor session IDs and is diagnostic only; tracker state and
  exact claim ownership remain authoritative.
- `handoff-v1/<work-item>-<hash>.md` contains the latest rendered cross-agent handoff packet for
  each work item.
- `copilot-shares-v1/<session-id>.md` contains worker-owned Copilot session exports used as handoff
  context.
- `*.tmp` files are interrupted atomic writes and are normally removed automatically.

The cache/state root must never be committed. It can include workspace paths, session identifiers,
failure summaries, and handoff context, so treat it as private machine-local data even where an
individual file is regenerable.

### Legacy files

Wrighty may read `sessions-v1.json` and `provider-availability-v1.json` long enough to migrate their
records into current stores. They are not current write targets. Keep them until the current files
contain the records you need and no older Wrighty version is in use.

## Web credentials

Ephemeral web launch tokens live only in process/browser memory. `wrighty web --persist-token`
creates a managed bearer credential at:

| Platform | Managed location |
| --- | --- |
| macOS/Linux | `~/.wrighty/webui/<tracker-slug>-<root-hash>/token` |
| Windows | `%LOCALAPPDATA%\Wrighty\webui\<tracker-slug>-<root-hash>\token` |

Creation and rotation also use `<token>.lock` and `.token.<guid>.tmp` in the same directory.
`--token-file` selects a different credential path. Token files are user-only and must never be
committed, copied into logs, or treated as cache. The storage inventory shows the path but never the
token value. See [Web authentication](web-console.md#authentication-and-token-lifetime).

## Worker worktrees

With `worker.workspaceMode: worktree`, the default root is
`{repoParent}/{repo}.worktrees/`; `worker.worktreeRoot` can select another template. Worktrees can
hold uncommitted product changes and retained agent sessions. They are operational workspaces, not
cache, and must never be deleted by a general cache cleanup. See
[Worker workspace modes](worker.md#workspace-modes).

## Workspace locks

Wrighty serializes worker execution in one workspace with per-user lock files under the operating
system temporary directory:

```text
<temporary-root>/wrighty-workspace-locks/<user-hash>/<workspace-hash>.lock
```

The lock records the workspace and process ID for diagnostics. The directory is recreated as
needed; do not remove lock files while a worker may still be using the corresponding workspace.

## Agent skills

`wrighty skill install` and `wrighty skill update` manage a bundled skill at these roots:

| Scope | Codex/Copilot/OpenCode | Claude Code |
| --- | --- | --- |
| User | `~/.agents/skills/wrighty/` | `~/.claude/skills/wrighty/` |
| Project | `<project>/.agents/skills/wrighty/` | `<project>/.claude/skills/wrighty/` |

Each installed directory contains `SKILL.md`, bundled references, and `.wrighty-skill.json`.
Project-scoped skills may be committed deliberately; user-scoped skills must not be committed to a
repository. See [Agent skills](agent-skills.md).

## External vendor session files

For session resume and cross-agent handoff, Wrighty may read vendor-owned files that it does not
create or manage:

| Agent | External location |
| --- | --- |
| Claude Code | `~/.claude/projects/**/<session-id>.jsonl` |
| Codex | `~/.codex/sessions/**/rollout-*-<session-id>.jsonl` |
| Copilot | Wrighty requests an explicit export under `copilot-shares-v1/`; it does not scan Copilot's private store |
| OpenCode | Wrighty requests `opencode export <session-id>`; it does not scan OpenCode's private store |

Vendor files follow the vendor's lifecycle and privacy rules. Wrighty reads only the session
selected for an authorized resume/handoff; they are not Wrighty cache entries.
