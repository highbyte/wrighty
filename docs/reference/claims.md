# Claims and ownership

## Claims

GitHub stores authoritative claims as append-only issue-comment events; its claimant Project fields
are display-only. Local Markdown stores the current authoritative claim in the machine-local
`.runtime-state.json` sidecar under the store lock; item documents never contain claim state.
Recorded agent session addresses are kept as durable machine-local records on both backends and
survive claim release and expiry. See the
[item metadata reference](../item-metadata/README.md) for the storage and
authority boundary, and [claim protocol v2](../design/claim-protocol-v2.md) for transition
resolution.

Claims contain an authoritative `claimantId` and `claimToken`, plus `claimantKind` attribution and
optional `agentType` and `sessionId` correlation metadata. Kind and agent/session metadata are
informational; the exact installation, claimant ID, and token authorize mutations.
Session IDs are published into comments with the same visibility as their issue. Use
`--no-claimant-context` if no attribution or correlation metadata should be published.

Active pre-v2 claims fail closed and require the documented alpha upgrade procedure below.

### Claimant attribution

`claim` and `pick` record who initiated the claim as `agent`, `human`, `automation`, or `unknown`.
Wrighty automatically detects current Codex, Claude Code, and GitHub Copilot CLI sessions. A direct
CLI invocation with no agent signal is recorded as `human`. Use explicit values for agents that do
not expose a supported signal and for scripts or other automation:

```shell
wrighty claim 42 --claimant-kind agent --agent-type other
wrighty claim 42 --claimant-kind automation --claimant-id automation:run-42
wrighty pick --no-claimant-context
```

For unattended automation, set the option on every acquisition command or export it once:

```shell
export WRIGHTY_CLAIMANT_KIND=automation
export WRIGHTY_CLAIMANT_ID=nightly-import-2026-07-16
wrighty pick --json
```

The equivalent environment variables are `WRIGHTY_CLAIMANT_KIND`, `WRIGHTY_CLAIMANT_ID`,
`WRIGHTY_CLAIM_TOKEN`, `WRIGHTY_AGENT_TYPE`, `WRIGHTY_SESSION_ID`, and
`WRIGHTY_NO_CLAIMANT_CONTEXT`. Resolution order is an explicit
`--claimant-kind`, `WRIGHTY_CLAIMANT_KIND`, automatic agent detection, then `human`. The bundled
agent skill explicitly supplies `--claimant-kind agent`, which acts as a fallback when the agent
program cannot be detected; its `agentType` is then `other`. Contradictory agent metadata with a
non-agent claimant kind is rejected. `--no-claimant-context` deliberately records `unknown` and
suppresses all attribution metadata. Conflicting vendor signals are never guessed; the command
continues with a warning and records `unknown` unless the caller explicitly identifies an agent.

GitHub projects the winning attribution for display but never projects the claim token or reads
projection fields for authorization. Projection failure cannot transfer or roll back a claim.
See [GitHub Project item metadata](../item-metadata/github-backend.md#project-item-metadata) for
the field-level contract. The [v1 protocol](../design/claim-protocol-v1.md) is historical only.

## Claim ownership, fencing, and takeover

A claim owner is the tuple `(workerIdentity, claimantId, claimToken)`. `workerIdentity` identifies
one Wrighty installation; `claimantId` identifies a human surface, agent session, or automation
run; `claimToken` is the current fencing generation. Every acquisition and takeover rotates the
opaque token. Tokens are visible workflow data, not passwords, but a mutating caller must present
the token it received—Wrighty never reads the authoritative claim and adopts its token.

Direct human CLI commands default to the installation-local claimant ID `human-cli`; set
`WRIGHTY_CLAIMANT_ID` for terminal-level separation. The web server creates a new random claimant
ID for each launch and retains acquired tokens in server memory. Detected agents derive their ID
from the vendor session; an undetected agent receives a generated ID in the acquisition result.
Automation must set a unique claimant ID and never uses a shared default. Claim, pick, and takeover
JSON results include the complete handle. Pass it with `--claimant-id`/`--claim-token` or
`WRIGHTY_CLAIMANT_ID`/`WRIGHTY_CLAIM_TOKEN` on every later mutation.

Four rules cover every operation:

1. **Reading is always allowed.** `list`, `get`, and the dashboard never require a claim.
2. **Mutating requires the exact handle.** `edit`, `move`, `finish`, `archive`, `release`, and
   renewal succeed only with the current claimant ID and token; a superseded handle receives
   `CLAIM_STALE`, and an unclaimed or expired item requires acquisition first (`CLAIM_REQUIRED`).
3. **On this installation you can always recover.** `wrighty edit <id> --takeover` acquires an
   unclaimed or expired item, and — after confirmation — displaces another active claimant here.
   `wrighty worker --item <id>` does the same to continue a recorded agent session.
4. **Another installation's active claim always wins until it expires.** Takeover and override
   release are denied with `CLAIM_NOT_OWNER`; claiming reports `CLAIM_HELD`. Wait out the finite
   lease or coordinate with that installation.

Reconnecting with the exact claimant ID and token is idempotent (`AlreadyOwned`, no rotation);
another claimant on the same installation is told `CLAIM_HELD_BY_LOCAL_CLAIMANT` and offered
takeover. `pick` simply skips anything it cannot claim.

### Recovery paths

Prefer the two combined operations; they handle claim handles, fencing, and session preservation
in one process:

- **`wrighty edit <id> --takeover`** — “ensure a human editing claim”: acquire, recover after
  expiry, or (with confirmation, or `--yes`) displace an active same-installation claimant, then
  apply the edit and print the worker continuation. Direct patch options support JSON;
  interactive editor mode does not.
- **`wrighty worker --item <id>`** — continue the recorded agent session: take over an active
  same-installation session, reacquire an expired one, or start fresh when nothing is recorded.

### Escape hatches

Two lower-level commands exist for cases the combined operations do not cover. Both name the
previous claimant, require confirmation (`--yes` for non-interactive or JSON use), work only on an
active claim owned by this installation, and never stop or signal an OS process:

- **`wrighty takeover <id>`** transfers the claim to the caller without editing anything —
  useful when a script needs the raw handle (`--print-resume-command` adds the vendor resume
  command).
- **`wrighty release <id> --override`** clears an abandoned claim and returns the item to the
  pool without taking it over. The recorded agent session remains available.

Common scenarios:

- **Agent to human web:** the viewer shows agent attribution and no ordinary Edit action. After
  **Take over for editing…** succeeds, the human web session owns a fresh token and the old agent's
  next edit, finish, archive, release, or renewal receives `CLAIM_STALE`. Plain Save remains human;
  **Save and hand back to _Agent_** rotates again to a new agent claimant before exposing its resume
  command.
- **Agent to another agent / human to agent:** matching agent program names do not imply ownership.
  The second claimant needs an explicit user-authorized takeover; normal claim and mutation never
  seize the item.
- **Exact reconnect:** claimant ID plus token returns `AlreadyOwned` without rotation. Claimant ID
  alone cannot reveal or recover a token. A restarted web server has a new ID and uses takeover.
- **Abandoned claimant:** clarify and continue with `edit --takeover` or `worker --item`, or
  override-release to return the item to the pool. Every displacement identifies the previous
  claimant and requires confirmation.
- **Other installation:** takeover and override release are denied until lease expiry.

Local Markdown provides strong cooperative fencing because validation, document mutation, and
takeover share the store lock. An old mutation that holds the lock first may complete before the
takeover; once takeover reports success, no later Local Markdown mutation with the old generation
can land. GitHub cannot condition issue and Project writes on the token. Wrighty checks immediately
before and after each write and reports `CLAIM_LOST_DURING_UPDATE` with applied/pending stages, but a
write already in flight may land after takeover and is never rolled back automatically.

Claim protocol v2 is an alpha breaking change. Before upgrading, finish or release every active v1
claim with the old binary. Do not run old and new Wrighty binaries concurrently. Active v1 GitHub
comments or Local Markdown claims fail with `CLAIM_FORMAT_UNSUPPORTED`; inactive v1 history is safe.

Local Markdown stores created before the runtime-state sidecar are migrated by running
`wrighty init` once per store: it lifts legacy `claim:`/`claimEpoch:` frontmatter into
`.runtime-state.json` and preserves recorded sessions. Until then, ordinary commands fail with
`STORE_MIGRATION_REQUIRED`. Before upgrading, finish or release active claims or let their finite
leases expire, and do not run pre-sidecar and current binaries concurrently against one store. See
[migration details](../item-metadata/local-markdown-backend.md#migration-from-pre-sidecar-stores).
