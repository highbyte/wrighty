# Agent skills

The package contains a narrow, explicit-opt-in `wrighty` Agent Skill shared by Codex,
Claude Code, and GitHub Copilot:

```shell
wrighty skill install --agent codex
wrighty skill install --agent claude
wrighty skill install --agent copilot
wrighty skill install --agent all
```

Omitting `--agent`, or passing `--agent auto`, targets every supported agent CLI installed on the
current machine. If both Codex and Copilot are installed, Wrighty writes their shared
`.agents/skills/wrighty` destination once. With no supported CLI installed, automatic selection
fails with `SKILL_AGENT_NOT_INSTALLED`. Use explicit `claude`, `codex`, or `copilot` when you want
one destination, or `all` when you deliberately want every supported destination regardless of
which CLIs are installed.

Project scope is the default. It resolves to the Git root when available and otherwise the current
directory. Use `--project-dir PATH` to choose another project or `--scope user` for a personal
installation. Codex and Copilot share `.agents/skills/wrighty`; Claude uses
`.claude/skills/wrighty`. An `all` installation creates those two physical copies.

Project-scoped skills intended for worktree workers must be committed. A Git worktree contains the
selected commit, not ignored or merely untracked files. Alternatively, install the Wrighty skill at
user scope so it is available to every repository and worktree:

```shell
wrighty skill install --agent all --scope user
wrighty skill check --agent all --scope user
```

Before a `worktree` worker claims an item, Wrighty verifies that the selected agent has either a
user-scoped skill or the required project skill in `HEAD`. An ignored project copy is deliberately
rejected with `WORKER_SKILL_UNAVAILABLE`; Wrighty does not silently copy or install executable
agent instructions into a new worktree.

Validate or update installed mechanics with:

```shell
wrighty skill check --agent all
wrighty skill check --agent all --check-tracker
wrighty skill update --agent all
```

Update copies assets bundled with the running `wrighty`; it never downloads skill content. It
preserves a customized `description`. Modified tool-owned mechanics produce `SKILL_MODIFIED`
unless `--force` is explicit. All skill operations support `--json`.

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
