# Execution profiles

A worker can run an agent harder or lighter without any work item naming a vendor model. Profiles
are stable labels — `economy`, `balanced`, `deep` — that each user resolves to concrete settings
locally.

The split is the point. **What a repository agrees on** is the vocabulary: which profile names exist
and which applies by default. **What you decide** is what those names mean in vendor terms.
Two developers on the same project can resolve `deep` differently, because a model name is a
property of what you have installed and are entitled to, not of the code.

- [What you get with no setup](#what-you-get-with-no-setup)
- [Choosing a profile](#choosing-a-profile)
- [Seeing what you can run](#seeing-what-you-can-run)
- [Overriding what a profile means](#overriding-what-a-profile-means)
- [From the web console](#from-the-web-console)
- [The repository vocabulary](#the-repository-vocabulary)
- [On GitHub](#on-github)
- [What a run records](#what-a-run-records)
- [Migrating when a model retires](#migrating-when-a-model-retires)
- [Limits](#limits)

## What you get with no setup

Wrighty ships three profiles, mapped to reasoning effort and **no model**:

| Profile | Effort | Model |
| --- | --- | --- |
| `economy` | `low` | whatever the vendor CLI is configured to use |
| `balanced` | `medium` | as above |
| `deep` | `high` | as above |

They work with nothing configured:

```shell
wrighty config profile list
```

**These tiers change how hard the agent thinks, not which model runs.** If your vendor default is a
flagship, `economy` still runs the flagship — just with less reasoning. That is a real reduction in
spend, because reasoning tokens dominate on these models, but it is not the same as running a
smaller one. Naming a cheaper model is a deliberate local override, described below.

**One caveat, measured rather than predicted.** Some models accept no reasoning-effort setting at
all. A Copilot account resolving to `claude-haiku-4.5` rejects it outright:

```
Error: Model "claude-haiku-4.5" does not support reasoning effort configuration (requested: "high").
```

Wrighty cannot see that in advance — it knows which levels a vendor's *flag* accepts, not what the
model behind the account will do with them. It is the model, not the vendor: the same Copilot CLI
runs `gpt-5.4` with effort perfectly well.

The run is not lost. The launch fails immediately, before any work happens, so Wrighty relaunches
once without the effort argument and says what it did:

```
effort-unsupported: copilot's model does not accept a reasoning-effort setting; relaunching
without it. Every execution profile will behave identically on this model until one that
supports effort is configured.
```

Only the effort is dropped; a pinned model is a separate choice and survives. The run records the
effort as unset, because that is what actually ran — recording `high` would attest to a request the
vendor refused.

That makes the default configuration work on such an account, but it makes every profile equivalent
there: `economy` and `deep` produce the same run. To get real tiers, pin a model that supports
effort:

```shell
wrighty config profile set deep --agent copilot --model gpt-5.4 --effort high
```

Copilot's `auto` is not a way around this — it also resolves to a model that declines effort.

The fallback is deliberately narrow. It fires only on the vendor saying the *model* cannot use
effort, not on an unrecognized level, an entitlement failure, or an exhausted quota — those still
fail the item, because retrying them differently would be guessing.

Wrighty ships effort levels rather than models on purpose. Model identifiers are vendor product
names that retire on the vendor's schedule: `gpt-5.6-luna` names one family generation, and `haiku`
is not among the aliases Claude Code's own `--help` documents. A shipped model catalogue would break
without Wrighty changing, and for codex a stale model is not even caught locally — the session
starts and fails at the API, having already spent a request. Effort levels are a far smaller
surface: `low`, `medium` and `high` are the three every vendor documents, and they do not retire
with a model family — though as above, an individual model may still decline effort entirely.

## Choosing a profile

Precedence, highest first:

1. `wrighty worker --profile <name>` — applies to that run
2. the work item's own profile
3. `worker.defaultExecutionProfile` — the repository default

```shell
wrighty worker --profile deep --once --yes
wrighty create --title "Tricky migration" --body-file spec.md --profile deep
wrighty edit <id> --profile economy --claimant-id <id> --claim-token <token>
wrighty edit <id> --clear-profile --claimant-id <id> --claim-token <token>
```

On the Local Markdown dashboard the item editor offers **Execution profile** beside **Agent policy**,
whenever the repository configures a vocabulary.

**Resolution fails closed.** A profile that resolves to nothing usable is an error
(`AGENT_PROFILE_UNAVAILABLE`), never a quiet fallback. Wrighty does not drop to a cheaper profile to
conserve credits, and does not escalate to a more capable one to rescue a failing run — both would
spend your money or change your results on a decision you never made.

A resumed session keeps the model and effort it started with, so `--profile` combined with
`--resume` is refused rather than ignored. Use `--fresh` to start a new session under a profile, or
`--handoff` to continue the work under a different agent.

## Seeing what you can run

Before pinning a model, ask the agents themselves:

```shell
wrighty config profile models
wrighty config profile models --agent codex --json
```

```
codex
  gpt-5.6-sol (used when no model is pinned): effort low/medium/high/xhigh/max/ultra, vendor default effort low
  gpt-5.4: effort low/medium/high/xhigh, vendor default effort medium
copilot
  gpt-5.4 (used when no model is pinned): cost 6x, effort none/low/medium/high/xhigh
  claude-haiku-4.5: cost 0.33x, effort unknown here
```

Each agent is asked over the interface Wrighty already launches it on, without starting a turn. No
account details are stored: the reply is read for its model list and nothing else.

**"effort unknown here" means unknown, not none.** The vendors differ in what they will disclose —
codex reports supported efforts for every model, claude reports them only for models that have them,
and copilot reports them for the model its session happens to be on. You can still configure an
effort a vendor will not vouch for; Wrighty saves it and says it went unchecked.

If an agent cannot be reached — not installed, signed out, offline, or answering in a shape this
version does not recognize — it says so and everything else keeps working. Discovery adds a check,
never a requirement.

## Overriding what a profile means

Overrides are **user-scoped** — they live in your user settings, not in the repository:

```shell
# Pin a model for one profile and agent.
wrighty config profile set deep --agent claude --model opus --effort xhigh

# A genuine low-cost tier: name a smaller model.
wrighty config profile set economy --agent codex --model gpt-5.6-luna --effort low

# Keep the effort, drop the model, so the vendor's own default applies again.
wrighty config profile set economy --agent codex --unset-model

# Remove the override entirely; the built-in tier applies again.
wrighty config profile unset economy --agent codex

wrighty config profile list
wrighty config profile show deep
```

`--model` takes whatever the vendor accepts: a rolling alias such as `sonnet` or `auto`, or an exact
model name. Wrighty checks the string is safe to pass and lets the vendor decide whether it exists —
it cannot know what your account is entitled to.

When you name both a model and an effort, Wrighty asks the vendor whether that model accepts that
level, and refuses a pair the vendor says is impossible:

```
$ wrighty config profile set deep --agent codex --model gpt-5.4 --effort ultra
ARGUMENT_INVALID: Model 'gpt-5.4' does not accept effort 'ultra'. It accepts: low, medium, high, xhigh.
```

That matters most for codex, which validates nothing locally: without the check the same mapping
starts a session and fails at the API, having already spent a request. A pair the vendor *cannot*
speak to is saved with a note rather than refused — see the unknown case above.

### Effort levels differ by vendor

All three vendors accept `low`, `medium`, `high`, `xhigh`, and `max`; support for the remaining
levels varies:

| Level | claude | codex | copilot |
| --- | --- | --- | --- |
| `none`, `minimal` | ✗ | ✗ | ✓ |
| `low`, `medium`, `high`, `xhigh` | ✓ | ✓ | ✓ |
| `max` | ✓ | ✓ | ✓ |
| `ultra` | ✗ | ✓ | ✗ |

A level the agent does not accept is refused when you set the mapping, not at launch. That matters
most for codex, which performs no local validation of its own: a bad effort there starts a session
and fails at the API, having already spent a request.

This check is a gate rather than a guarantee. Effort support is really a property of the *model*,
not the vendor — `ultra` works on the GPT-5.6 family and nowhere else, and `max` is absent from
`gpt-5.4`. Wrighty cannot enumerate models yet, so it refuses what could never work and lets the
vendor reject the rest.

## From the web console

`wrighty web` edits both halves on one page, the **Settings** tab. Under **Repository settings**,
*Execution profiles* sets the shared names and the default. Under **User settings**, your mappings
appear as one list —
one row per (profile, agent), edited in place with its stored values shown, removed with its Remove
button, and added to from the row beneath. The model choices come from what each agent reports,
with its relative cost and effort levels shown beside each.

The same rules apply there as on the command line: an impossible pair is refused, an unverifiable one
is saved with a note, and an agent that cannot be asked falls back to a free-text model field.

User settings apply to the next command; nothing needs restarting. Repository settings need
the worker and the web process restarted, and the page says so when you save one.

## The repository vocabulary

Shared policy, committed in `.wrighty.json`:

```shell
wrighty config repository profiles add docs-only
wrighty config repository profiles remove docs-only
wrighty config repository profiles set economy balanced deep   # replaces the whole list
wrighty config repository profiles default balanced
wrighty config repository profiles default --clear
```

Prefer `add` and `remove`. `set` replaces the entire list, so any name you leave out is removed —
and on GitHub that has consequences beyond the file (see below).

A repository that configures no vocabulary gets the three built-in names, so a project can set a
default profile without every user having to declare anything first.

A name the repository configures but Wrighty does not ship — `docs-only`, say — needs a mapping in
the user settings of everyone who might run it. Until then, resolving it fails closed.

## On GitHub

Items carry their profile in a Project single-select, `Wrighty policy - profile`, alongside the
execution and agent policy fields. Options are title-cased on the board (`Deep`) while the stored
vocabulary stays lowercase (`deep`); the CLI, the config file and item front matter all use the
lowercase form.

The field is provisioned only when the repository configures a vocabulary — a project that does not
use profiles never gains a field whose only option would be *Repository default*.

```shell
wrighty init --check     # reports the field and options that are missing or stale
wrighty init             # applies them
```

**Removing a profile removes the board option, which clears the value from every item holding it.**
`init --check` names that before `init` would do it:

```
remove option Docs-only from 'Wrighty policy - profile'
(this clears the value from any item still holding it)
```

Items whose profile is cleared fall back to the repository default on their next run. That is the
one place a profile changes without anyone editing the item, which is why the warning exists.

## What a run records

A fresh launch records what it asked the vendor for, beside the session address: the profile, the
resolved model and effort, whether the mapping came from the built-ins or your settings, and the
vendor CLI version at the time.

This is **machine-local only**. It is never a GitHub label, comment, Project field, work-item front
matter, URL, or transcript — it describes your installation's mapping, which the repository never
agreed to.

The CLI version is why the record is worth keeping. A mapping that worked under one vendor release
can stop working under the next, and without a version stamp that failure is indistinguishable from
a mapping that was always wrong.

## Migrating when a model retires

Only relevant if you have pinned models. The built-in tiers name none, so they do not retire.

1. Update the mapping: `wrighty config profile set economy --agent codex --model <next-tier> --effort low`
2. Check it: `wrighty config profile show economy`
3. Drain retained sessions, or hand them off. A recorded session keeps its original selection until
   it completes, so an in-flight run is unaffected by the change either way.
4. Observe the next fresh run. `wrighty get <id> --json` shows the profile the item carries.

The codex family slugs are the ones that retire first, since they name a single family generation.

## Limits

- Profiles set model and effort. They do not grant execution, widen permissions, or bypass
  approval, capacity, or workspace checks.
- `economy` names your designated low-cost tier. Wrighty holds no pricing data of its own and cannot
  compare cost across vendors. Where a vendor publishes a relative multiplier — copilot does, as
  `0.33x`, `6x`, `9x` — Wrighty shows it beside the model so you can weigh the choice, but never
  ranks, sorts or selects on it.
- Nothing selects `economy` automatically. Usage limits, capacity backoff, circuit recovery, and
  same-agent retry never rewrite a recorded selection to a cheaper or more capable tier.
- A retired or unavailable model is not substituted. Resolution fails and says so.

## Related

- [Configuration](configuration.md) — `worker.executionProfiles`, `worker.defaultExecutionProfile`,
  `github.workerProfileField`.
- [User settings](user-settings.md) — where your profile mappings are stored.
- [Autonomous worker mode](worker.md) — how a launch reaches an agent.
- [Usage recovery and agent handoff](usage-recovery-and-agent-handoff.md) — why a retry keeps its
  original selection.
