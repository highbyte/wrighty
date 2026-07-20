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

For GitHub, `wrighty init` creates **Current agent type** as a single-select field and
**Current session ID** and **Creation attempt ID** as text fields, repairs missing standard agent
options, and refreshes the local node-ID cache. Existing compatible fields are reused. Duplicate
names or incompatible field types are reported without being changed.

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
