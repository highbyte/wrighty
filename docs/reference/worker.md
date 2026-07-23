# Autonomous worker mode

`wrighty worker` schedules one explicitly eligible item at a time, claims it with a fenced handle,
starts Claude Code, Codex, or Copilot headlessly, renews the claim for a fixed budget, and records
the workspace and vendor session address. Wrighty is the scheduler; the vendor CLI remains the
agent runtime.

Worker mode runs an unattended agent with broad permissions. Start with a dry run and one item:

```shell
wrighty create --title "Automate this" --body "..." --auto --agent claude
wrighty worker --dry-run --once --agent claude
wrighty worker --once --agent claude --workspace-mode worktree --item-timeout 30m
```

Dry runs never claim an item or start an agent and do not require confirmation. Before a live run,
Wrighty performs a read-only preflight and reports how many items are currently claimable plus the
first candidate. With `--once`, no claimable item means it prints the candidate diagnostics and
exits without a risk warning or confirmation prompt. A continuous worker prints the same complete
initial diagnostics, confirms once because it may process future items, and then uses compact
one-line `idle` events while polling. Non-interactive and JSON live runs must acknowledge the risk
with `--yes`. Preflight is only a snapshot; the contention-safe atomic pick still occurs after
confirmation.

Eligibility is opt-in. Local Markdown stores managed `wrighty-auto: true` and optional
`wrighty-agent`; GitHub uses `wrighty:auto` and `wrighty:agent=<vendor>` labels. Vendor resolution
is `--agent`, then the item preference, then `worker.defaultAgent`; Wrighty errors instead of
guessing. A generic worker started without either `--agent` or `worker.defaultAgent` prints an
informational notice that only item-pinned work can run. If automation-enabled items without a
resolved agent later appear during continuous polling, Wrighty reports that changed condition once
and then returns to compact idle messages. `--filter key=value` adds AND filters, `--max-items`
bounds spend, `--idle-timeout` bounds idle waiting, and `--json` emits one JSON lifecycle event per
line. `wrighty worker --check` runs a short, read-only vendor probe and verifies a usable session
handle; the probe still invokes the vendor and may incur usage.

`wrighty init` ensures the GitHub worker labels exist for every supported vendor and, unless
`--skip-issue-forms` is selected in the approved initialization plan, scaffolds a backlog form, a
default-agent worker form, and one agent-specific form per vendor. The default-agent form applies
only `wrighty:auto`; the worker must resolve its vendor from `--agent` or `worker.defaultAgent`. Agent
availability remains a property of the worker machine: choosing a form records intent, while worker
preflight still reports a missing or unsupported local vendor executable. Use
`wrighty init --skip-issue-forms` when the repository manages its own issue-template experience.
Interactive initialization asks whether to commit and push forms it changed. Automation must opt in
with `wrighty init --yes --publish-issue-forms`; `--yes` by itself does not publish repository files.
Wrighty's generated chooser configuration disables blank issues for contributors, although GitHub
continues to expose a maintainer-only blank option to users with Write access or above.

## Terminal color and machine output

Human worker output uses semantic color on event prefixes when `--color auto` (the default) detects
that the individual output stream is an interactive, ANSI-capable terminal. Standard output and
standard error are detected independently. Redirected output and writers without declared terminal
capability remain plain text.

Use `--color never` for durable human-readable logs, or `--color always` when an explicit consumer
such as `less -R` should receive ANSI sequences:

```shell
wrighty worker --yes --color never >worker.log 2>&1 &
wrighty worker --yes --color always | less -R
```

In automatic mode, the presence of `NO_COLOR` or `TERM=dumb` disables color. Explicit
`--color always` or `--color never` overrides those automatic checks. `--color always`
deliberately writes ANSI sequences even when human output is redirected.

`--json` always wins over color selection: every standard-output line remains unstyled JSON under
`--color auto`, `always`, and `never`. A background NDJSON worker can be started safely with:

```shell
wrighty worker --yes --json >>worker.ndjson 2>>worker-errors.log &
```

Color changes only the trusted event or warning prefix and resets immediately. Event names and
all existing text remain present, while paths, arguments, messages, session IDs, and operator
commands are never wrapped in styling. Color selection does not affect confirmation or `--yes`.

Worker dispatch state is separate from workflow status and eligibility. Wrighty manages
`wrighty-worker-state` locally and `wrighty:worker-state=<state>` on GitHub; operators should use
the CLI or web controls rather than edit it directly:

| State | Meaning | Continuous-worker behavior |
| --- | --- | --- |
| absent | Ordinary item | Eligible from `Todo` when `wrighty-auto=true`; this preserves compatibility with existing auto-tagged items. |
| `needs-attention` | A vendor session stopped for clarification or another operator decision | Shown prominently, but never retried automatically. |
| `queued` | Clarification is saved and the recorded session is ready to continue | Resumed before fresh `Todo` work. |

`wrighty-auto` remains the durable permission for unattended execution. Queuing is a deliberate
one-time dispatch decision; it does not require toggling automation off and back on.

The Local Markdown web editor exposes these managed values as **Eligible for worker processing**
and **Preferred agent**. If no item can be claimed, the worker reports how many active items it
considered in the source status, how many lack `wrighty-auto` or an item-level `wrighty-agent`,
how many filters excluded, how many cannot resolve a supported agent, and how many otherwise
eligible items were unavailable because of an active claim or claim contention.

Preassigned Claude and Copilot handles are stable for one claim generation but change when an item
is acquired again; deliberate continuation uses the session ID recorded on the active claim.

Workspace handling is a worker setting, not a work-item field. Resolution is the explicit
`--workspace-mode` option, then `worker.workspaceMode` in `.wrighty.json`, then `current`:

| Mode | Directory | Concurrency behavior |
| --- | --- | --- |
| `current` (default) | Current repository checkout | Takes an exclusive Wrighty worker lock. A second worker targeting the same canonical directory gets `WORKSPACE_BUSY` before it claims an item or starts an agent. |
| `shared` | Current repository checkout | Explicitly disables the worker lock. Multiple workers may run there concurrently. Wrighty warns because it cannot detect or resolve file, staging, build, or commit conflicts. |
| `worktree` | Fresh directory under the configured `worker.worktreeRoot` (default `<repo>.worktrees` beside the repository) | Gives each item an isolated branch and checkout. Recommended for unattended or concurrent workers. |

`shared` is an unsafe opt-out for an operator who accepts responsibility for coordinating the
items. Agents may not recognize that a changed or staged file belongs to another concurrent agent.
Select it explicitly for one invocation, or deliberately make it the repository default:

```shell
wrighty worker --workspace-mode shared --yes
```

```json
{
  "worker": {
    "workspaceMode": "shared"
  }
}
```

Every live run resolved to `shared` prints the additional collision warning, including runs using
the configured default.

## Branches, worktrees, and the workspace lifecycle

Wrighty creates a git branch **only in `worktree` mode**: each processed item gets a fresh
worktree and a dedicated branch, both created with
`git worktree add -b wrighty-worker/<item>-<unique> <path> HEAD`. In `current` and `shared`
modes the agent works directly on whatever branch is checked out and Wrighty creates nothing.
The branch name is recorded in the machine-local session record: `wrighty get <id>` shows it,
the `finished` output prints it, and it survives claim release and expiry.

### Location and naming

Three worker settings control where worktrees live and how they are named:

| Setting | Default | Placeholders |
| --- | --- | --- |
| `worker.worktreeRoot` | `{repoParent}/{repo}.worktrees` | `{repo}`, `{repoParent}`, `{home}`, `{repoPathHash}` |
| `worker.branchFormat` | `wrighty-worker/{id}-{title}` | `{id}`, `{number}`, `{title}`, `{unique}`, `{agent}`, `{date}` |
| `worker.worktreeNameFormat` | `{id}-{title}` | same as `branchFormat` |

`{id}` is the full item slug (`local-22`, `github-owner-repo-42`); `{number}` is the bare item
number (`22`, `42`); `{title}` is a slug of the item title truncated to 30 characters;
`{unique}` is an 8-character per-acquisition fragment; `{repoPathHash}` disambiguates
same-named repositories under a shared root such as `{home}/.wrighty/worktrees`. A CI-friendly
convention like `branchFormat: "feature/{number}-{title}"` makes the push-PR completion path
rename-free.

Every expansion is sanitized to a valid git ref or directory name and capped in length.
Uniqueness is guaranteed regardless of format: when the format omits `{unique}` and the branch
or path already exists — retained worktrees from earlier runs are a normal state — Wrighty
appends the unique fragment instead of failing. Keeping worktrees inside the repository
(`{repo}/...`) is discouraged: nested worktrees are picked up by IDE indexers and build globs,
and `git clean -xdf` in the main checkout can destroy active agent work.

The branch exists from spawn time, but it only *contains* the work once something is committed
inside the worktree. Until the first commit, the branch still points at the spawn-time base
commit and the worktree's working directory holds the only copy of the changes.

### Commit policy

`worker.completion.commit` decides who commits, and the worker prompt instructs the agent
explicitly in both directions so the outcome never depends on vendor-agent habit:

| Value | Behavior |
| --- | --- |
| `inspect` (default) | The agent is told to leave every change uncommitted. The worktree is always retained as your review queue, and the finished output says so. Until you commit, the working directory is the only copy of the work. |
| `agent` | The agent is told to commit its work in logical commits referencing the item. A clean worktree is then removed on finish while the branch keeps the work; pass `--keep-workspace` to retain it anyway. |

In `current` and `shared` modes the commit instruction is never added: Wrighty does not direct
commits on the operator's own checkout.

`agent` mode depends on the vendor agent's environment actually permitting an unattended commit.
Wrighty's prompt asks for the commit, but it deliberately cannot override the agent's own
governance — a global "do not commit unless I ask" instruction, a restrictive permission mode, or
a sandbox that blocks `git commit` will all veto it. When that happens the agent leaves the change
uncommitted, git's dirty-tree guard retains the worktree, and the item safely lands in
`needs-attention` rather than being reported done. This is the intended fallback, not a failure:
the work is never lost, and you can commit it yourself or rerun with commits permitted. If you
routinely disallow unattended commits, prefer the default `inspect` policy.

### Completing a finished item

Wrighty deliberately never merges, pushes, or opens PRs. `worker.completion.integration`
(`none` default, `merge-local`, or `push-pr`) selects which guidance the finished output and the
agent skill render; execution stays with you. Because main is checked out in your primary
working copy, git will not let the worktree commit onto it directly — the flow is always
commit on the worker branch, then integrate from the main checkout:

```shell
# inspect policy: commit first, inside the worktree
cd ../myrepo.worktrees/local-22-validate-user-names && git add -A && git commit

# merge-local, from the main checkout (remove the worktree before deleting its branch)
git merge --ff-only wrighty-worker/local-22-validate-user-names
git worktree remove ../myrepo.worktrees/local-22-validate-user-names
git branch -d wrighty-worker/local-22-validate-user-names

# or push-pr, from any checkout
git push -u origin wrighty-worker/local-22-validate-user-names
```

Archive the item as the last step, from the web dashboard or with `wrighty archive` while
holding a claim; `archive.onStatuses` automates this at finish for fire-and-forget setups.

### Retained workspaces

Retained worktrees and worker branches accumulate by design: inspect-first runs, failed runs,
and merged-but-unremoved workspaces are all normal states. Two commands surface and clear them:

```shell
wrighty workspaces                    # list retained worktrees: dirty/clean, merged/unmerged, item
wrighty workspaces cleanup <id>       # remove the item's worktree and delete its merged branch
wrighty workspaces cleanup <id> --force  # discard uncommitted changes and unmerged commits too
```

The two status tokens are **orthogonal** — they measure different things:

- **`dirty` / `clean`** describes the *working tree* (`git status`): are there uncommitted
  changes in the worktree?
- **`merged` / `unmerged`** describes the *commit graph* (`git merge-base --is-ancestor <branch>
  HEAD`): are the branch's own commits already contained in the main checkout's HEAD? A branch
  with no commits of its own is trivially "merged".

Because they are independent, each workflow leaves a characteristic signature, and the completion
flow moves the worktree through them:

| After… | State | Why |
| --- | --- | --- |
| an `inspect` run | `[dirty, merged]` | the agent left the work uncommitted (dirty), so the branch still points at the spawn-time base commit and has nothing beyond HEAD (merged). This is the normal resting state, not a contradiction. |
| committing in the worktree | `[clean, unmerged]` | the work is now committed on the branch (clean tree) but not yet in main (unmerged). |
| `merge-local` / integrating and removing | (drops off the list) | the branch is merged into the main checkout and the worktree removed. |

Cleanup delegates every safety decision to git: a dirty worktree is refused
(`WORKSPACE_NOT_CLEAN`) and an unmerged branch is refused (`WORKSPACE_BRANCH_UNMERGED`); by default
Wrighty never forces either. This is why an `inspect` worktree (`[dirty, merged]`) is refused on
the worktree-remove step — the uncommitted work is protected — while its branch would delete
cleanly if the tree were clean.

`--force` overrides those two git refusals — `git worktree remove --force` and `git branch -D` —
**discarding uncommitted changes and unmerged commits**. Use it only when you know the leftover
files are disposable (for example, tool artifacts such as `.memsearch/`); for anything recurring,
prefer `.gitignore`, since ignored files never block a normal cleanup. `--force` deliberately does
**not** override an active claim: an item whose claim is still held always reports `CLAIM_HELD`,
because forcing there could pull a workspace out from under a live worker or editor. Both commands
support `--json`.

`wrighty get <id>` and the web item viewer show the same working-tree and branch state for the
one item, calculated on demand from git on the machine that holds the worktree. When the recorded
worktree is not present on the current host (or git cannot be read), the state is reported as
unavailable rather than guessed — the recorded branch and path are still shown.

### Reviewing the session

After an item is genuinely finished, Wrighty prints a `review:` command that opens the completed
vendor session interactively when its workspace still exists, plus a suggested completion prompt
that asks the agent to walk the diff, propose a commit, integrate, clean up, and archive with
your approval. The review command invokes the vendor directly, carries no Wrighty claimant ID or
token, and does not reacquire the completed item. It is always available in `current` and
`shared` modes while the checkout exists; under the `inspect` commit policy the worktree is
retained too. With `commit: agent`, use `--keep-workspace` to retain a clean successful worktree
for later review:

```shell
wrighty worker --once --workspace-mode worktree --keep-workspace
# finished: ...
#   branch: wrighty-worker/local-22-validate-user-names
#   review: cd '...' && claude --resume '...'
```

Wrighty passes the absolute original tracker configuration path to the child agent as
`WRIGHTY_CONFIG_PATH`. Consequently, Local Markdown `get`, mutation, renewal, and finish
commands operate on the authoritative original store rather than a stale copy checked out in
the agent worktree.

Renewal occurs at lease half-life and has a fixed spawn-time budget equal to `--item-timeout`. It
can never renew past that deadline, so the maximum hold after a hung run is
`--item-timeout + leaseMinutes`. On `CLAIM_STALE` or `CLAIM_EXPIRED`, the default
`--on-fenced kill` stops the process tree. `detach` is available for deliberate operator use, but a
detached process can keep editing files and is unsafe in a shared checkout.

While a vendor process is running, the worker emits a single-line operational heartbeat every five
minutes. It reports elapsed time, the current claim-expiry time, remaining fixed timeout budget,
and workspace mode:

```text
2026-07-19T14:20:00.0000000+00:00 running: local:22 [claude] — 20m elapsed; claim valid until 2026-07-19T15:00:00.0000000+00:00; timeout in 40m; workspace worktree
```

This is intentionally process-level visibility rather than an agent transcript. Wrighty does not
stream model responses, tool calls, or reasoning, and the optional web dashboard does not become an
agent frontend. In another terminal, use `wrighty get <id>` to inspect the durable claim, session,
workspace, and lease state. When the worker runs under a service or with redirected output, ordinary
process logs retain the same heartbeat and lifecycle lines.

Vendor process success is not item completion. An item is `finished` only when the agent calls
`wrighty finish` and the configured completion state is observed. If a successful agent turn exits
while its exact claim remains active, the worker emits `needs-attention`, leaves the item
`In Progress`, sets its worker state to `needs-attention`, stops renewing, and retains the
session/workspace claim until its finite lease expires. A continuous worker does not retry that
state automatically. `--once` returns exit code 10 for this outcome.

The `needs-attention` footer is organized by what the operator wants to do. In `wrighty web`, choose
**Queue for worker** directly when fixing an external permission or configuration problem requires
no work-item edit. Wrighty ends the retained same-installation claim and marks the recorded session
queued, including after that claim expires. When the requirements need clarification, choose **Take
over for editing** while its claim is active or **Claim for editing** after expiry, edit the title or
body, then choose **Save and queue for worker**. Choose **Save and hand back to <agent>** when you
instead want the interactive vendor resume command immediately. Choose **Finish** when the tracked
work is already complete. To close the item without further agent work, save it and choose
**Archive** from the item view. The web claim path preserves a complete local recorded session
across expiry.

The CLI equivalent is atomic and does not require copying claim environment variables:

```shell
wrighty edit <id> --takeover --yes --body-file requirements.md --requeue
```

`--requeue` requires a complete recorded agent/session/workspace address. It clears active human
ownership, rotates the terminal fencing generation, and marks the session `queued`. A normal
continuous `wrighty worker` scans queued `In Progress` items before fresh `Todo` candidates and
resumes the recorded vendor session. `wrighty requeue <id>` is available when the caller already
holds and supplies the exact claim handle.

After saving the clarification, continue headlessly with:

```shell
wrighty worker --item <id> --yes
```

That command works both while the current claim is active and after it expires. Wrighty infers
whether to take over the active local session, recover an expired session under a new claim
generation, or start a new session when no recorded address exists. Claim expiry invalidates
authorization, not the vendor's durable session; an expired token is never revived or reused.
Automatic recovery is limited to the installation that created the session, where its recorded
workspace and vendor state are meaningful. Another installation must use `--fresh` explicitly
after expiry.

For CLI editing while the current claim is still active, use either the interactive editor or
direct edit options:

```shell
wrighty edit <id> --takeover
wrighty edit <id> --takeover --yes --title "Clear title" --body-file requirements.md
```

The first command prompts before displacing an active claimant; the scripted example uses `--yes`.
Both also work after expiry, acquiring a new human editing claim without a takeover prompt while
preserving a recoverable local session. They apply the edit with the resulting handle inside one
Wrighty process, retain human ownership, and print the headless continuation command. No environment
variables need to be copied.

`--item <id>` processes exactly that item and chooses from claim state: an active same-installation
session is taken over and resumed; an expired session is reacquired under a new claim and resumed;
an item with no recorded session starts new. It never takes over another installation's active
claim, and it refuses to silently discard an incomplete or missing-workspace session address.
Use Boolean intent assertions when inference is not desired:

```shell
wrighty worker --item <id> --resume   # require a recoverable existing session
wrighty worker --item <id> --fresh    # require an unclaimed item and start a new session
```

`--resume` and `--fresh` are mutually exclusive and fail when current state does not match the
requested intent. Fresh starts still require normal worker eligibility and accept the configured
source or active status. Add `--dry-run` to print the inferred or asserted action without claiming,
taking over, or spawning.

For takeover, run:

```shell
wrighty takeover <id> --yes --print-resume-command
```

This rotates the fencing token and preserves the recorded vendor session/workspace address. With
`--print-resume-command`, an agent takeover prints both interactive and headless-worker alternatives;
a human takeover prints the safe headless-worker continuation. The separate
`wrighty resume-command <id>` prints only the recorded interactive vendor address without rotating
the claim; it reads the durable session record, so it also works after the item is finished or the
claim released — which is how you reopen a completed session for guided completion. It prints a
command you run in your shell; add `--exec` (macOS/Linux) to launch the recorded session directly
instead of copying and re-running the printed command. Once the session is open, paste the
guided-completion prompt Wrighty prints (a separate copy block) to have the agent summarize the
diff, commit, integrate, clean up, and archive with your approval at each step. Takeover is
limited to the same Wrighty installation. A worker elsewhere cannot be
seized on demand; wait at most
`--item-timeout + leaseMinutes` for expiry or coordinate with that installation.

The web UI provides the equivalent flow: **Take over for editing**, clarify, then choose
**Save and hand back to _Agent_** for interactive continuation, or plain **Save** for a headless
worker continuation command. Handback rotates the claim to a fresh agent claimant and displays the
environment-prefixed interactive command plus the headless alternative. Plain Save keeps human
ownership and displays only the headless command, which performs the transfer when run. For an
interactive continuation, enter the adjacent vendor-specific follow-up prompt to explicitly load
the Wrighty skill, re-read the clarified item, and continue.
Release ends ownership without discarding recovery state: the recorded session/workspace address
is a durable machine-local record that survives release and expiry, so a released item can still
be resumed later with `wrighty worker --item <id>` on the installation that recorded the session.

## Captured run outcome

When a run ends — `finished`, `needs-attention`, `failed`, `timed-out`, or `rejected` — Wrighty
records the outcome (`succeeded` / `failed` / `rejected`), the agent's final message or block
reason (truncated), and the end time onto the durable session record. This is **backend-neutral**
and overwrite-only: it survives release, expiry, takeover, and archive, exactly like the recorded
session address. It surfaces as a **Last run** block in `wrighty get` (human and `--json`), in the
web item panel above the resume/requeue actions, and in `wrighty status`. This makes the local
clarify → requeue loop self-contained: read the block reason in the web UI or `wrighty get`, edit
the description, and requeue — without opening the vendor session first.

The captured outcome also distinguishes a **completed** item from a **paused** one. An unclaimed
item whose recorded session succeeded and whose status reached the configured finish state
(`defaultFinishTo`) reports worker activity `completed` — the work landed; its primary next action
is finalize/archive, not resume. An item whose session is merely retained for later resumption
reports `paused-session`. Both keep the durable resume address; only the presentation differs, and
a `completed` item can still be reopened deliberately if its worktree is present. (The
*resumability* half is separate: `wrighty get`'s `resumableHere` is `false` and
`wrighty resume-command` refuses with `RESUME_WORKTREE_ABSENT` once the recorded worktree directory
is gone.)

## Discovering what needs attention (`wrighty status`)

`wrighty status` is the machine-side "what needs me?" surface — the primary discovery counterpart
to the web dashboard, and for the GitHub backend the surface that substitutes for it. It groups the
active items by the operator's next action:

```shell
wrighty status          # human-readable, grouped
wrighty status --json   # same groups for scripting
```

- **Needs attention** — blocked items, each with the last-run outcome and final-message excerpt and
  the clarify → requeue / continue commands.
- **Completed — retained worktree** — finished items whose worktree is still present, each with the
  branch, its `dirty`/`merged` git state, and the integration commands for the configured policy.
- **Paused — resumable session** — retained sessions waiting to be resumed, with the resume command.
- **Active** — items with a live claim (agent processing, human editing, automation).
- **Queued** — items marked to be resumed by a continuous worker.

The retained-worktree git state is calculated on demand, bounded and timeout-guarded, only for the
items in the first three groups and only on the machine that holds the worktree (it degrades to
"unavailable" off-host) — the same posture as `wrighty workspaces`. The at-a-glance
`[worktree]` marker in `wrighty list` (and the board badge in the web dashboard) flags which items
have a retained worktree without any git call; drill into `wrighty get`, the web item viewer, or
`wrighty workspaces` for the per-item `dirty`/`merged` detail.

## GitHub handover comment

For the GitHub backend the "UI" is github.com, so an issue left `In Progress` with the
`wrighty:worker-state=needs-attention` label tells the operator nothing on its own. When a run ends
in `needs-attention`, or finishes with a **retained** worktree, the worker posts (or overwrites) a
single marker-identified handover comment on the issue:

- **What happened** — outcome and the agent's final message / block reason.
- **Where** — host label, branch, and (only when `shareLocalPaths` is enabled) the workspace path.
- **Next actions** — copy-paste command blocks: open the recorded session
  (`wrighty resume-command github:owner/repo#42`), the clarify → requeue loop, the cross-machine
  takeover path, and — for the done phase — the plan-020 completion commands (review diff, guided
  completion, merge-local / push-pr).

It is a **single comment per issue**, found by the `<!-- wrighty-handover:v1 -->` marker and
**edited in place** on subsequent runs (no comment spam; GitHub's edit history preserves the
trail). It is trimmed to a short "resolved" form when the item is requeued, archived, or its
workspace is cleaned up, so stale instructions do not linger. Posting is **best-effort** — a
failure to write the comment never fails the run.

Configure the exposure with `worker.handoverComment`:

| Value | Behavior |
| --- | --- |
| `full` (default) | includes the branch and the host label (and the workspace path when `shareLocalPaths` is enabled) |
| `minimal` | omits local machine details (host, workspace path); keeps the branch |
| `off` | posts nothing |

Wrighty defaults to the **least-disclosure** posture, so on a fresh install neither the workspace
path nor the real machine name leaves the machine:

- **Workspace path** — `worker.shareLocalPaths` defaults to `false`. The absolute path (which embeds
  the OS username) is not published on any of the three GitHub surfaces:
  - the claim marker carries no workspace path (the real path stays only in the machine-local
    session cache, which is authoritative for resume on the recording host — resume is unaffected);
  - the Project workspace-path field is not written;
  - the handover comment omits the workspace path from its "Where" line and uses path-free
    completion commands (`wrighty resume-command <id> --exec`, `wrighty workspaces cleanup <id>`),
    which resolve the retained worktree locally on the recording host.

  Set `worker.shareLocalPaths: true` only when every collaborator with repository access is trusted
  to see local machine paths; then the raw `cd '<path>' …` / `git worktree remove '<path>'` commands
  are published instead. The branch name (e.g. `wrighty-worker/local-5-…`, no username) is always
  published, since `git merge --ff-only <branch>` needs it. `shareLocalPaths` has no effect on the
  Local Markdown backend, whose paths never leave the machine.

- **Host** — with no configured label the comment shows the placeholder `anonymous`; the real
  machine name (`Environment.MachineName`, which often embeds a person's name) is never published by
  default. To publish a symbolic name that is meaningful to you but reveals nothing, set a
  user-scoped host label:

  ```shell
  wrighty config set-host "workstation-alpha"   # published instead of 'anonymous'
  wrighty config show                            # show the label and the effective host
  wrighty config set-host --clear                # revert to the 'anonymous' placeholder
  ```

  The label is stored in a durable, user-scoped settings file (macOS
  `~/Library/Application Support/wrighty/settings-v1.json`, Linux `~/.config/wrighty/…`, Windows
  `%APPDATA%\wrighty\…`; override the directory with `WRIGHTY_CONFIG_DIR`), not in the per-repo
  `.wrighty.json`. It applies to every repository this installation works. See
  [user settings](user-settings.md) for the full reference.

`minimal`/`off` remain available to suppress the host label and branch from the comment entirely,
independent of `shareLocalPaths`.

### The two-path resume model

A recorded vendor session is bound to the **host that ran it**. From that machine, resume it
(`wrighty resume-command <id>`, or continue headlessly with `wrighty worker --item <id> --yes`).
From **any other machine**, the recorded workspace and vendor session are not meaningful, so start a
fresh session instead:

```shell
wrighty takeover <id> --yes --print-resume-command
```

The handover comment states the bound host label explicitly (and the paths when `shareLocalPaths`
is enabled), turning the common "which machine?" confusion into an explicit choice.

> **Do not hand-edit the `wrighty:worker-state` label on GitHub.** Flipping
> `needs-attention` → `queued` in the GitHub UI bypasses the claim protocol (no claim event, no
> token rotation). Always requeue with `wrighty requeue <id>` (or `wrighty edit … --requeue`).

## Verified vendor capability matrix

Verified on 2026-07-16 with Claude Code 2.1.210, codex-cli 0.144.1, and GitHub Copilot CLI 1.0.71:

| Capability | Claude | Codex | Copilot |
| --- | --- | --- | --- |
| Headless start | `-p` | `exec` | `-p` |
| Machine output | JSON | JSONL (`--json`) | JSONL |
| Session handle | preassigned UUID | parsed from `thread.started` | preassigned name |
| Headless resume | `-p --resume` | `exec resume` | `-p --resume=` |
| Working directory | process cwd | `-C` | `-C` |
| Autonomy | `--dangerously-skip-permissions` | headless sandbox | `--allow-all-tools` |

These CLI surfaces are version-sensitive. Validate vendor upgrades in a throwaway repository before
unattended use.
