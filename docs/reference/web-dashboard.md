# Web operations console

`wrighty web` is one secured, machine-local operations application for both tracker backends. Run
it from the directory containing `.wrighty.json` or any child directory:

```shell
wrighty web
```

Wrighty binds an ephemeral port on `127.0.0.1` and, when IPv6 is available, `::1`; prints the IPv4
loopback address; and opens the dashboard in the default browser. Use a fixed port or keep the
browser closed when needed:

```shell
wrighty web --port 8080
wrighty web --no-open
```

The console is organized as page-level tabs under the header, so every section is discoverable
without scrolling: **Board** (Local Markdown only), **Operations**, and **Settings**. The GitHub
backend has no board and opens on **Operations**. A needs-attention badge on the Board tab (the
Operations tab for GitHub) keeps paused items visible from any tab, and the selected tab is kept in
the URL fragment so a refresh or shared link reopens the same section.

Both backends show typed repository configuration, stored-versus-active revisions, local worker
processes, operational item groups, retained-session recovery state, and provider capacity. The
GitHub surface is deliberately read-only for work items: use the configured GitHub repository and
Project for issue content and Project fields. Its **Validate GitHub target** action explicitly runs
the read-only initialization check; merely opening or refreshing the console does not create or
change GitHub resources.

Local Markdown adds its existing board, item viewer, and claim-aware editor to the shared
operations surface. GitHub never renders or authorizes those Local-only item mutation routes.

[![Local Markdown Operations tab showing operational items and a running local worker](../assets/screenshots/local-markdown-web-ui-operations.png)](../assets/screenshots/local-markdown-web-ui-operations.png)

The Local Markdown Operations tab complements the board with process and recovery state; it does
not replace the board or start and stop workers.

## GitHub context approval

For the GitHub backend, **Operations → Operational items** includes a compact
**Context** projection from the Project field. It does not load issue conversations during routine
dashboard polling. Choose **Inspect** on one item to perform the full, content-free approval read
in a right-side details drawer. The drawer shows the current result code, Project-field state,
approval source and cutoffs, revision digest, decision counts, and links to comments still awaiting
a decision. The initial **Approved (*)** badge and its tooltip identify the lightweight projection;
after inspection, that row shows the authoritative result for the inspected revision without the
asterisk.

[![GitHub repository control plane showing operational items, context status, and a running local worker](../assets/screenshots/github-web-ui-operations.png)](../assets/screenshots/github-web-ui-operations.png)

The GitHub Operations tab is a local control plane beside GitHub's Project board, not a second item
editor.

The details drawer offers **Approve context** or **Reapprove context**. This protected, confirmed POST
cycles the Project field through **Needs review** and back to **Approved**, establishing a new base
approval and batch cutoff, then reloads the same diagnostics the next worker launch will use. The
action does not claim the item, change execution policy, or start a worker. A later title or body
edit remains stale until the edit-invalidation workflow resets the Project field or a current
approval supersedes that edit.

<a href="../assets/screenshots/github-web-ui-operations-context-approve.png"><img src="../assets/screenshots/github-web-ui-operations-context-approve.png" width="620" alt="GitHub context-approval drawer refusing unapproved context and offering Approve current context"></a>

This example is deliberately in **Needs review**: unresolved approval means no GitHub content is
sent to an unattended agent until an authorized operator reviews and approves the current context.

Local Markdown deliberately has no Context column or approval action. Its machine-local title and
body are approved by definition, and the backend has no discussion stream to decide.

The **Settings** tab holds **Repository settings** (shared, in `.wrighty.json`) and **User
settings** (yours, stored in your user profile on this computer). Repository settings expose only typed workflow, archive, worker, completion, and web-policy
forms. Each submission carries the raw-file revision and edits only the configuration path loaded
at process startup; the browser cannot supply a different path. A concurrent manual or CLI edit
returns `CONFIG_CONFLICT`. A successful save does not hot-reload this process or running workers:
the console keeps showing its active revision, compares registered worker revisions, and displays
restart guidance until the affected processes restart. Malformed base configuration still prevents
normal startup and must be repaired through the CLI or manually.

## Viewing the dashboard from another machine

Keep Wrighty on loopback whenever a tunnel or browser relay is available. A cmux remote-SSH
workspace can route its browser pane to the remote loopback service directly. With ordinary SSH,
forward a local port to the remote loopback endpoint instead of exposing Wrighty on a network
interface.

When no tunnel is available, an operator can deliberately bind one specific address assigned to the
machine, such as its Tailscale address:

```shell
wrighty web --bind 100.100.100.100 --port 8080
```

Wrighty refuses `0.0.0.0`, `::`, and addresses not assigned to a local interface. A non-loopback
server still uses plaintext HTTP and prints a warning on every start. Token authentication remains
enabled, but possession of the launch URL grants dashboard access; use this mode only on an
encrypted or trusted transport such as Tailscale.

Direct access by an intentional DNS name requires each exact name to be allowed:

```shell
wrighty web --bind 100.100.100.100 --allow-host wrighty.example.ts.net
```

`--allow-host` is repeatable and accepts no wildcard. It extends both Host and direct-HTTP mutation
Origin validation; it does not change the listening interface or infer DNS names. Bind address,
allowed hosts, port, browser opening, authentication, token lifetime, and public URL are
per-invocation machine settings. They are not stored in `.wrighty.json` or managed by
`wrighty config`.

## Authentication and token lifetime

By default, each `wrighty web` process creates a new random launch token. The browser captures it
from the URL fragment, removes the fragment, and keeps the token in origin-scoped `sessionStorage`.
It therefore survives refreshes in that browser tab/session without becoming a cookie or a
longer-lived `localStorage` credential. An authentication failure clears the stored token; reopen
the URL printed by the running server to authenticate again.

After authenticating one browser, use **Copy access link** in the dashboard header to copy a full
URL for another browser or Tailscale-connected computer. The browser reconstructs the URL locally
from its current origin and in-memory token; Wrighty does not expose a token-retrieval endpoint.
The copied URL is a bearer credential, so share and store it accordingly. If no browser is already
authenticated, copy the `Open` URL printed when `wrighty web` starts.

For a stable single-user service, explicitly opt in to a managed persistent token:

```shell
wrighty web --persist-token
```

The token is stored outside the tracker repository at
`~/.wrighty/webui/<tracker-slug>-<root-hash>/token` on Unix or
`%LOCALAPPDATA%\Wrighty\webui\<tracker-slug>-<root-hash>\token` on Windows. Wrighty creates managed
directories and files with user-only access (`0700` and `0600` on Unix, user-only ACLs on Windows)
and refuses unsafe existing permissions. The root hash distinguishes same-named checkouts; moving
the tracker intentionally selects a new managed token.

Use `--token-file <path>` to select a persistent token location outside the tracker, or add
`--rotate-token` to either persistent mode to replace its token before the server starts:

```shell
wrighty web --persist-token --rotate-token
wrighty web --token-file /secure/operator/path/wrighty-token
```

A copied persistent launch URL remains a bearer credential until the token is rotated or deleted.
Wrighty never stores the launch token in `.wrighty.json`.

`--auth none` is available only for deployments where reachability itself is the intended access
control. It is incompatible with persistent-token options and prints a strong warning even on
loopback. Every client that can reach the Wrighty socket can then read and mutate the tracker; Host
and mutation-Origin validation still apply, but they are browser attack defenses rather than client
authentication.

## Reverse proxy

Keep the Wrighty backend on loopback and explicitly name the proxy's public origin:

```shell
wrighty web --public-url https://wrighty.example
```

`--public-url` adds exactly that authority to Host validation, adds exactly that origin to
mutation-Origin validation, and controls the printed launch URL. It does not make Wrighty serve TLS,
change the listening address, or trust `Forwarded`/`X-Forwarded-*` headers.

If the proxy performs authentication and Wrighty runs with `--auth none`, the loopback backend must
be inaccessible except through that proxy. Proxy authentication is ineffective when clients can
bypass the proxy and connect to the Wrighty socket directly.

The header identifies the resolved workspace/configuration root used by the dashboard. Paths inside
the current user's home directory are shortened with `~`; paths anywhere else remain absolute. Long
paths are visually truncated, with the complete path available from the header tooltip.

The dashboard's **New item** action opens a structured Local Markdown creation form. Status defaults
to `defaultPickFrom`; execution policy is off by default; and a agent policy does not imply
eligibility. **Create item** uses the ordinary retry-safe creation pipeline. It never claims the
new item, starts a worker, or launches a vendor agent.

The item editor's **Execution policy** section carries automatic execution, agent policy, and —
when the repository configures an execution-profile vocabulary — **Execution profile**. A repository
that does not use profiles sees no such control. The choice applies to the item's next fresh run; a
recorded session keeps the model and effort it started with. See
[Execution profiles](execution-profiles.md).

The dashboard also shows configured status columns, priority and claim state, supports active/archived
filtering, and renders each item's Markdown. A developer can claim an item, edit its structured
title/body/status/priority fields, save and release it, finish it, or archive it. YAML frontmatter is
never exposed as editable content. If the file changes after an edit form was opened, Wrighty keeps
the browser draft and shows the current version beside it instead of overwriting either version.

Claims belonging to another claimant session are read-only in the web application. For a claim on
this installation, **Take over for editing…** confirms an explicit transfer and opens the editor
only after the browser session owns the new token generation. **Release existing claim…** clears an
abandoned same-installation claim without taking it over. The legacy `protectNonHumanClaims`
setting no longer weakens authorization: claimant fencing is always enforced.
When a worker has recorded `needs-attention`, the item view identifies the headless process as
exited while describing the retained claim as ownership and fencing metadata. If the item is still
eligible, `In Progress`, and has a complete local resume address, **Queue for worker** ends that
retained claim and queues the session without opening or saving the edit form. The transition is
validated with the dispatch state and claim under the same Local Markdown store lock, so it refuses
to queue if a worker has already resumed and cleared `needs-attention`.
For a resumable agent claim, plain **Save** retains human ownership. **Save and resume
automatically** queues the recorded session for a continuous worker. **Save and show manual
_Agent_ resume command**, under **More actions…**, performs a second fenced transfer to a fresh
agent claimant and only then exposes the agent-scoped interactive resume command. After plain Save,
the web UI instead exposes a copyable
`wrighty worker --item <id> --resume --yes` command that explicitly performs that transfer and continues the
recorded session headlessly under worker supervision.

On macOS, the session panel also provides deliberate local launch actions when the recorded
workspace still exists on this installation:

- **Open _Agent_ CLI** opens the adapter-built resume invocation in a new Terminal window. It is
  available only after the dashboard owns the exact current agent claim, so the new process
  receives that claimant ID and fencing token.
- **Open _Agent_ Desktop** opens an allowlisted per-session deep link. For active work, take over as
  human first; the human claim remains active while Desktop is open. An unclaimed completed session
  may be opened without a claim for review.
- Codex and GitHub Copilot have supported address shapes; the corresponding application and URI
  handler must be installed. Before opening Copilot Desktop, change **Settings → Sessions → Show
  Copilot CLI Session** from **Off** to a retention period that includes the recorded session.
  Wrighty cannot detect this setting. Some Copilot Desktop versions may open Home instead of the
  recorded CLI session; this does not alter the recorded session, and **Open Copilot CLI** remains
  available as the reliable fallback. Claude's resume link is offered by default and remains
  labeled experimental where it is offered; set `worker.desktopSessions.claude` to `off` to
  withdraw it.
- Copyable interactive and headless commands remain visible as fallbacks where their ownership
  rules permit them.

Every launch is a confirmed, form-encoded POST protected by the dashboard's configured
authentication plus its Host and exact-Origin checks. The browser submits only the item ID and the
expected session fingerprint; the server reloads the durable address and constructs the executable
or URI from the fixed vendor adapter. Wrighty rejects a changed session, ownership generation,
missing workspace, unsupported platform, or unavailable application before launch.

Desktop is a human-supervised phase: it cannot receive a reliable per-session Wrighty process
environment. Stop or idle Desktop/CLI before **Save and resume automatically** or another
hand-back action. The confirmation is a coordination acknowledgement; Wrighty cannot detect every
independently opened vendor client. These explicit local actions currently run Wrighty's built-in
session, ownership, workspace, vendor, and platform checks. Configurable custom launch gates are a
later workflow-extension phase.

When a run has ended, the item panel shows a **Last run** block above the resume/requeue actions:
the outcome (`succeeded` / `failed` / `rejected`), when it ended, and the agent's final message or
block reason. This makes the clarify → requeue loop self-contained — read the block reason, edit
the description, and requeue without opening the vendor session first. A finished-and-landed item
shows a **Completed** callout (its next action is finalize/archive), distinct from the paused-session
"needs attention" state (waiting to be resumed).

The header's compact **Provider capacity** control sits immediately before the connection indicator
and combines probe actions and circuit state for every configured agent. Its summary reports active
probes and unavailable providers; expanding it opens an anchored popover with one responsive row
per agent containing status, known retry/probe time, sanitized reason, and action. The popover's
**Probe all** action checks all configured providers concurrently. It consumes no board height, and
multiple circuits or probes remain inside the same popover. A probe can run whether or not a circuit
is open. Each confirmed action starts only the selected provider's bounded check request, or one
request per provider for **Probe all**: no item is claimed or changed. A
successful/non-capacity response leaves or makes capacity available; a usage-capacity response
opens or extends the circuit.

Otherwise-ready cards resolved to that provider show **_Agent_ unavailable**, and their item panel
explains that automatic workers will leave the item unclaimed. An intentional
`wrighty worker --item <id> --yes` run remains the item-specific override. The header popover and
affected item panel also provide **Probe _Agent_ now**. Each form allows up to 130 seconds for the
vendor check and disables its submit button while running. While the shared probe lease is active,
all probe locations instead show a disabled **Probe in progress** button, including after a refresh
or in another browser tab. A proactive probe temporarily pauses automatic work for that provider.
Provider state participates in the board and header-fragment revisions, so normal dashboard
refreshes update both cards and the popover when a probe finishes even when no item file changed. The provider record is
machine-local and is not published into Local Markdown frontmatter or GitHub.

Board cards and the item panel show a **worktree** badge when a worker worktree is recorded for the
item — an at-a-glance signal derived from the session record with no git call. The per-item
`dirty`/`merged` state stays on the item viewer (and `wrighty get` / `wrighty workspaces`), where the
git probe is bounded to a single item.

The web command serves all browser assets from the executable and makes no CDN requests. The server
stops with Ctrl+C. Failed web requests are
logged to the same terminal with the HTTP method, safe request target, status, Wrighty error code,
and exception details. Launch and claim tokens are never logged. The authenticated dashboard header
intentionally displays the workspace root; error responses continue to redact it. Agents and
scripts should continue to use the stable CLI/JSON contract rather than automate this
developer-facing HTML surface.
