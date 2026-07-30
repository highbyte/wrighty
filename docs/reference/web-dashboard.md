# Local web dashboard

The Local Markdown backend includes an offline dashboard for developers. Run it from the directory
containing `.wrighty.json` or any child directory:

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

The web command currently supports only `backend: local-markdown`. It serves all browser assets from
the executable and makes no CDN requests. The server stops with Ctrl+C. Failed web requests are
logged to the same terminal with the HTTP method, safe request target, status, Wrighty error code,
and exception details. Launch and claim tokens are never logged. The authenticated dashboard header
intentionally displays the workspace root; error responses continue to redact it. Agents and
scripts should continue to use the stable CLI/JSON contract rather than automate this
developer-facing HTML surface.
