# Agent skills

The bundled `wrighty` skill lets you manage work and workers from Codex, Claude Code, GitHub
Copilot, or OpenCode. It is a conversational alternative to the web console for everyday board
and processing operations, and can also help author or implement a work item in the current
agent session. It operates through the Wrighty CLI against the same items and claims as the web
console; switching surfaces requires no synchronization.

## Capabilities by backend

Workflow status describes where an item sits in planning. Operational state describes its
processing: for example, an In Progress item may be working, awaiting clarification, or scheduled
to retry. The skill keeps these views separate, just as the web console separates Board from
Operations.

| Ask the skill to… | Local Markdown | GitHub |
| --- | --- | --- |
| Summarize items by workflow status, including counts and IDs | Yes; a conversational view of the Local Markdown board | Yes; reads tracked issues and configured Project statuses. GitHub Project views remain the planning board |
| Explain processing state, blockers, claims, and available actions | Yes | Yes; exact local session and workspace details require the recording installation |
| Create, inspect, or implement a work item | Yes, through the existing claim-aware CLI workflows | Yes, through the existing claim-aware CLI workflows |
| Queue, Send back, or Resume through the Board workflow executors | Yes, for eligible items | Not supported by these executors; use the appropriate GitHub planning or Wrighty continuation workflow |
| Apply those workflow actions in a reviewed batch | Yes, on the exact frozen selection | Not supported; the skill should explain the limitation without applying a substitute mutation |
| List workers and assess pickup for an item | Yes, within the local configuration scope | Yes, within the local configuration scope; this is not a list of workers on every GitHub-connected computer |
| Start an exact-item, next-item, or bounded queue worker | Yes, when authorized and the host can own the process | Yes, subject to the same launch checks, including GitHub execution/context approval |
| Start continuous processing | Instructions for the user to start it in a terminal or web console; the skill does not launch it | Same boundary |
| Inspect, drain, or interrupt a registered worker | Yes, for verified local CLI or web-hosted runs | Yes, for verified local CLI or web-hosted runs |

The web **Board** and general item editor exist only for Local Markdown. With GitHub, manage
planning in Issues and Project views; web **Operations** remains available for worker and item
processing state on both backends. An unsupported Board action does not mean the GitHub backend
cannot process or continue work. Inspect the item's available actions and use the supported
[GitHub workflow](../workflows.md#approve-github-context-and-invalidate-edits) or
[targeted continuation](worker.md#the-two-path-resume-model). Generic status moves are not
substitutes for Queue, Send back, or Resume semantics.

## Example requests

These examples use Codex's `$wrighty` invocation. Use the equivalent form for your
[agent surface](#supported-skill-surfaces), and replace IDs with those returned for your tracker.

```text
$wrighty Show active work grouped by status, with counts and canonical item IDs.
$wrighty What needs attention? Separate blocked items from scheduled retries.
$wrighty Can an existing worker pick up this item? Explain the evidence; do not start one.

# Local Markdown workflow actions
$wrighty Queue local:12 for automatic processing. Do not start a worker.
$wrighty Send local:19 back to the backlog.
$wrighty Resume the recorded session for local:23 by queueing it for a worker.
$wrighty Preview queueing the Todo items in area=api. Show exactly what would change first.

# Worker operations on either backend
$wrighty Process this exact item with the configured worker agent and a 30-minute item timeout.
I authorize unattended processing of this item only. Keep the run attached and report the outcome.
$wrighty Process at most three eligible items with a 30-minute item timeout and a five-minute
idle timeout. Keep the run attached and report the results.
$wrighty Show me how to start a continuous worker in my terminal.
$wrighty Stop worker <run-id> after its current item; let that item finish.
$wrighty Interrupt worker <run-id> now, including its active agent. Report any recovery needed.
```

The skill uses the configured workflow statuses and reports filters or incomplete counts.
Queueing, resuming a recorded session for a worker, and starting a worker are separate effects.
Requests that authorize an effect can proceed without repeated confirmation; a preview alone
does not authorize execution. Batch results identify applied, skipped, failed, or uncertain items.
With the worker-queue policy enabled, Queue authorizes automatic processing and Send back revokes
that authorization. If the policy is disabled, execution permission remains a separate setting.
See [workflow actions](actions.md) for eligibility and revalidation.

Worker pickup is advisory: “could pick up” is not a reservation or a timing guarantee, and
“unknown” is not permission to launch a duplicate. Worker inspection covers registrations in the
current local configuration; remote, unregistered, and other-configuration workers can be outside
that view. See [worker discovery and control](workers.md).

## Example responses

These exchanges were captured from Codex sessions using the bundled Wrighty skill against test
trackers. The responses retain their Markdown formatting; the GitHub example is an excerpt.
Each example uses a separate tracker state. Item names and IDs belong to the test data, and
wording and layout can vary by agent and session.

### Board overview and worker availability

This Local Markdown request only inspects the board and registered workers.

**You:**

> $wrighty Show my active board grouped by workflow status. Include the total count in each column and the canonical item IDs. Also tell me whether any workers are running. This is an overview only.

**Agent:**

> Your active board has **3 items**, excluding archived items.
>
> | Workflow status | Total | Items |
> |---|---:|---|
> | Todo | 2 | `local:1` — Alpha API<br>`local:2` — Beta UI |
> | Worker queue | 1 | `local:3` — Gamma tests |
> | In Progress | 0 | — |
> | Done | 0 | — |
>
> **Workers:** No registered workers were found in this board’s local configuration. Remote or unregistered workers are outside this check’s scope.
>
> No changes were made.

### Queue specific items without starting a worker

This Local Markdown request authorizes queueing exactly two items. Queueing allows automatic
processing; it does not itself launch a worker. An eligible existing worker may pick up the items.

**You:**

> $wrighty Queue local:1 and local:2 for automatic processing. I authorize that queueing and eligibility change for exactly those two items. Leave local:3 unchanged, and do not start a worker.

**Agent:**

> Queued `local:1` and `local:2` in **Worker queue** with automatic processing enabled.
>
> Verified `local:3` is unchanged. No worker was started.

### Explain an unsupported GitHub batch

This GitHub request asks for a preview, without authorizing changes. The response excerpt below
omits the preceding item table and preserves the explanation of the backend limitation.

**You:**

> $wrighty Preview queueing all Todo items for automatic processing as one reviewed batch. Show the exact selection and consequences before any execution. Do not change items or start a worker.

**Agent (excerpt):**

> **Batch preview is unsupported on this GitHub backend.** Wrighty returned `NOT_SUPPORTED`: “Batch workflow actions require Local Markdown.” Individual Queue consequence inspection is also unsupported.
>
> No frozen preview was created, so execution eligibility and exact consequences could not be verified. Nothing was changed, and no worker was started.

For GitHub, use Issues and Project views for planning, and the supported Wrighty operations
described in [capabilities by backend](#capabilities-by-backend) for processing and worker control.

## Worker ownership and stopping

The skill launches only finite runs attached to their invoking command. It defaults to
`worker --item ID` for an exact item or `worker --once` for the next eligible item. An explicitly
requested bounded queue run may use `--max-items N`, with item and idle timeouts. That limits the
number processed, not total elapsed time or the identities of the selected items: the worker
selects eligible work as it proceeds. It is different from a frozen batch of workflow actions.

**Start continuous workers yourself**, either in your terminal or with **Start worker** in the
web console. The skill supplies instructions and states that it has not started the process;
it does not launch a continuous worker even if the agent host offers a retained terminal. An
idle timeout alone does not change this rule. It also does not repeat finite runs to simulate
continuous processing. The skill can inspect, assess pickup from, drain, and interrupt workers
you have already started.

For allowed finite runs, if the agent surface cannot keep the command attached through
completion, the skill supplies the command for you to run instead. This is a skill guidance
boundary; the CLI continues to support continuous workers for terminals and the web host.

A conversation does not guarantee background execution or monitoring after task cancellation,
application exit, logout, or restart. There is no Wrighty detach command, service installation, or
startup/restart management in this workflow. Worker-spawned implementation sessions cannot
recursively start workers.

**Drain** closes intake and lets the current item finish. **Interrupt** cancels the current agent
process tree and performs interruption bookkeeping; it does not mark the item successfully
completed. Both address a verified run ID, including individual web-hosted runs that share a
process. The skill distinguishes an accepted stop request from completed shutdown, checks the
item outcome, and does not fall back to killing an OS process. See [worker control](workers.md#cooperative-control).

## Install and maintain the skill

Run your agent in the configured project with access to the `wrighty` executable. For GitHub,
the agent's command environment also needs network access and an authenticated `gh` CLI with
the repository/Project permissions described in [configuration](configuration.md#initialize-the-github-backend).
Installing a skill does not grant those permissions. If tracker access fails, the agent should
report which observations are unavailable rather than invent counts or eligibility.

Install for a selected agent, or all supported destinations:

```shell
wrighty skill install --agent codex
wrighty skill install --agent claude
wrighty skill install --agent copilot
wrighty skill install --agent opencode
wrighty skill install --agent all
```

Omitting `--agent`, or passing `--agent auto`, targets every supported agent CLI installed on the
current machine. Codex, Copilot, and OpenCode use the same
`.agents/skills/wrighty` destination once. With no supported CLI installed, automatic selection
fails with `SKILL_AGENT_NOT_INSTALLED`. Use explicit `claude`, `codex`, `copilot`, or `opencode` when you want
one destination, or `all` when you deliberately want every supported destination regardless of
which CLIs are installed.

User scope is the default because one installation then works across repositories and worktrees.
Use `--scope project` only when a repository deliberately needs its own copy. Project scope
resolves to the Git root when available and otherwise the current directory; `--project-dir PATH`
chooses another project root. Codex, Copilot, and OpenCode share `.agents/skills/wrighty`; Claude
uses `.claude/skills/wrighty`. An `all` installation creates those two physical copies under the
selected scope.

Project-scoped skills intended for worktree workers must be committed. A Git worktree contains the
selected commit, not ignored or merely untracked files. The default user installation avoids that
worktree coupling:

```shell
wrighty skill install --agent all --scope user
wrighty skill check --agent all --scope user
```

Before a `worktree` worker claims an item, Wrighty verifies that the selected agent has either a
user-scoped skill or the required project skill in `HEAD`. An ignored project copy is deliberately
rejected with `WORKER_SKILL_UNAVAILABLE`; Wrighty does not silently copy or install executable
agent instructions into a new worktree.

Do not routinely install the same physical skill target at both scopes. Agent hosts do not share a
portable precedence rule: Claude gives its user skill precedence, Copilot and OpenCode give the
project copy precedence, and Codex may expose both same-named skills. Wrighty therefore reports
both copies as a duplicate instead of claiming that either one wins. `skill install` refuses to
create a second-scope copy with `SKILL_DUPLICATE`; `--force` is available for deliberate testing or
an exceptional host-specific setup. Remove the unintended recognized copy with an explicit scope:

```shell
wrighty skill uninstall --agent codex --scope project
wrighty skill uninstall --agent codex --scope user
```

Uninstall is idempotent for a missing target. It refuses an unrecognized installation, and requires
`--force` before removing a recognized copy whose Wrighty-owned files were modified. An explicit
scope is always required so a default cannot remove the wrong copy.

Validate or update installed mechanics with:

```shell
wrighty skill check --agent all                 # user scope (default)
wrighty skill check --agent all --scope project
wrighty skill check --agent all --check-tracker
wrighty skill update --agent all                # user scope (default)
```

`skill check` reports `missing`, `current`, `outdated`, `modified`, or `malformed`. A non-current
state is inspection output rather than a command failure, so automation should inspect
`result.installations[].state` from `--json` instead of relying on the exit code.

Update copies assets bundled with the running `wrighty`; it never downloads skill content. Skill
currency is determined by the bundled skill version and Wrighty-owned mechanics. The CLI version
stored in `.wrighty-skill.json` is installation provenance and does not by itself make a skill
outdated. Update preserves a customized `description`. Modified tool-owned mechanics produce
`SKILL_MODIFIED` unless `--force` is explicit. All skill operations support `--json`.

The agent-facing CLI entry points `init`, `pick`, `claim`, `resume`, `worker`, and `web` inspect
both project- and user-scoped installations before running. They write non-fatal warnings to
standard error for outdated, modified, or malformed copies, including the explicit `skill update`
or `skill check` command to run. They also warn when both scopes are installed and give explicit
uninstall commands. Standard output and the requested command's exit status are unchanged,
including for `--json`; missing optional skills and a single current copy stay silent. The notice
is best-effort, so a failed background inspection never blocks the requested operation.

`wrighty init --check` also includes the inspected skill health in its validation report. Human
output lists installed physical targets with scope, path, state, and installed/bundled versions;
an entirely missing target is summarized once because installation remains optional. With
`--json`, `result.skills[]` contains every physical target and scope, including `missing`, plus its
agent IDs, versions, and whether the recognized copy can be updated safely.

The web console's **Agents** menu combines enablement, capacity, and skill maintenance in one
compact table. A healthy installation says **Current** with the normal success treatment; missing,
outdated, modified, malformed, or duplicate targets contribute to its warning state and attention
count. Codex, Copilot, and OpenCode each point to their shared physical target, while Claude has its
own target.

**Manage skill** expands one agent row without stacking another overlay. Its location selector
switches one card between the User and Project path, state, and available **Install**, **Update**, or
**Uninstall** action. An outdated row also provides an **Update skill** shortcut that updates every
outdated location for that target. Per-agent skill controls remain disabled while that agent is
disabled. The expanded row names every agent affected when the physical target is shared.

The **Skills for all agents** footer can install missing skills for enabled, detected agents at the
selected User or Project location, update every recognized outdated copy in its existing location,
or uninstall safe copies from the selected location. Modified or malformed content remains CLI-only
because the web console never applies `--force`. Existing copies at both scopes remain a warning
until one is uninstalled. Every action refreshes the Agents menu without adding a success status
message; failures remain visible as errors.

## Supported skill surfaces

This table covers the surfaces on which the bundled Wrighty skill can be used. See
[Supported agents and surfaces](supported-agents.md) for Wrighty's broader support for headless
workers, session resume, Desktop opening, and agent handoff.

Install the skill for the coding agent first. The table lists how to invoke Wrighty on each
supported skill surface:

| Coding agent surface | Activation | Example |
|---|---|---|
| Codex Desktop | Explicit only | `/wrighty Pick the next available item, implement it, run its tests, and finish it.` or the equivalent `$wrighty ...` |
| Codex CLI or IDE extension | Explicit only | `$wrighty Pick the next available item, implement it, run its tests, and finish it.` |
| Claude Code CLI or Desktop | Explicit only | `/wrighty Pick the next available item, implement it, run its tests, and finish it.` |
| GitHub Copilot CLI or an IDE surface that exposes skill commands | Automatic or explicit | `/wrighty Work on tracker item #42 and finish it when complete.` |
| GitHub Copilot coding agent or another surface without a skill slash command | Automatic, or named in the prompt | `Use the wrighty skill to work on tracker item #42 and finish it when complete.` |
| OpenCode CLI | Automatic through its native skill tool, or named in the prompt | `Use the wrighty skill to work on tracker item #42 and finish it when complete.` |

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

# Claude Code CLI or Desktop
/wrighty Pick the next available item and implement it.
/wrighty Archive tracker item #42.

# GitHub Copilot
/wrighty Show the available tracker items.
Use the wrighty skill to claim tracker item #42 and update its priority to P0.

# OpenCode CLI
Use the wrighty skill to pick the next available tracker item and implement it.
```

Slash-command availability is a feature of the coding-agent surface, not of the Wrighty CLI. If a
Copilot surface does not expose `/wrighty`, name the skill in the prompt as in the
table. After installing or updating, use `wrighty skill check --agent AGENT --check-tracker` to
verify both the skill files and the `wrighty` executable.

The skill tells agents to mutate tracker state only through the CLI and branch on structured error
codes. A skill is guidance, not a sandbox; use host permissions or hooks when bypass prevention
must be mechanically enforced.

For substantial creation, material clarification, and implementation of a referenced item, the
skill also makes a semantic requirements-readiness judgement. It uses the work-item text together
with trustworthy repository evidence, permits ordinary low-risk implementation choices, and asks
only for unresolved decisions that materially affect the result. Explicit tracked drafts may stay
incomplete, but the skill does not present them as ready or enable automatic processing until the
same assessment passes. Fresh worker sessions independently assess the approved context they
receive; the skill does not stamp items with a reusable “verified” marker.

The skill's Local Markdown workflow actions share eligibility and locked revalidation with the
web Board. CLI and web batches share the Core execution loop; the CLI persists its own previews
and results for use across invocations. Worker discovery and cooperative control also use the
shared Core services. The skill describes these outcomes in conversation; it does not maintain a
separate board or worker scheduler.
