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

For GitHub, `wrighty init` creates **Current agent type** as a single-select field and
**Current session ID** and **Creation attempt ID** as text fields, repairs missing standard agent
options, and refreshes the local node-ID cache. Existing compatible fields are reused. Duplicate
names or incompatible field types are reported without being changed.

Initialization also ensures the repository labels `wrighty:auto`, `wrighty:agent=claude`,
`wrighty:agent=codex`, and `wrighty:agent=copilot` exist. These labels describe item intent; they do
not assert that a particular vendor CLI is installed on the machine that eventually runs
`wrighty worker`.

Before any mutating initialization, Wrighty completes read-only discovery and prints the resolved
backend, repository or local store, Project reuse or creation choice, configuration path, planned
actions, common override flags, and any manual GitHub follow-up such as setting the Default
repository or deleting `View 1`. Interactive use continues only after an explicit `y` response;
the default response is No. JSON and redirected-input runs fail with
`INIT_CONFIRMATION_REQUIRED` unless `--yes` approves the complete plan. `wrighty init --check`
remains read-only and never prompts or requires `--yes`. For a new configuration, the common
overrides also show how to select the other backend: GitHub to Local Markdown or Local Markdown to
GitHub.

The default GitHub plan creates five local issue forms under `.github/ISSUE_TEMPLATE`:

- **Wrighty task** adds the configured Project without authorizing worker processing;
- **Wrighty worker task (default agent)** adds `wrighty:auto` without pinning a vendor;
- the Claude, Codex, and Copilot worker forms add `wrighty:auto` and their agent-specific label.

The default-agent form requires the worker machine to resolve an agent through `--agent` or
`worker.defaultAgent`. Wrighty also creates a managed `config.yml` with
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

When `wrighty init` creates a GitHub Project, GitHub also creates an initial table named
`View 1`. Wrighty queries the Project's views, creates and verifies `Wrighty Board`, and reports
both results. GitHub does not expose a supported API for deleting or reordering Project views, so
Wrighty leaves `View 1` unchanged. If you want `Wrighty Board` to be the Project's only and default
view, open `View 1`, choose its view menu, and delete it manually.

For an existing Project, normal initialization preserves every view. Use
`wrighty init --create-view` to explicitly create `Wrighty Board` when missing.
`wrighty init --check` queries and validates views without writing.

## Configuration file

The CLI searches the current directory and its parents for `.wrighty.json`. During
first-time setup it writes the file in the current directory unless `--config` is supplied. The
file contains no credentials and should normally be committed so different machines use the same
tracker configuration. For the GitHub backend, authentication remains in `gh`.

Complete configuration examples are available for the
[GitHub backend](../../.wrighty.github.example.json) and the
[local Markdown backend](../../.wrighty.local-markdown.example.json). Copy the relevant file
to `.wrighty.json` and replace its example values when configuring manually. Both
examples show every setting supported by that backend and enable automatic archiving when Status
becomes `Done`; use an empty `archive.onStatuses` array to disable that behavior. Running
`wrighty init` is preferred because it also creates or validates the backend resources.

`defaultPickFrom`, `defaultPickTo`, and `defaultFinishTo` control the composite agent workflows.
`finish` uses `defaultFinishTo` unless `--status` is supplied.
`worker.workspaceMode` sets the default worker workspace behavior to `current`, `shared`, or
`worktree`. An explicit `wrighty worker --workspace-mode ...` overrides it. When neither is set,
the mode is `current`.

`worker.completion.commit` (`inspect` default, or `agent`) decides whether a worktree worker's
agent leaves changes uncommitted for review or commits them before finishing, and
`worker.completion.integration` (`none` default, `merge-local`, or `push-pr`) selects the
completion guidance rendered after finish. Wrighty never executes merge, push, or PR creation.
See [Autonomous worker mode](worker.md#branches-worktrees-and-the-workspace-lifecycle).

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
