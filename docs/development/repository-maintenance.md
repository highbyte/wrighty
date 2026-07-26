# Repository maintenance

This runbook describes Wrighty's current repository, security, dependency, and release
maintenance procedures. It is for the maintainer and for an AI agent acting under the
maintainer's direction.

An agent may inspect repository state and diagnose drift without additional approval. Changing
GitHub settings, rotating or revoking credentials, bypassing protections, deleting history,
creating tags or releases, and publishing are outward-facing actions and require the maintainer's
explicit approval. Nothing in this document authorizes unattended monitoring or background
changes.

Run commands from the repository root. Examples use `highbyte/wrighty`; set
`WRIGHTY_REPOSITORY` when intentionally checking a different repository.

## Check repository settings

Run the read-only drift check after changing repository settings and before preparing a release:

```shell
scripts/repository-settings.sh check
```

The check covers:

- squash-only merging and automatic deletion of merged branches;
- immutable GitHub releases;
- the protected `v*` release-tag namespace;
- the `release` Environment's allowed branch and tag patterns; and
- the presence and rotation deadline of the release GitHub App credentials.

The script reports drift and exits nonzero. It does not correct settings in `check` mode.

With explicit approval, apply the non-secret configuration:

```shell
scripts/repository-settings.sh apply --confirm highbyte/wrighty
scripts/repository-settings.sh check
```

`apply` changes repository, Environment, and tag-ruleset settings. It never creates a GitHub App
and never reads or writes a private key.

## Merge and default-branch policy

Allow squash merging only. Disable merge commits and rebase merging, and automatically delete a
merged branch. The default-branch ruleset must block direct pushes, force pushes, and branch
deletion and require pull requests, resolved review conversations, strict up-to-date status
checks, and these observed checks:

- `Build and test`
- `Dependency review`
- `scan`
- `SonarCloud Code Analysis`

Required-check names and their reporting Apps must come from an observed green pull request, not
from workflow job identifiers.

### Recover from a blocking ruleset mistake

A stale or misspelled required check can make every pull request unmergeable. Diagnose the exact
rule first. If correcting it requires temporarily disabling enforcement:

1. Obtain explicit maintainer approval for the named ruleset.
2. Disable only that ruleset:

   ```shell
   scripts/repository-settings.sh set-ruleset-enforcement \
     "main protection" disabled --confirm highbyte/wrighty
   ```

3. Make the smallest necessary correction in GitHub.
4. Immediately reactivate the ruleset and verify repository state:

   ```shell
   scripts/repository-settings.sh set-ruleset-enforcement \
     "main protection" active --confirm highbyte/wrighty
   scripts/repository-settings.sh check
   ```

Record why enforcement was disabled, what changed, and when it was re-enabled. Do not create a
standing bypass actor as a substitute for this recovery procedure.

## Dependency maintenance

Review routine, non-security dependency updates during scheduled maintenance. Read the upstream
release notes, assess compatibility, and require the normal merge gates. Do not automatically
merge dependency updates.

A newly disclosed high- or critical-severity vulnerability in an existing dependency interrupts
planned work and should be investigated promptly. Moderate- and low-severity findings normally
join scheduled maintenance unless the affected behavior makes their practical risk higher.

GitHub Dependency Review is the pull-request gate. For NuGet, retain `NuGetAuditMode=all` and
treat `NU1903` and `NU1904` as errors so transitive high- and critical-severity advisories are also
covered.

## Secret-scanning response

GitHub push protection blocks known secrets before a push when possible. GitHub or a credential
partner may also raise an alert or revoke a detected credential. An AI agent does not monitor or
act on these events unless explicitly invoked or it encounters an alert during assigned work.

For a blocked push:

1. Stop; do not bypass push protection.
2. Determine whether the value is a real credential.
3. If the credential may have left a controlled local context, rotate or revoke it before
   continuing.
4. Remove the value from the commit and store it through the approved secret mechanism.

For an exposure in a pushed commit, log, release asset, or other shared location, treat the
credential as compromised. Rotate or revoke it first; removing the value from `HEAD` is not
sufficient. An agent must ask before rotating credentials, rewriting or deleting history,
dismissing an alert, or bypassing protection.

Keep private vulnerability reports private until a fix or effective mitigation is ready. Do not
copy sensitive details into a public issue or pull request.

## Configure release access

Wrighty uses a private GitHub App instead of a long-lived personal access token to update the
Homebrew tap and Scoop bucket.

### Create and install the GitHub App

Create a private App named `wrighty-release` in the maintainer account:

- disable webhooks;
- do not enable user authorization;
- grant repository **Contents: Read and write** and no other repository permissions; and
- install it only on `highbyte/homebrew-tap` and `highbyte/scoop-bucket`.

Generate a private key. GitHub App private keys do not have an intrinsic expiry date, so Wrighty
enforces an annual rotation deadline as repository policy.

### Configure the release Environment

After explicit approval, create the non-secret Environment and tag settings:

```shell
scripts/repository-settings.sh apply --confirm highbyte/wrighty
```

In the `release` Environment, set:

- variable `WRIGHTY_RELEASE_APP_CLIENT_ID` to the App's client ID;
- secret `WRIGHTY_RELEASE_APP_PRIVATE_KEY` to the complete downloaded PEM private key; and
- variable `WRIGHTY_RELEASE_APP_KEY_ROTATE_BY` to the next rotation deadline in `YYYY-MM-DD`
  format.

The Environment permits deployments only from branch `main` and tags matching `v*`. It has no
required reviewer because the release skill already stops for explicit confirmation before it
creates the tag or dispatches publication.

Prefer storing the private key in the configured secret manager under the identical name
`WRIGHTY_RELEASE_APP_PRIVATE_KEY`. Feed it directly to GitHub without printing or capturing it:

```shell
secret-pipe WRIGHTY_RELEASE_APP_PRIVATE_KEY |
  gh secret set WRIGHTY_RELEASE_APP_PRIVATE_KEY \
    --repo highbyte/wrighty --env release
```

Set the non-secret client ID through the GitHub UI or:

```shell
gh variable set WRIGHTY_RELEASE_APP_CLIENT_ID \
  --repo highbyte/wrighty --env release --body "<client-id>"
```

Record the next annual deadline from the rotation date:

```shell
scripts/repository-settings.sh record-key-rotation --confirm highbyte/wrighty
scripts/repository-settings.sh check
```

Finally, run the read-only credential preflight from protected `main`:

```shell
skills/release-wrighty/scripts/prepare-release.sh dispatch-credential-preflight
```

Wait for the `Release` workflow to finish. It mints a one-hour token limited to Contents access
on the two package repositories and performs read-only access checks.

## Rotate the release App private key

Rotate annually, or immediately after suspected exposure:

1. Generate a second private key for `wrighty-release`. Keep the old key active.
2. Store the new key in the approved secret manager.
3. Replace `WRIGHTY_RELEASE_APP_PRIVATE_KEY` in the `release` Environment without displaying the
   value.
4. Run the credential preflight and wait for it to pass.
5. Record the rotation date:

   ```shell
   scripts/repository-settings.sh record-key-rotation --confirm highbyte/wrighty
   ```

6. Delete the old key from the GitHub App.
7. Run `scripts/repository-settings.sh check`.

If preflight fails, keep the old key, restore the Environment secret from it if necessary, and
diagnose the new key, App installation, client ID, permissions, repository selection, and
rotation deadline. Do not delete the old key until the replacement passes.

## Prepare and publish a release

Invoke the repository's `release-wrighty` skill when deciding to release. It:

- inspects the description and actual diff of every merged pull request since the applicable
  published baseline;
- identifies commits without an associated pull request;
- recommends a Semantic Versioning tag and short release brief;
- includes GitHub-generated pull-request and comparison links;
- asks for edits and explicit confirmation;
- runs credential preflight and revalidates the reviewed remote commit; and
- creates an empty draft release and dispatches the release workflow.

The default target is the current commit at the tip of remote `main`. An alternate target must be
an existing remote commit. Tags always use `v<semantic-version>`.

The workflow builds self-contained ZIP files for `win-x64`, `win-arm64`, `linux-x64`,
`linux-arm64`, and `osx-arm64`, with a matching `.zip.sha256` for each archive. Before publishing,
it validates assembly and informational versions, creates build-provenance attestations, and runs
a real disposable Local Markdown smoke test on Ubuntu, Windows, and macOS.

Immediate publication makes the completed draft immutable, verifies the release and every asset,
updates the Homebrew tap and Scoop bucket with the App token, installs the packages on their
supported runners, repeats the Local Markdown smoke test, and uninstalls the package.

If the maintainer chooses a draft, the workflow stops after verified assets are attached. Later
publishing that draft through GitHub triggers public-release verification, package-manager
updates, installation smoke tests, and cleanup.

Standard verification commands include:

```shell
gh release verify vX.Y.Z --repo highbyte/wrighty
gh release verify-asset vX.Y.Z wrighty-X.Y.Z-linux-x64.zip \
  --repo highbyte/wrighty
gh attestation verify wrighty-X.Y.Z-linux-x64.zip \
  --repo highbyte/wrighty \
  --signer-workflow highbyte/wrighty/.github/workflows/release.yml
```

SBOM publication is intentionally deferred. Do not publish or attest an SBOM until there is a
validated process that inventories the entire self-contained archive, including the bundled .NET
runtime, for every release runtime identifier.

## Release failure and exceptional rollback

On workflow failure, diagnose and report the cause. Ask before retrying a workflow, making
corrective repository changes, or cleaning up a partial tag or draft. If cleanup is declined,
leave the state intact and report exactly what remains.

The normal correction for a published release is a new version. In exceptional circumstances,
the maintainer may explicitly decide to delete a release and tag:

1. Confirm the exact tag and the irreversible consequences. An immutable release tag name cannot
   be reused.
2. With explicit approval, temporarily disable only the release-tag ruleset:

   ```shell
   scripts/repository-settings.sh set-ruleset-enforcement \
     "release tags" disabled --confirm highbyte/wrighty
   ```

3. Delete the release and tag:

   ```shell
   gh release delete vX.Y.Z --repo highbyte/wrighty --cleanup-tag
   ```

4. Immediately reactivate and verify the ruleset:

   ```shell
   scripts/repository-settings.sh set-ruleset-enforcement \
     "release tags" active --confirm highbyte/wrighty
   scripts/repository-settings.sh check
   ```

If package-manager publication fails after the GitHub release is public, keep the immutable
release intact. Diagnose and correct the downstream update separately; never replace published
assets with `--clobber`.
