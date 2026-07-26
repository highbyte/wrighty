# Integration testing

Wrighty includes a process-level Local Markdown smoke test and opt-in live GitHub integration
tests in addition to `dotnet test Wrighty.slnx`. The Local Markdown test is isolated and requires
no external service. The GitHub tests require an authenticated `gh` session, GitHub Project
scopes, and real GitHub resources.

Run the commands in this guide from the repository root.

## GitHub disposable integration fixture

The repository includes a setup script for the disposable personal GitHub Project and issue used
by live integration tests:

```shell
scripts/setup-github-integration-fixture.sh
```

The normal mode is idempotent: it reuses the exact configured Project and issue when present,
ensures the Status and Priority fields, runs `wrighty init` to link the repository and reconcile
managed fields, adds the issue to the Project, sets it to Todo/P1, clears current-agent
projections, and validates the resulting schema. It rewrites the tracked test-only
`.wrighty.integration-fixture.json` with the selected Project number. This leaves
`.wrighty.json` available for a real tracker configuration in this repository.

To discard all fixture history and recreate it from scratch:

```shell
scripts/setup-github-integration-fixture.sh --recreate
```

`--recreate` permanently deletes Projects with the exact fixture title and every real issue from
the configured repository contained in those Projects, including issues created by integration
tests. It also deletes issues with either the exact fixture title or the `wrighty-fixture` label.
Deleting an issue deletes its claim comments. The flag is intentionally destructive and
non-interactive so it can be used by automated integration setup. Run `--help` for repository,
owner, and title overrides.

## Premise checks

The three `scripts/prototype-*.sh` probes are not regression tests. They do not exercise Wrighty;
they verify **assumptions Wrighty's design rests on**, all of which live in systems outside this
repository — GitHub's API behaviour and the vendor CLIs' own context handling.

| Probe | Premise it guards | Cost |
| --- | --- | --- |
| `prototype-github-context-approval.sh` | Findings F1–F5: which GitHub timestamps advance, and which content transitions are observable at all | Free, but GraphQL-budgeted |
| `prototype-agent-prompt-transport.sh` | Finding F7: which vendors accept a prompt on standard input | Free; `--live` spends agent tokens |
| `prototype-session-context-retention.sh` | Finding F8: resumed sessions retain their launch context — the basis of plan 030's decision 20 | **Billed**, several minutes |

Two things follow from that framing.

**A failure is not a Wrighty regression.** It means an external premise changed and the plan
decision resting on it needs revisiting. That is a different response from "fix the code", and the
owning plan should be updated rather than the probe adjusted until it passes.

**None of them belong in CI.** The retention probe spends real agent turns on every run, and
repeated full runs of the GitHub probe exhaust the GraphQL point budget. Run them deliberately: on
a vendor CLI upgrade, before the phase that builds on their findings, or when a finding is
questioned. Each script's header states its own triggers.

The retention probe is the most valuable of the three, because its premise is the most fragile.
Vendor context management changes without announcement, and if resumed sessions stop retaining
their launch context, the delta-resume design degrades silently — nothing else here would notice.

## Static analysis and `scripts/`

`scripts/**` is excluded from SonarCloud analysis (`sonar.exclusions` in
`.github/workflows/sonarscan-dotnet.yml`). Everything there is developer tooling — walkthroughs,
fixtures, and prototype probes — rather than shipped product code.

The exclusion exists because the shell rules were reaching only *newly added* scripts: the dozen
already in the tree predate the analyzer and are not measured, so new scripts were being held to a
bar no existing one meets. One consistent rule for the directory beats an inconsistent one. Some of
the rules also asked for changes that are wrong here — an explicit `return 0` at the end of a
function overrides the last command's exit status, which is precisely what these scripts exist to
detect and report.

This removes lint coverage, not verification. The scripts run in CI and locally, several assert
their own behaviour, and `# shellcheck` directives remain in the sources for anyone running
shellcheck directly.

## Launch preflight smoke test

Every worker launch passes the [launch preflight](../reference/worker.md#launch-preflight). Because
a refused launch never reaches an agent, this test needs no live vendor and no second terminal — it
drives the locally built CLI against a temporary store with a fake `claude` on `PATH`:

```shell
scripts/test-launch-preflight.sh
```

It asserts that an admitted launch still reaches the vendor, and that a post-claim refusal releases
the claim, restores the source status, starts no vendor process, and creates no workspace even in
worktree mode. Nothing is billed.

Add `--narrate` to have each step explained and every worker event printed before it is checked —
useful for seeing the behaviour rather than just confirming it:

```shell
scripts/test-launch-preflight.sh --narrate
```

The refusal is triggered deterministically with `--filter status=Todo`: the pre-claim scan admits
the item, the worker claims it and moves it to the active status, and the post-claim stage then
finds the operator filter no longer matches. That reaches the same code path a real mid-flight
policy change takes. (It also means a status filter matching the source status will churn every
item this way.)

The **pre-spawn** stage is not covered here. It is wired and enforced, but no built-in check
registers there yet and the CLI passes no additional checks, so from the command line that stage
always admits. `LaunchPreflightWorkerTests` covers it by registering a check directly; it becomes
reachable live when plan 030 phase 4 adds the approved-context check.

## Claim-fencing smoke tests

### Local Markdown backend

Run the process-level claim-fencing workflow against an isolated temporary Local Markdown store:

```shell
scripts/test-local-markdown-claim-fencing.sh
```

The script builds and exercises the local Wrighty CLI, uses separate `WRIGHTY_CACHE_DIR` values to
simulate two installations, verifies frontmatter v2 and stale-token fencing, and removes its
temporary configuration and store on exit. Use `--keep-store` to retain the fixture for inspection,
`--skip-build` to use an existing local build, or `--help` for all options.

The deterministic store-lock ordering tests remain in the .NET test suite. This smoke test adds
real CLI process, configuration, environment, serialization, and filesystem coverage.

### Worker and human flows

Run the process-level worker/human scenarios against an isolated Git repository and Local Markdown
store:

```shell
scripts/test-worker-human-flows.sh
```

The script uses fake vendor processes only. It verifies needs-attention state, dashboard visibility,
atomic CLI `edit --takeover` and token-free headless handback, explicit clarification requeue and
continuous resumption of the same recorded session, default same-workspace rejection before claim or spawn,
configured concurrent `shared` mode with collision warnings, CLI/config precedence unit coverage,
concurrent worktree isolation, and exact-item recovery that deliberately expires a claim and
asserts that the same Claude session resumes under a new fencing token. Each fake Claude process
also requires the committed project
skill and runs `wrighty get <id> --json` from its assigned workspace. The worktree cases therefore
verify that the child receives `WRIGHTY_CONFIG_PATH` and reads the original Local Markdown store
even though its item and live claim are absent from the worktree checkout.
Every scenario prints the policy it exercises before running its assertions. The `probes` suite
records non-gating observations for unresolved behavior such as direct interactive resumes and
`--on-fenced detach`:

```shell
scripts/test-worker-human-flows.sh --suite rejection
scripts/test-worker-human-flows.sh --suite happy
scripts/test-worker-human-flows.sh --suite probes
```

Use `--keep-store` to retain the temporary repository, worktrees, fake-agent controls, dashboard
response, and command transcripts. Use `--skip-build` to reuse the existing local build.

### Worker usage recovery (live exhausted account)

Real provider compatibility for usage exhaustion is intentionally kept out of the normal test
suite: reproducing it requires a temporarily exhausted Claude, Codex, or Copilot account and may
span the provider's reset window. Run the dedicated Local Markdown walkthrough while the selected
account is currently limited:

```shell
scripts/walkthrough-worker-usage-recovery.sh \
  --agent claude \
  --retry-minutes 130
```

The walkthrough provisions a disposable repository and prints two commands to run in a second
terminal. The first live worker run must classify the provider stop, retain its session/worktree,
release the claim, schedule a bounded retry, and open the installation-local provider circuit. The
walkthrough creates a second fresh item and verifies that a normal worker leaves both it and the
future retry untouched while emitting `provider-unavailable`; this proves filtering occurs before
claim, workspace preparation, or spawn. It also verifies the portable frontmatter state,
machine-local timer, and `wrighty get`/`status` projections. After capacity returns, choose a manual
retry-now override or wait until the timer and exercise normal due-retry selection; the retained
vendor session must complete the fixture item and clear its dispatch state.

`wrighty provider probe AGENT --yes --json` and the Local Markdown dashboard's provider-capacity
actions can test capacity without claiming an item, regardless of whether a circuit is already
open. A still-limited live account must open or extend the circuit, while a successful probe must
leave or make fresh items eligible. The same command and web actions use the shared provider probe
lease, so concurrent attempts must not spawn more than one vendor check.

Use `--resume-mode manual|automatic` to preselect that final path. When the provider does not expose
an exact machine-readable reset, `--retry-minutes` sets the first fallback delay; choose a value
slightly beyond the expected reset. The fixture is kept automatically after a failed or interrupted
walkthrough and can also be retained after success with `--keep-fixture`. This focused walkthrough
does not create GitHub resources.

The GitHub-backend counterpart exercises the same live-provider behavior while also checking the
authoritative issue label, four display-only Project recovery fields, single handover comment, and
probe/retry commands:

```shell
WRIGHTY_RUN_GITHUB_WALKTHROUGH_LIVE=1 \
  scripts/walkthrough-worker-usage-recovery-github.sh \
  --agent claude
```

It uses the same private `<owner>/<repo>-test` guardrails as the GitHub completion walkthrough:
`gh` must be authenticated, the target must be private and end in `-test`, and the live
acknowledgement variable is mandatory. It creates uniquely named issues, verifies that an explicit
provider probe does not claim or mutate the scheduled item, and deletes only those issues after a
successful run. If provider capacity remains unavailable, it executes the final scheduled attempt
and verifies the bounded transition to `needs-attention`; that is also a successful walkthrough
outcome. The Project and test repository are reused. Failed or interrupted fixtures are kept for
inspection; use `--keep-fixture` to retain a successful one. A one-time `start` acknowledgement
synchronizes the controlling terminal before provisioning; later checkpoints use Enter only and
also wait for worker claim/run state to settle before verification.

### Worker completion lifecycle (live agent)

The flows above use fake vendor processes. The completion lifecycle — retained-versus-removed
worktrees under the commit policy, branch recording, `wrighty workspaces` and its cleanup guards,
and the guided-completion skill flow — depends on a real vendor session and cannot be driven by a
fake agent. `scripts/walkthrough-worker-completion.sh` is a guided, semi-automated walkthrough for
that path:

```shell
scripts/walkthrough-worker-completion.sh
```

It provisions a disposable Local Markdown repository (with the Wrighty skill installed and a few
seeded items), prints the exact commands to run in a **second terminal**, and pauses while you run
`wrighty worker` and drive the guided-completion session there. After each step it verifies the
observable result — recorded branch, retained or removed worktree, `wrighty workspaces` listing,
and the cleanup guard codes (`WORKSPACE_NOT_FOUND`, `CLAIM_HELD`, `WORKSPACE_NOT_CLEAN`,
`WORKSPACE_BRANCH_UNMERGED`). Config validation and the claim/no-workspace guards run fully
automatically; the agent-driven scenarios (commit policy, naming template, guided completion) are
opt-in prompts. Pick your vendor with `--agent claude|codex|copilot` (default `claude`); the
vendor CLI must be installed and authenticated. Use `--keep-fixture` to retain the temporary
repository and worktrees, and `--skip-build` to reuse the existing local build. Nothing outside the
temporary directory is touched. The scenario logic is shared with the GitHub-backend variant
(below) through `scripts/walkthrough-lib.sh`, so both walkthroughs exercise the identical worker,
`wrighty workspaces`, `wrighty resume-command`, and guided-completion steps.

### Worker completion lifecycle on the GitHub backend

The completion-lifecycle scenarios are backend-neutral — they drive the worker and the CLI, never
the (Local Markdown only) web dashboard — so the same walkthrough runs against the GitHub backend.
`scripts/walkthrough-worker-completion.sh` (above) is the Local Markdown driver;
`scripts/walkthrough-worker-completion-github.sh` is the GitHub driver over the same shared library:

```shell
WRIGHTY_RUN_GITHUB_WALKTHROUGH_LIVE=1 \
  scripts/walkthrough-worker-completion-github.sh
```

Unlike the local walkthrough, this one is **live**: it creates real issues, Project items, labels,
and branches, and you drive a real vendor agent in a second terminal. It never touches the product
repository. It resolves, creates (private, when missing), and validates a dedicated disposable
repository derived as `<owner>/<repo>-test` via `scripts/ensure-github-test-repo.sh` — the name must
end in `-test` and the repository must be private, or the run refuses. It clones that repository
into a temporary directory, runs `wrighty init --backend github --skip-issue-forms` to provision the
worker labels and a linked Project, installs and commits the skill, seeds the same work items as
GitHub issues, and then runs the identical scenarios.

`WRIGHTY_RUN_GITHUB_WALKTHROUGH_LIVE=1` is required to acknowledge the live mutations, and `gh` must
be authenticated. Derive the test repository from a specific source with `--source-repo OWNER/REPO`
(default: the current `gh` repository), name the Project with `--project-title`, and pick your
vendor with `--agent`. On exit the scoped teardown deletes only the issues that run created and
removes the temporary clone; the `-test` repository, its Project, and its labels are reused across
runs and are never deleted here. Use `--keep-fixture` to keep the clone and the created issues.

`scripts/ensure-github-test-repo.sh` is usable on its own too — `--name-only` prints the derived
`<owner>/<repo>-test` name with no network calls, and it is designed to be sourced by the other live
GitHub scripts as they move onto the shared test repository (plan 024).

### GitHub backend

The opt-in claim-fencing script builds and exercises the local Wrighty CLI against exactly the
`highbyte/wrighty` repository and the **Wrighty claim fencing** Project configured by
`.wrighty.integration-fixture.json`:

```shell
WRIGHTY_RUN_GITHUB_CLAIM_FENCING_LIVE=1 \
  scripts/test-github-claim-fencing.sh
```

It creates one uniquely titled disposable issue, simulates same- and different-installation
callers with isolated `WRIGHTY_CACHE_DIR` values, and validates exact reconnect, explicit takeover,
old-token fencing, current-token mutation, override release, cross-installation denial, concurrent
takeovers, Project attribution, and the server-backed v2 event chain. Tokens are retained only in
script variables or its temporary directory and are not printed. As required by the protocol, they
are visible in the disposable issue comments until cleanup deletes the issue and its comments.

The script refuses any other repository, owner, or Project title. On exit it verifies the issue
title and permanently deletes only the issue created by that run. Use `--keep-issue` to preserve it
for inspection, `--skip-build` to use an existing local build, or `--help` for all options.

### GitHub Project view capability

The canonical-board capability has a focused live result. On 2026-07-20, Wrighty's disposable
user-owned Project was exercised with GitHub REST API version `2026-03-10`:

- `POST /users/{user_id}/projectsV2/{project_number}/views` with
  `{"name":"Wrighty Board","layout":"board"}` created a board view successfully;
- the REST response returned an empty `group_by` array;
- a GraphQL read returned `BOARD_LAYOUT` and an empty `groupByFields` connection;
- the resulting GitHub UI displayed the Status options as the board columns (`Todo`,
  `In Progress`, and `Done`).
- GraphQL view enumeration returned GitHub's initial `View 1` table and the created
  `Wrighty Board`, including their view numbers and layouts.

The UI result confirms GitHub's documented behavior that a board uses Status for its columns by
default. REST `group_by` and GraphQL `groupByFields` describe optional additional grouping, so their
empty values do not indicate an ungrouped board. Wrighty can therefore create the canonical board
and verify its exact name and `BOARD_LAYOUT`; `wrighty init --create-view` enables that operation
for an existing Project. Re-run this focused prototype against a disposable Project if GitHub
changes the endpoint or default board behavior.

GitHub exposes no supported view-delete or view-reorder API. A newly created Project therefore
retains its initial `View 1` table until an operator deletes it through the UI. Wrighty uses the
GraphQL enumeration above to print this manual cleanup guidance only when it created the Project
and confirmed the exact initial view.

### GitHub Project default repository capability

A focused GitHub.com prototype on 2026-07-20 created disposable user-owned Project 18 with
`createProjectV2(repositoryId: ...)`. GraphQL confirmed that `highbyte/wrighty` was linked, but the
Project board's new-issue dialog still preselected `highbyte/dotnet-6502`. The disposable Project
was deleted after the check.

This confirms that `CreateProjectV2Input.repositoryId` establishes a repository link, not the
Project's Default repository. The public Project GraphQL and REST surfaces expose no supported
setter or readable field for that setting, and GitHub Projects are intentionally multi-repository.
Wrighty therefore reports the exact one-time manual **Project menu → Settings → Default
repository** step after creating a Project instead of claiming that initialization configured or
verified it.

`scripts/setup-github-integration-fixture.sh` now runs `wrighty init --create-view` for the
disposable fixture. A normal setup therefore creates the canonical board when it is missing and
exercises the idempotent existing-view path on later runs. The script's final `init --check`
validates the resulting Project schema and compatible view without writing.

GitHub initialization also verifies all managed worker labels. Every non-interactive mutating init
in an integration script passes `--yes` to approve its fully resolved plan before writes. Scripts
that test only remote Project and label initialization also pass `--skip-issue-forms`. Generated
forms are not committed or pushed by `--yes` alone; an automation that deliberately tests form
publication must additionally pass `--publish-issue-forms` and use a disposable branch.

Concurrent commands may overlap and produce one winning takeover plus one `CLAIM_STALE`, or GitHub
may serialize them so both transitions succeed in sequence. The script verifies the final resolved
handle in either valid case. Deterministic `CLAIM_LOST_DURING_UPDATE` placement remains a controlled
fake/test-hook scenario rather than a live timing assertion.

## GitHub persistent pagination fixture

Live validation across GitHub's real 100-item Project page boundary uses a separate persistent
fixture. Its seed workflow and read-only test are deliberately independent.

The seed script defaults to a dedicated private repository named
`OWNER/wrighty-scale-fixture`, a private Project named `Wrighty Pagination Fixture`, and 101
deterministically titled issues:

```shell
scripts/seed-github-pagination-fixture.sh
```

Initial seeding is mutating and may take several minutes because requests are serialized with a
delay. It creates the private repository only when absent, reuses or creates the exact Project,
creates only missing labelled issues, repairs missing Project membership, and configures one
final-page sentinel as `In Progress`/`P1`. It generates the ignored local configuration file
`.github-pagination-fixture.json`.

When the repository, Project, 101 issues, membership, fields, and sentinel are already valid, a
normal rerun performs validation reads without recreating them. Validate without permitting any
repair using:

```shell
scripts/seed-github-pagination-fixture.sh --check
```

Unexpected extra or duplicate fixtures stop with an error and are never deleted automatically.
`--recreate` explicitly deletes only the exact fixture Project and issues carrying the fixture
label, then rebuilds them; it never deletes the private repository. Run `--help` for owner,
repository, item-count, pacing, and configuration overrides.

The live xUnit project is excluded from the solution and skips unless explicitly enabled. After
the fixture has been seeded and validated, run only the read-only pagination test with:

```shell
WRIGHTY_RUN_GITHUB_LIVE=1 \
WRIGHTY_GITHUB_LIVE_CONFIG="$PWD/.github-pagination-fixture.json" \
dotnet test tests/Highbyte.Wrighty.GitHubLiveTests \
  --filter Category=GitHubLivePagination
```

Set `WRIGHTY_GITHUB_LIVE_ITEM_COUNT` when the seed used a count other than 101. The test does not
seed, repair, edit, or delete GitHub resources. It verifies the expected item count, real
page-request count, direct field lookup, and discovery of the final-page sentinel.

## Behaviour prototypes

Some designs depend on how GitHub or a vendor CLI actually behaves, not on how Wrighty behaves.
The `scripts/prototype-*.sh` harnesses measure that behaviour and write a machine-readable
observation record. They implement nothing, and a probe they cannot settle is recorded as `manual`
or `open` rather than assumed to pass.

Records are written under the ignored `.wrighty-prototype/` directory. Curated findings belong in
the design log, not in this repository.

### GitHub approval-revision behaviour

```shell
scripts/prototype-github-context-approval.sh --check
WRIGHTY_RUN_GITHUB_PROTOTYPE_LIVE=1 scripts/prototype-github-context-approval.sh
```

`--check` validates prerequisites and provisions nothing new beyond the Project and fields; it
makes no probe mutations. A live run measures Project single-select value timestamps (including
whether an unrelated field write disturbs them), reaction identity and strict ordering against the
current comment revision, repository permission and exact role lookup, issue title/body edit
visibility, comment pagination, and minimize/delete observability.

It never touches the product repository: like the worker-completion walkthrough, it provisions its
own current-schema Project on the private `<owner>/<repo>-test` repository resolved by
`scripts/ensure-github-test-repo.sh`, and deletes the issues it created on exit unless
`--keep-fixture`. The test repository and its Project are reused across runs.

Probes needing a second GitHub identity — deleting another user's reaction, confirming an
unauthorized reactor has no effect, custom repository roles, team membership — are reported as
`manual` with the exact command to run. The record's verdict stays `incomplete` until those are
filled in. A measured negative that selects a documented fallback rather than invalidating the
design is reported as `constrained`, so a settled design choice is never mistaken for a blocked
gate.

**GraphQL budget.** The GraphQL API is point-budgeted, and an exhausted budget makes reads return
*empty rather than erroring* — which would otherwise be recorded as confident probe failures that
never happened. The script checks the remaining budget before starting, reads the Project field
schema once instead of per write, and aborts rather than recording results from an incomplete read.
Back-to-back full runs can still exhaust the budget; allow for the reset between them
(`gh api rate_limit --jq .resources.graphql`).

### Vendor prompt transport

```shell
scripts/prototype-agent-prompt-transport.sh
WRIGHTY_RUN_AGENT_TRANSPORT_LIVE=1 scripts/prototype-agent-prompt-transport.sh --live
```

The default tier is free: it measures the platform's real argv ceiling against an `exec`, reports
each installed vendor's version, and scans each CLI's own help for a standard-input or prompt-file
path. No agent session is started, so nothing is billed.

`--live` additionally starts one short session per vendor per transport and checks that the whole
prompt arrived — the marker sits at the *end* of the prompt, after filler, so a size limit on the
way in is detectable — and that structured output still parses. This spends real agent tokens on
every installed vendor and requires the explicit acknowledgement above.
