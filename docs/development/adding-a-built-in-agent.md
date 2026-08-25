# Adding a built-in agent

Wrighty supports agents through an immutable, compile-time registry. Adding an agent is a code
change: repository and user configuration may select a registered integration, but cannot define
executables, URI schemes, parsers, transcript locations, or other executable behavior.

Start in
[`AgentRegistry.cs`](../../src/Highbyte.Wrighty.Core/Workers/AgentRegistry.cs). An
`AgentDescriptor` owns the stable ID, display and vendor names, executable name, capability flags,
skill target, GitHub projection, and local Desktop metadata. Its `AgentIntegration` binds those
facts to process-scoped services. Registry construction rejects invalid or duplicate IDs,
capabilities without implementations, service IDs that disagree with the descriptor, conflicting
skill targets, unsafe paths, and unsafe Desktop schemes or platform declarations.

## Support, installation, and readiness

Keep these three states separate:

1. The registry says which integrations and capabilities this Wrighty binary supports.
2. `AgentRuntimeCatalog` says which registered executables are physically installed on the host.
3. `TestingAgentRuntimeCatalog` overlays repository-scoped test simulations on that physical
   snapshot. It may make an installed agent effectively unavailable, but never unregisters it or
   removes its settings and schema.

Worker selection and model discovery use the effective runtime view when installation matters.
Configuration vocabulary, web settings, GitHub schema, and documentation use registered support.
`wrighty worker --check` is the live readiness check; finding an executable is not proof that its
session protocol works.

## Choose a support level

Do not treat registration as an all-or-nothing promise. Choose the smallest level that has been
qualified and let every absent capability degrade explicitly:

1. **Registered identity only.** A descriptor with `AgentCapabilities.None` can preserve bounded
   attribution for historical or external sessions. It is not operational agent support.
2. **Manual Wrighty integration.** `SkillInstallation` alone gives a human-operated agent useful
   Wrighty instructions. `ContextDetection` can make that integration more convenient, but is not
   required.
3. **Fresh worker execution.** `WorkerExecution` is the minimum for unattended work. A fresh-only
   worker is useful when requirements assessment runs inline or is disabled, but cannot continue,
   recover, or perform the enforced two-turn readiness flow in the same vendor session.
4. **Continuable worker execution.** `WorkerExecution | Resume | SkillInstallation` is the
   recommended operational baseline. Add `InteractiveCli` when an operator can safely take over
   the recorded session.
5. **Full product integration.** Add discovery, export, context, Desktop, and GitHub projection
   only where the vendor actually supports them.

`WorkerExecution` by itself is therefore the minimum that gives Wrighty's automated worker value.
It is deliberately not shorthand for full parity with the built-in agents.

## Implementation checklist

### Identity and execution

- Add one descriptor and integration in `BuiltInAgentRegistry`. Use a lowercase, bounded ID that
  will remain stable in configuration and stored work-item/session data.
- Declare capabilities per descriptor. Do not put a new agent behind a shared all-capabilities
  constant merely because existing agents currently have the same qualified surface.
- Add the vendor's `IAgentAdapter` in its own file under
  `src/Highbyte.Wrighty.Core/Workers/`. This interface is the mandatory fresh-worker contract: it
  owns fresh and approved-context construction, readiness checks, permission and model/effort
  capability, session-handle creation and emitted-ID matching, and result parsing.
- Implement `IAgentResumeAdapter`, `IAgentInteractiveAdapter`, and `IAgentDesktopAdapter` only for
  the corresponding declared optional capability. `AgentIntegration` exposes those narrow
  services from the execution adapter, and registry construction rejects flags that disagree with
  the implemented interfaces.
- Keep invocations structured. Resolve only the descriptor's compiled executable name, pass
  approved work-item context over standard input, and never interpolate untrusted text into a
  shell command.
- Preserve the generic `WorkerRunHost` lifecycle. The integration supplies vendor protocol; it
  does not take ownership of drain, immediate interruption, process-tree exit, or durable recovery.

### Mandatory `WorkerExecution` contract

Qualify all of the following before declaring `WorkerExecution`:

- run a prompt to unattended completion without a terminal or interactive confirmation;
- accept Wrighty's complete approved context over standard input, never through process arguments;
- produce stable structured output from which success, failure, a bounded final message, and a
  session ID can be parsed;
- implement `BuildCheck` as a read-only probe that succeeds and emits a session ID matching the
  generated or preassigned handle—Wrighty requires this even for a fresh-only agent;
- map read-only, workspace, and full profiles without silently widening access, and report
  `Partial` or `Unrestricted` whenever the vendor cannot enforce the requested boundary;
- distinguish a vendor-native file-write sandbox from shell-command confinement: if shell commands
  can write outside the workspace, `ConfinesFileWrites` must be false;
- validate model and effort values before process start, even when model discovery is absent; and
- tolerate Wrighty's generic timeout, cancellation, and process-tree termination lifecycle.

### Optional capabilities

Declare a capability only when its binding and qualification evidence exist:

| Capability | Required binding | Behavior when absent |
| --- | --- | --- |
| `WorkerExecution` | `IAgentAdapter` | No automated execution or worker selection |
| `Resume` | `IAgentResumeAdapter` | Fresh sessions only; no continuation, recovery, or two-turn enforced assessment |
| `ModelDiscovery` | `IAgentModelDiscovery` | Explicitly configured model IDs still work when the adapter validates them |
| `SessionExport` | `IAgentSessionExporter` | Handoffs use bounded workspace summaries without a vendor transcript |
| `SkillInstallation` | `AgentSkillTarget` | Wrighty cannot install or update its instructions for the agent |
| `ContextDetection` | `IAgentContextDetector` | Callers must identify agent and session context explicitly |
| `InteractiveCli` | `IAgentInteractiveAdapter` | No `resume-command` execution or terminal takeover |
| `DesktopLaunch` | `IAgentDesktopAdapter` and `AgentLocalLaunch` | No Desktop action; CLI support remains independent |
| `GitHubProjection` | `AgentProjection` | The agent is unavailable in GitHub Project agent policy |

Apply these additional rules to the optional bindings:

- `SessionExport` reads must be bounded and degrade to an explicit unavailable result; a missing
  transcript cannot block a handoff. If the vendor's sanitized export removes useful session text,
  use the narrowest safe local source and apply Wrighty's own bounding and sanitization.
- Prefer `EnvironmentAgentContextDetector` for ordered session variables and bounded presence
  signals. Agent presence without a session ID is still useful and must not invent an ID.
- Agents may share a physical skill target, but a shared target ID must have identical path and
  transformation metadata. Use `RequiresInvocationPolicy` for a target-specific front-matter
  transformation.
- Qualify both user- and project-scoped skill discovery. Record the vendor's behavior when the same
  skill name exists at both scopes—precedence, duplication in selectors, or another outcome—and
  verify that Wrighty diagnoses the duplicate without guessing which copy wins.
- Interactive invocations must use the descriptor's allowlisted executable.
- A Desktop application or URI scheme is not proof that it can address an existing session.
  Declare `DesktopLaunch` only after validating that exact round trip on every declared operating
  system; otherwise keep Desktop absent even when the application is installed.
- Model identifiers may contain a provider and vendor-specific variant dimensions. Preserve the
  complete executable model ID rather than assuming a single global model-name namespace.
- Agent identity and capacity-provider identity are separate concepts. If the CLI fronts several
  providers, qualify and key provider limits independently instead of treating the agent ID as the
  subscription owner.
- GitHub projection option names are persistent schema and must not be casually renamed.

An absent capability is supported behavior, not an unfinished switch arm. Keep the service and its
metadata absent and ensure the caller presents a bounded unavailable reason.

## Product surfaces

The CLI composition root creates the registry once and passes it to worker execution, runtime and
version discovery, model discovery, context detection, skill availability, GitHub projection,
local session launch, and the web server. Do not add a parallel `ForAgent` switch or a second list
of supported IDs.

The web forms bind permission overrides and fallback entries as collections keyed by descriptor
ID. A new worker-capable descriptor therefore appears in selectors and settings without another
property. Configuration files remain unchanged: their existing dictionaries accept the new key
once validation recognizes the registered ID.

Two decisions remain deliberately manual:

- Usage-failure fallback edges can spend another provider's subscription. Registering an agent
  must not silently add it to default fallback order.
- Adding `GitHubProjection` changes the required Project schema. `wrighty init --check` must report
  the missing options, and initialization or upgrade must observe and reuse their real option IDs
  before work-item policy can use them.

Claim attribution is broader than worker execution. Historical or external agent names may render
as bounded descriptive text, while only registered worker-capable IDs receive execution actions.
The reserved `other` attribution value is never a built-in worker ID.

## Conformance and qualification

Extend the fake-fourth-agent coverage rather than adding a one-off assertion for the new vendor.
The registry tests must prove that the integration reaches generic runtime, fresh/check/resume,
capability, context, local-launch, GitHub projection, and web presentation paths. Cross-vendor
contract tests should enumerate registered integrations; vendor protocol fixtures remain
vendor-specific.

Run the normal quality gate:

```shell
dotnet test Wrighty.slnx --configuration Release
npm test
python3 -m unittest discover -s tests/PackageManagerManifestTests -p 'test_*.py'
python3 -m unittest discover -s tests/ReleaseSmokeTests -p 'test_*.py'
```

For every candidate, record an evidence row with the agent and CLI version, platform, invocation or
transport, result (`passed`, `partial`, or `unavailable`), and the known limitation. Before
documenting a real capability, qualify:

- executable/version discovery and authenticated readiness;
- fresh, check, and resume session identity;
- standard-input context transport and sanitized result/failure parsing;
- workspace, read-only, and full permission profiles, including parent-path and network probes;
- hosted-worker drain and immediate interruption, including child-process exit and recovery;
- model/effort discovery or validation, if declared;
- bounded transcript export and missing-export degradation, if declared;
- skill discovery, user-default and explicit project installation, update, uninstall, duplicate
  scope diagnosis, and explicit invocation, if declared;
- interactive CLI and Desktop round trips on every declared operating system; and
- Local Markdown configuration plus GitHub Project initialization/upgrade round trips.

Add a fresh-only fake adapter to conformance coverage. It must compile with `IAgentAdapter` alone,
register with only `WorkerExecution`, remain absent from resume/interactive/Desktop services, and
receive bounded unavailable behavior from those product surfaces. This prevents optional methods
from drifting back into the minimum adapter contract.

Finally, update [`supported-agents.md`](../reference/supported-agents.md) with only the capabilities
that were actually qualified. Dynamic or configuration-defined agent plugins are not supported.
