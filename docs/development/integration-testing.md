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
