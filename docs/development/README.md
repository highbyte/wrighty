# Developing Wrighty

The implementation is guided by the
[original design](../design/agent-facing-work-item-tracker-cli.md) and the related public design
documents in [`docs/design/`](../design/).

Run the commands in this guide from the repository root.

## Prerequisites

- [.NET 10 SDK](https://dotnet.microsoft.com/download/dotnet/10.0) for building and running the
  .NET test suite.
- Python 3 for the package-manifest tests.
- An authenticated [GitHub CLI](https://cli.github.com/) session and a disposable issue and
  Project only for live GitHub integration testing.

## Build and run

For repeated development use, source the activation script once in the current Bash or Zsh
session. It builds the Debug artifact, defines a temporary `wrighty` shell function that invokes
the built DLL directly, and prepends the artifact directory to `PATH`:

```shell
source scripts/activate-development-cli.sh
wrighty --help
wrighty list --compact
```

The command works from any directory and avoids `dotnet run` and project evaluation on each
invocation. The `PATH` change is inherited by agent CLIs started from the activated shell and by
their child command shells. For example:

```shell
source scripts/activate-development-cli.sh
claude
```

For a local Claude Desktop session on macOS, fully quit Claude first, then launch a new application
process from the activated terminal so it inherits the modified `PATH`:

```shell
source scripts/activate-development-cli.sh
open -n -a "Claude"
```

In the new Desktop session, ask Claude to verify the development command before using Wrighty:

```shell
command -v wrighty
wrighty --help
```

The agent session or desktop application must be started after activation; an already-running
process cannot receive the changed environment. Desktop applications launched independently from
the Dock or Finder and new terminal sessions do not inherit it. Remove the function and restore
the original `PATH` with:

```shell
wrighty_deactivate
```

Set `WRIGHTY_DEV_CONFIGURATION=Release` before sourcing to use a Release build. Set
`WRIGHTY_DEV_NO_BUILD=1` to reuse an existing artifact without building first.

For one-off commands without activation:

```shell
dotnet build Wrighty.slnx
dotnet run --project src/Highbyte.Wrighty.Cli -- init
dotnet run --project src/Highbyte.Wrighty.Cli -- list --compact
dotnet run --project src/Highbyte.Wrighty.Cli -- get 42
dotnet run --project src/Highbyte.Wrighty.Cli -- creation-attempt new --json
dotnet run --project src/Highbyte.Wrighty.Cli -- create --title "Example" --body-file description.md --priority P1 \
  --creation-attempt-id 019f5c485c2b7862aeac80eb638a7b5c
dotnet run --project src/Highbyte.Wrighty.Cli -- claim 42
dotnet run --project src/Highbyte.Wrighty.Cli -- move 42 "In Progress"
dotnet run --project src/Highbyte.Wrighty.Cli -- edit 42 --priority P0 --body-file description.md
dotnet run --project src/Highbyte.Wrighty.Cli -- pick
dotnet run --project src/Highbyte.Wrighty.Cli -- finish 42
dotnet run --project src/Highbyte.Wrighty.Cli -- archive 42
dotnet run --project src/Highbyte.Wrighty.Cli -- list --archived
dotnet run --project src/Highbyte.Wrighty.Cli -- unarchive 42
dotnet run --project src/Highbyte.Wrighty.Cli -- release 42
```

Every command except help supports `--json`. `list` additionally supports `--compact`, `--status`,
`--limit`, `--archived`, and `--include-archived`.

## Test

```shell
dotnet test Wrighty.slnx
python3 -m unittest discover -s tests/PackageManagerManifestTests -p 'test_*.py'
```

Unit tests and local filesystem integration tests do not call GitHub. Live GitHub protocol and
archive validation requires an authenticated `gh` session and a disposable issue/Project; it is
intentionally not part of the normal test run.

The package-manifest tests exercise Homebrew and Scoop generation locally and do not access either
companion repository.

### Integration tests

The repository includes an isolated Local Markdown claim-fencing smoke test and opt-in workflows
that use real GitHub issues and Projects. See [Integration testing](integration-testing.md) for
the backend-specific guarantees, fixture setup, safety constraints, and commands.

## Release

Publishing a GitHub release triggers the release workflow. Its tag must be a semantic version,
optionally prefixed with `v` (for example, `v0.1.0-alpha`). The workflow uses that version to
publish self-contained, single-file `wrighty` CLI builds for `win-x64`, `win-arm64`,
`linux-x64`, `linux-arm64`, and `osx-arm64`.

For each runtime, the release receives a `wrighty-<version>-<rid>.zip` asset containing the
executable and bundled skill files, plus a matching `.zip.sha256` file in conventional
`<sha256>  <filename>` format.

The release workflow updates `highbyte/homebrew-tap` and `highbyte/scoop-bucket` after it has
published and verified the release checksums. Its manual-dispatch mode builds the same per-runtime
ZIP and checksum artifacts without creating a release or updating either package-manager
repository. Before publishing a release, configure the `PACKAGE_MANAGER_TOKEN` repository secret
with Contents read/write access to both companion repositories.
