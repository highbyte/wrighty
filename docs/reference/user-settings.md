# User settings

User settings are durable, **user-scoped** preferences that apply to every repository this Wrighty
installation works with — distinct from the per-repository [`.wrighty.json`](configuration.md) tracker
configuration, which is committed and shared between machines. A user setting is a deliberate
personal choice (for example, the symbolic host label published to GitHub) that should *not* travel
with the repository.

They are also distinct from the machine-local **cache** (regenerable node-ID and session data): the
settings file is authoritative and is never regenerated, so deleting it loses the operator's
choices rather than a rebuildable derivative.

## Where they are stored

A single JSON file, `settings-v2.json`, in the OS-appropriate user configuration directory:

| Platform | Default directory |
| --- | --- |
| macOS | `~/Library/Application Support/wrighty` |
| Linux | `$XDG_CONFIG_HOME/wrighty`, or `~/.config/wrighty` when `XDG_CONFIG_HOME` is unset |
| Windows | `%APPDATA%\wrighty` |

Set the `WRIGHTY_CONFIG_DIR` environment variable to override the directory (used for tests and
non-standard layouts). Writes are atomic, and a corrupt or unreadable file is tolerated by falling
back to defaults rather than failing a command.

An older `settings-v1.json` is read and migrated forward automatically. It is **left in place**
rather than upgraded, so an older Wrighty on the same machine keeps working; the first change writes
`settings-v2.json` alongside it. Until then `wrighty config user show` reports which file it is
reading from.

## Managing settings

Use the `wrighty config` command group; there is no need to edit the file by hand.

```shell
wrighty config user show                       # print user-scoped settings and effective values
wrighty config user host set workstation-alpha # set the symbolic host label
wrighty config user host clear                 # revert the host label to the default

wrighty config profile models                  # what each installed agent reports it can run
wrighty config profile list                    # your execution-profile mappings
wrighty config profile set deep --agent claude --model opus --effort xhigh
wrighty config profile unset deep --agent claude
```

The web console edits this scope too. `wrighty web` shows the agent enablement allowlist in the
header's **Agents** menu and the remaining user settings inside the **User** section of
**Settings**. Both write the same file and refuse a save whose view of it has gone stale — so a
page left open while you change something from a terminal cannot overwrite that change.

`wrighty config show` displays both user and repository configuration. The user section includes
the host label and all stored execution-profile mappings, and always prints the absolute
`settings-v2.json` path and whether the file exists; when it does not, Wrighty reports that defaults
are in effect. `wrighty config user show --json` exposes the same source and effective values for
automation. The aggregate command also reports Wrighty's effective filesystem footprint; see
[Storage and filesystem reference](storage.md). Repository settings use
`wrighty config repository ...`; see
[Configuration](configuration.md#inspect-and-safely-change-repository-policy).

## Settings reference

Every user setting and its default is listed below.

| Setting | CLI | Default | Description |
| --- | --- | --- | --- |
| `hostLabel` | `wrighty config user host set <label>` / `clear` | (unset → `anonymous`) | Symbolic host name published in the GitHub [status comment](worker.md#github-status-comment) in place of the real machine name (`Environment.MachineName`, which often embeds a person's name). When unset, the comment shows the placeholder `anonymous`, so the real machine name is never published by default. Set a label that is meaningful to you but reveals nothing to disambiguate which machine holds a retained worktree. |
| `enabledAgents` | Web console **Agents** menu | (unset → every detected agent) | Explicit allowlist for agents eligible for automatic Wrighty-managed work on this computer. The first toggle materializes the complete detected set before applying the change; later installations are therefore not silently enabled. An explicit `--agent` selection remains a one-run override. |
| `workerProfiles` | `wrighty config profile set/unset` | (unset → built-in tiers) | Your model and reasoning-effort mapping for each execution profile and agent. User-scoped because a model name describes what you have installed and are entitled to, not what the project agreed on — the shared vocabulary lives in the repository instead. Absent means the built-in `economy`/`balanced`/`deep` tiers apply, which set effort only. See [Execution profiles](execution-profiles.md). |

Additional user-scoped settings introduced later are documented here.
