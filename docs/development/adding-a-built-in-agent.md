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

## Implementation checklist

### Identity and execution

- Add one descriptor and integration in `BuiltInAgentRegistry`. Use a lowercase, bounded ID that
  will remain stable in configuration and stored work-item/session data.
- Add the vendor's `IAgentAdapter` in its own file under
  `src/Highbyte.Wrighty.Core/Workers/`. The adapter owns fresh, approved-context, resume, check,
  interactive CLI, and Desktop construction; permission and model/effort capability; session
  handle creation and emitted-ID matching; skill-prompt decoration; and result parsing.
- Keep invocations structured. Resolve only the descriptor's compiled executable name, pass
  approved work-item context over standard input, and never interpolate untrusted text into a
  shell command.
- Preserve the generic `WorkerRunHost` lifecycle. The integration supplies vendor protocol; it
  does not take ownership of drain, immediate interruption, process-tree exit, or durable recovery.

### Optional capabilities

Declare a capability only when its implementation and qualification evidence exist:

- `ModelDiscovery`: implement `IAgentModelDiscovery` and bind it in the integration.
- `SessionExport`: implement `IAgentSessionExporter`. Reads must be bounded and must degrade to an
  explicit unavailable result; a missing transcript cannot block a handoff.
- `ContextDetection`: register an `IAgentContextDetector`. Prefer
  `EnvironmentAgentContextDetector` for ordered session variables and bounded presence signals.
  The provider handles conflicts and rejects unsafe session IDs.
- `SkillInstallation`: declare an `AgentSkillTarget`. Agents may share a physical target, but a
  shared target ID must have identical path and transformation metadata. Use
  `RequiresInvocationPolicy` for a target-specific front-matter transformation.
- `InteractiveCli`: the adapter must return an allowlisted `LocalAgentInvocation` using its
  descriptor executable.
- `DesktopLaunch`: declare `AgentLocalLaunch` with the application, URI scheme, exact supported
  operating systems, and whether the route is supported or experimental; return a matching
  independently validated address from the adapter.
- `GitHubProjection`: declare stable option names, descriptions, colors, and distinct projection
  orders. Existing option spelling is persistent schema and must not be casually renamed.

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

Before documenting a real capability, record the tested CLI version and platforms and qualify:

- executable/version discovery and authenticated readiness;
- fresh, check, and resume session identity;
- standard-input context transport and sanitized result/failure parsing;
- workspace, read-only, and full permission profiles, including parent-path and network probes;
- hosted-worker drain and immediate interruption, including child-process exit and recovery;
- model/effort discovery or validation, if declared;
- bounded transcript export and missing-export degradation, if declared;
- skill discovery, installation, update, and explicit invocation;
- interactive CLI and Desktop round trips on every declared operating system; and
- Local Markdown configuration plus GitHub Project initialization/upgrade round trips.

Finally, update [`supported-agents.md`](../reference/supported-agents.md) with only the capabilities
that were actually qualified. Dynamic or configuration-defined agent plugins are not supported.
