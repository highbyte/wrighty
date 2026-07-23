# User settings

User settings are durable, **user-scoped** preferences that apply to every repository this Wrighty
installation works — distinct from the per-repository [`.wrighty.json`](configuration.md) tracker
configuration, which is committed and shared between machines. A user setting is a deliberate
personal choice (for example, the symbolic host label published to GitHub) that should *not* travel
with the repository.

They are also distinct from the machine-local **cache** (regenerable node-ID and session data): the
settings file is authoritative and is never regenerated, so deleting it loses the operator's
choices rather than a rebuildable derivative.

## Where they are stored

A single JSON file, `settings-v1.json`, in the OS-appropriate user configuration directory:

| Platform | Default directory |
| --- | --- |
| macOS | `~/Library/Application Support/wrighty` |
| Linux | `$XDG_CONFIG_HOME/wrighty`, or `~/.config/wrighty` when `XDG_CONFIG_HOME` is unset |
| Windows | `%APPDATA%\wrighty` |

Set the `WRIGHTY_CONFIG_DIR` environment variable to override the directory (used for tests and
non-standard layouts). Writes are atomic, and a corrupt or unreadable file is tolerated by falling
back to defaults rather than failing a command.

## Managing settings

Use the `wrighty config` command group; there is no need to edit the file by hand.

```shell
wrighty config show                            # print each setting and its effective value
wrighty config show --json                     # the same, as structured JSON
wrighty config set-host "workstation-alpha"    # set the symbolic host label
wrighty config set-host --clear                # revert the host label to the default
```

## Settings reference

Every user setting and its default is listed below.

| Setting | CLI | Default | Description |
| --- | --- | --- | --- |
| `hostLabel` | `wrighty config set-host <label>` / `--clear` | (unset → `anonymous`) | Symbolic host name published in the GitHub [handover comment](worker.md#github-handover-comment) in place of the real machine name (`Environment.MachineName`, which often embeds a person's name). When unset, the comment shows the placeholder `anonymous`, so the real machine name is never published by default. Set a label that is meaningful to you but reveals nothing to disambiguate which machine holds a retained worktree. |

Additional user-scoped settings introduced later are documented here.
