# Agent skills

The package contains a narrow `wrighty` Agent Skill shared by Codex, Claude Code, GitHub Copilot,
and OpenCode:

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

The web console always shows **Agent skills** in the header. A healthy installation says
**Current** with the normal success treatment; missing, outdated, modified, malformed, or duplicate
targets use the warning border and attention count. The overlay groups agents by physical target,
so Codex, Copilot, and OpenCode share one management row while Claude has its own. Each row shows
user and project scope, path, version, state, and the safe actions available for that copy.

The overlay can install an entirely missing target at either scope, update a recognized outdated
copy, or uninstall a recognized unmodified copy. It also offers **Install all missing** with user
scope selected by default, **Update all outdated**, and separately confirmed user/project bulk
uninstalls. Modified or malformed content remains CLI-only because the web console never applies
`--force`. Existing copies at both scopes remain a warning until one is uninstalled. Every action
refreshes the header immediately without adding a success status message; failures remain visible
as errors.

During the consolidated agent-management transition, the neighboring **Agents** menu exposes the
same safe maintenance operations in a compact per-agent table. **Manage skill** expands one row;
it does not stack another overlay. Its location selector switches one card between the User and
Project path, state, and available action. An outdated row also provides an **Update skill**
shortcut that updates every outdated location for that target. Per-agent skill controls remain
disabled while that agent is disabled. Codex, Copilot, and OpenCode each point to their shared
physical target, and the expanded row says that an action affects all three. The original **Agent
skills** control remains available for comparison until the consolidated surface has been accepted.

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
