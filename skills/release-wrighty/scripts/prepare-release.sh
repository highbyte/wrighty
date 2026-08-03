#!/usr/bin/env bash
#
# Deterministic evidence, draft creation, and workflow dispatch for the
# release-wrighty skill. Semantic impact analysis remains the agent's job.

set -euo pipefail

readonly repository="${WRIGHTY_REPOSITORY:-highbyte/wrighty}"
readonly workflow="release.yml"

usage() {
  cat <<EOF
Usage:
  prepare-release.sh evidence <stable|prerelease> [target] [output-directory]
  prepare-release.sh notes <tag> <target-sha> <previous-tag|-> <output-file>
  prepare-release.sh revalidate <stable|prerelease> <target> <target-sha> <previous-tag|-> <tag>
  prepare-release.sh create-draft <stable|prerelease> <tag> <target-sha> <title> <notes-file> --confirm <tag>
  prepare-release.sh dispatch-credential-preflight
  prepare-release.sh dispatch-release <tag> <draft|publish> --confirm <tag>

The default evidence target is remote main. Mutating operations require the
confirmed tag to be repeated exactly.
EOF
}

require_tools() {
  command -v gh >/dev/null 2>&1 || {
    echo "GitHub CLI (gh) is required." >&2
    exit 1
  }
  command -v python3 >/dev/null 2>&1 || {
    echo "Python 3 is required." >&2
    exit 1
  }
  gh auth status >/dev/null
}

validate_kind() {
  local kind="$1"
  [[ "$kind" == "stable" || "$kind" == "prerelease" ]] || {
    echo "Release kind must be stable or prerelease." >&2
    exit 2
  }
}

validate_tag() {
  local tag="$1"
  python3 - "$tag" <<'PY'
import re
import sys

numeric = r"(?:0|[1-9][0-9]*)"
nonnumeric = r"(?:[0-9A-Za-z-]*[A-Za-z-][0-9A-Za-z-]*)"
prerelease = rf"(?:{numeric}|{nonnumeric})"
pattern = re.compile(
    rf"^v{numeric}\.{numeric}\.{numeric}"
    rf"(?:-{prerelease}(?:\.{prerelease})*)?"
    r"(?:\+[0-9A-Za-z-]+(?:\.[0-9A-Za-z-]+)*)?$"
)
if not pattern.fullmatch(sys.argv[1]):
    raise SystemExit(f"Invalid Wrighty release tag: {sys.argv[1]!r}")
PY
}

resolve_remote_commit() {
  local ref="$1"
  gh api "repos/$repository/commits/$ref" --jq .sha
}

write_releases() {
  gh release list \
    --repo "$repository" \
    --limit 100 \
    --exclude-drafts \
    --json tagName,name,isDraft,isImmutable,isLatest,isPrerelease,publishedAt
}

resolve_baseline() {
  local kind="$1"
  write_releases | python3 -c '
import json
import sys

kind = sys.argv[1]
releases = sorted(
    (release for release in json.load(sys.stdin) if not release["isDraft"]),
    key=lambda release: release["publishedAt"] or "",
    reverse=True,
)
eligible = (
    releases
    if kind == "prerelease"
    else [release for release in releases if not release["isPrerelease"]]
)
print(eligible[0]["tagName"] if eligible else "-")
' "$kind"
}

evidence() {
  local kind="$1"
  local target="${2:-main}"
  local output_directory="${3:-}"
  validate_kind "$kind"

  if [[ -z "$output_directory" ]]; then
    output_directory="$(
      mktemp -d "${TMPDIR:-/tmp}/wrighty-release-evidence.XXXXXX"
    )"
  else
    mkdir -p "$output_directory"
    [[ -z "$(find "$output_directory" -mindepth 1 -maxdepth 1 -print -quit)" ]] || {
      echo "Evidence output directory must be empty: $output_directory" >&2
      exit 1
    }
  fi

  local target_sha
  local baseline
  target_sha="$(resolve_remote_commit "$target")"
  baseline="$(resolve_baseline "$kind")"
  [[ "$baseline" != "-" ]] || {
    echo "Wrighty has no published baseline release for '$kind' analysis." >&2
    exit 1
  }

  write_releases > "$output_directory/releases.json"
  gh api "repos/$repository/compare/$baseline...$target_sha" \
    > "$output_directory/compare.json"

  local ahead_by
  ahead_by="$(
    python3 - "$output_directory/compare.json" <<'PY'
import json
import sys

with open(sys.argv[1], encoding="utf-8") as source:
    print(json.load(source)["ahead_by"])
PY
  )"
  if [[ "$ahead_by" == "0" ]]; then
    echo "No changes exist after baseline $baseline." >&2
    exit 3
  fi

  mkdir "$output_directory/pull-requests"
  : > "$output_directory/pr-numbers.txt"
  : > "$output_directory/unassociated-commits.jsonl"

  while IFS= read -r commit_sha; do
    [[ -n "$commit_sha" ]] || continue
    local associated=false
    while IFS= read -r pr_number; do
      [[ -n "$pr_number" ]] || continue
      associated=true
      printf '%s\n' "$pr_number" >> "$output_directory/pr-numbers.txt"
    done < <(
      gh api "repos/$repository/commits/$commit_sha/pulls" \
        --jq '.[] | select(.merged_at != null) | .number'
    )

    if [[ "$associated" != "true" ]]; then
      gh api "repos/$repository/commits/$commit_sha" \
        --jq '{
          sha: .sha,
          url: .html_url,
          message: .commit.message,
          authoredAt: .commit.author.date
        }' \
        >> "$output_directory/unassociated-commits.jsonl"
    fi
  done < <(
    python3 - "$output_directory/compare.json" <<'PY'
import json
import sys

with open(sys.argv[1], encoding="utf-8") as source:
    for commit in json.load(source)["commits"]:
        print(commit["sha"])
PY
  )

  sort -nu "$output_directory/pr-numbers.txt" \
    -o "$output_directory/pr-numbers.txt"

  while IFS= read -r pr_number; do
    [[ -n "$pr_number" ]] || continue
    gh pr view "$pr_number" \
      --repo "$repository" \
      --json \
        number,title,body,url,author,mergedAt,mergedBy,baseRefName,headRefName,mergeCommit,additions,deletions,changedFiles,files \
      > "$output_directory/pull-requests/$pr_number.json"
    gh pr diff "$pr_number" \
      --repo "$repository" \
      --patch \
      > "$output_directory/pull-requests/$pr_number.patch"
  done < "$output_directory/pr-numbers.txt"

  python3 - \
    "$repository" \
    "$kind" \
    "$target" \
    "$target_sha" \
    "$baseline" \
    "$output_directory" <<'PY'
import json
from pathlib import Path
import sys

repository, kind, target, target_sha, baseline, output = sys.argv[1:]
root = Path(output)
with (root / "compare.json").open(encoding="utf-8") as source:
    compare = json.load(source)
with (root / "releases.json").open(encoding="utf-8") as source:
    releases = json.load(source)

published = sorted(
    (release for release in releases if not release["isDraft"]),
    key=lambda release: release["publishedAt"] or "",
    reverse=True,
)
pr_numbers = [
    int(number)
    for number in (root / "pr-numbers.txt").read_text(encoding="utf-8").splitlines()
    if number
]
unassociated = [
    json.loads(line)
    for line in (root / "unassociated-commits.jsonl")
    .read_text(encoding="utf-8")
    .splitlines()
    if line
]
metadata = {
    "repository": repository,
    "kind": kind,
    "target": target,
    "targetSha": target_sha,
    "baselineTag": baseline,
    "comparisonUrl": compare["html_url"],
    "aheadBy": compare["ahead_by"],
    "pullRequests": pr_numbers,
    "unassociatedCommits": unassociated,
    "latestPublishedTag": published[0]["tagName"] if published else None,
    "latestStableTag": next(
        (release["tagName"] for release in published if not release["isPrerelease"]),
        None,
    ),
    "latestPrereleaseTag": next(
        (release["tagName"] for release in published if release["isPrerelease"]),
        None,
    ),
}
(root / "metadata.json").write_text(
    json.dumps(metadata, indent=2) + "\n",
    encoding="utf-8",
)

lines = [
    "# Wrighty release evidence",
    "",
    f"- Kind: `{kind}`",
    f"- Target: `{target_sha}` (resolved from `{target}`)",
    f"- Baseline: `{baseline}`",
    f"- Comparison: {compare['html_url']}",
    f"- Commits ahead: {compare['ahead_by']}",
    "",
    "## Pull requests",
    "",
]
for number in pr_numbers:
    with (root / "pull-requests" / f"{number}.json").open(
        encoding="utf-8"
    ) as source:
        pull_request = json.load(source)
    lines.append(
        f"- [#{number}]({pull_request['url']}): {pull_request['title']}"
    )
if not pr_numbers:
    lines.append("- None")

lines.extend(["", "## Unassociated commits", ""])
for commit in unassociated:
    summary = commit["message"].splitlines()[0]
    lines.append(f"- [`{commit['sha'][:12]}`]({commit['url']}): {summary}")
if not unassociated:
    lines.append("- None")

(root / "README.md").write_text("\n".join(lines) + "\n", encoding="utf-8")
PY

  echo "Release evidence written to $output_directory" >&2
  printf '%s\n' "$output_directory"
}

notes() {
  local tag="$1"
  local target_sha="$2"
  local previous_tag="$3"
  local output_file="$4"
  validate_tag "$tag"
  [[ "$(resolve_remote_commit "$target_sha")" == "$target_sha" ]] || {
    echo "Target '$target_sha' is not an exact remote commit." >&2
    exit 1
  }
  mkdir -p "$(dirname "$output_file")"

  if [[ "$previous_tag" == "-" ]]; then
    gh api --method POST "repos/$repository/releases/generate-notes" \
      -f tag_name="$tag" \
      -f target_commitish="$target_sha" \
      --jq .body \
      > "$output_file"
  else
    gh api --method POST "repos/$repository/releases/generate-notes" \
      -f tag_name="$tag" \
      -f target_commitish="$target_sha" \
      -f previous_tag_name="$previous_tag" \
      --jq .body \
      > "$output_file"
  fi
  echo "Generated release notes: $output_file"
}

revalidate() {
  local kind="$1"
  local target="$2"
  local reviewed_sha="$3"
  local reviewed_baseline="$4"
  local proposed_tag="$5"
  validate_kind "$kind"
  validate_tag "$proposed_tag"

  local current_sha
  local current_baseline
  current_sha="$(resolve_remote_commit "$target")"
  current_baseline="$(resolve_baseline "$kind")"
  [[ "$current_sha" == "$reviewed_sha" ]] || {
    echo "Release target moved from $reviewed_sha to $current_sha." >&2
    exit 1
  }
  [[ "$current_baseline" == "$reviewed_baseline" ]] || {
    echo "Release baseline moved from $reviewed_baseline to $current_baseline." >&2
    exit 1
  }
  if gh api "repos/$repository/git/ref/tags/$proposed_tag" >/dev/null 2>&1 \
    || gh release view "$proposed_tag" --repo "$repository" >/dev/null 2>&1; then
    echo "Proposed tag '$proposed_tag' already exists." >&2
    exit 1
  fi

  local ahead_by
  ahead_by="$(
    gh api "repos/$repository/compare/$reviewed_baseline...$reviewed_sha" \
      --jq .ahead_by
  )"
  [[ "$ahead_by" != "0" ]] || {
    echo "No changes remain after baseline $reviewed_baseline." >&2
    exit 1
  }

  echo "Release evidence is still current."
}

create_draft() {
  local kind="$1"
  local tag="$2"
  local target_sha="$3"
  local title="$4"
  local notes_file="$5"
  local confirmation_flag="${6:-}"
  local confirmation_tag="${7:-}"
  validate_kind "$kind"
  validate_tag "$tag"
  [[ "$confirmation_flag" == "--confirm" && "$confirmation_tag" == "$tag" ]] || {
    echo "Draft creation requires: --confirm $tag" >&2
    exit 2
  }
  [[ -f "$notes_file" ]] || {
    echo "Release notes file does not exist: $notes_file" >&2
    exit 1
  }
  [[ "$(resolve_remote_commit "$target_sha")" == "$target_sha" ]] || {
    echo "Target '$target_sha' is not an exact remote commit." >&2
    exit 1
  }
  if gh api "repos/$repository/git/ref/tags/$tag" >/dev/null 2>&1 \
    || gh release view "$tag" --repo "$repository" >/dev/null 2>&1; then
    echo "Tag or release '$tag' already exists." >&2
    exit 1
  fi

  local arguments=(
    "$tag"
    --repo "$repository"
    --target "$target_sha"
    --title "$title"
    --notes-file "$notes_file"
    --draft
  )
  if [[ "$kind" == "prerelease" ]]; then
    arguments+=(--prerelease)
  fi

  local release_url
  release_url="$(gh release create "${arguments[@]}")"

  # A draft release records only target_commitish; GitHub creates the tag at publication.
  # The publication workflow must be dispatched from the tag ref and validates it, so the
  # promised "draft release and its tag" requires creating the tag explicitly here. Found by
  # the first real run of this flow: verifying the tag before creating it failed every time.
  local draft_target
  draft_target="$(gh api "repos/$repository/releases" \
    --jq ".[] | select(.draft == true and .tag_name == \"$tag\") | .target_commitish")"
  [[ "$draft_target" == "$target_sha" ]] || {
    echo "Draft release targets '$draft_target' instead of $target_sha." >&2
    exit 1
  }
  gh api "repos/$repository/git/refs" \
    -f "ref=refs/tags/$tag" \
    -f "sha=$target_sha" >/dev/null
  local created_sha
  created_sha="$(resolve_remote_commit "$tag")"
  [[ "$created_sha" == "$target_sha" ]] || {
    echo "Draft release tag resolved to $created_sha instead of $target_sha." >&2
    exit 1
  }

  printf '%s\n' "$release_url"
}

dispatch_credential_preflight() {
  gh workflow run "$workflow" \
    --repo "$repository" \
    --ref main \
    -f operation=credential-preflight \
    -f publication=draft
}

dispatch_release() {
  local tag="$1"
  local publication="$2"
  local confirmation_flag="${3:-}"
  local confirmation_tag="${4:-}"
  validate_tag "$tag"
  [[ "$publication" == "draft" || "$publication" == "publish" ]] || {
    echo "Publication must be draft or publish." >&2
    exit 2
  }
  [[ "$confirmation_flag" == "--confirm" && "$confirmation_tag" == "$tag" ]] || {
    echo "Release dispatch requires: --confirm $tag" >&2
    exit 2
  }
  [[ "$(gh release view "$tag" --repo "$repository" --json isDraft --jq .isDraft)" == "true" ]] || {
    echo "Release '$tag' must exist and still be a draft." >&2
    exit 1
  }

  gh workflow run "$workflow" \
    --repo "$repository" \
    --ref "$tag" \
    -f operation=prepare-release \
    -f tag="$tag" \
    -f publication="$publication"
}

main() {
  require_tools
  local command="${1:-}"
  case "$command" in
    evidence)
      [[ "$#" -ge 2 && "$#" -le 4 ]] || {
        usage >&2
        exit 2
      }
      shift
      evidence "$@"
      ;;
    notes)
      [[ "$#" -eq 5 ]] || {
        usage >&2
        exit 2
      }
      shift
      notes "$@"
      ;;
    revalidate)
      [[ "$#" -eq 6 ]] || {
        usage >&2
        exit 2
      }
      shift
      revalidate "$@"
      ;;
    create-draft)
      [[ "$#" -eq 8 ]] || {
        usage >&2
        exit 2
      }
      shift
      create_draft "$@"
      ;;
    dispatch-credential-preflight)
      [[ "$#" -eq 1 ]] || {
        usage >&2
        exit 2
      }
      dispatch_credential_preflight
      ;;
    dispatch-release)
      [[ "$#" -eq 5 ]] || {
        usage >&2
        exit 2
      }
      shift
      dispatch_release "$@"
      ;;
    -h|--help|help)
      usage
      ;;
    *)
      usage >&2
      exit 2
      ;;
  esac
}

main "$@"
