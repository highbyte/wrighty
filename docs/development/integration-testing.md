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
