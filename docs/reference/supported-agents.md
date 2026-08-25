# Supported agents and surfaces

Wrighty supports Claude Code, Codex, and GitHub Copilot. Support is specific to a capability and
surface: an agent may be available to the headless worker, through the Wrighty skill, through its
interactive CLI, or through a Desktop application.

| Agent family | Headless start/resume | Model discovery | Session export for handoff | Context detection | Interactive surfaces |
| --- | --- | --- | --- | --- | --- |
| **Claude** | Yes, through Claude Code CLI | Yes | Yes, from the local Claude Code transcript store | Session and presence signals from Claude Code | Wrighty skill and CLI; Claude Desktop on macOS and Windows is experimental |
| **Codex** | Yes, through Codex CLI | Yes | Yes, from the local Codex rollout store | Thread signal from Codex | Wrighty skill and CLI; ChatGPT Desktop on macOS and Windows |
| **GitHub Copilot** | Yes, through Copilot CLI | Yes | Yes for worker-owned sessions when Wrighty's requested share export completes | Session signal from Copilot CLI | Wrighty skill and CLI; GitHub Copilot Desktop on macOS, Windows, and Linux |

The worker always uses a locally installed agent CLI. Skill support describes where the bundled
Wrighty instructions can be used interactively; it does not mean every listed surface can run a
headless worker or open a retained local session.

## What support means for the worker

For headless operation, **supported** means that Wrighty has an adapter for the agent family.
**Installed** means that the corresponding CLI executable can be found on the current machine, and
**ready** means that `wrighty worker --check` can start it and obtain a usable session handle.

All three agent families support headless start and same-agent resume. Their permission and session
interfaces differ. See [Autonomous worker mode](worker.md#local-agent-availability) for discovery
and readiness, [spawned-agent permissions](worker.md#spawned-agent-permissions) for the effective
sandbox behavior, and the [verified vendor capability matrix](worker.md#verified-vendor-capability-matrix)
for the underlying CLI operations.

## Session handoff

Claude, Codex, and Copilot can all be targets of a cross-agent handoff. Claude and Codex normally
provide their local session transcripts when they are the source. A Copilot transcript is available
only for worker-owned sessions for which Wrighty's requested export completed. If no transcript is
available, the handoff still starts the target agent with the retained workspace and a
workspace-only context packet.

See [Usage recovery and agent handoff](usage-recovery-and-agent-handoff.md#cross-agent-handoff) for
the workflow and the [session export matrix](worker.md#session-export-for-cross-agent-handoff) for
the per-agent limitations.

## Interactive and Desktop surfaces

The [Agent skills](agent-skills.md#supported-skill-surfaces) page lists the supported interactive
surfaces and how the Wrighty skill is invoked on each one.

Using the Wrighty skill from Claude Code in Desktop is supported. This is separate from opening a
retained Claude Code CLI session in Claude Desktop through a session deep link, which remains
experimental.

The web console can open a retained Desktop session through the application's registered URI
handler. Claude Desktop and Codex in ChatGPT Desktop are supported on macOS and Windows; GitHub
Copilot Desktop is supported on macOS, Windows, and Linux. Wrighty can open an agent CLI in Apple
Terminal on macOS or Windows Terminal on native Windows. The copyable CLI and headless commands
remain available on every platform.

Windows CLI launching requires Windows Terminal and its `wt.exe` app execution alias. Wrighty
opens a new window in the recorded workspace and passes the adapter-built executable, arguments,
and claim environment directly, without constructing a PowerShell or `cmd.exe` command. Automatic
CLI launching from WSL is not supported.

The Desktop application must use the same local session store as the worker that recorded the
session. This matters especially when Wrighty runs inside WSL: Codex in WSL uses the Linux
`~/.codex` directory by default, while ChatGPT Desktop uses `%USERPROFILE%\.codex`. Point
`CODEX_HOME` at the Windows directory or synchronize the stores before expecting a Windows Desktop
deep link to find that session. The same general rule applies whenever an agent CLI and its Desktop
application use separate local profiles.

[ChatGPT Desktop](https://learn.chatgpt.com/docs/reference/commands#deep-links) and
[GitHub Copilot Desktop](https://docs.github.com/en/copilot/how-tos/github-copilot-app/open-with-deep-links)
document their Desktop address shapes. Copilot Desktop must also be configured to retain CLI
sessions, and some versions may open its Home view instead of the requested session; opening the
Copilot CLI remains the fallback. Claude Desktop session opening uses an undocumented vendor link
and is therefore offered as an experimental feature that can be disabled.

See [Web console](web-console.md) for the ownership rules, platform checks, and current Desktop
limitations.
