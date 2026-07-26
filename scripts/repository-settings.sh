#!/usr/bin/env bash
#
# Check or apply Wrighty's non-secret GitHub repository settings.
#
# This script never reads or writes credential values. GitHub App creation,
# installation, and private-key storage remain interactive maintainer steps.

set -euo pipefail

readonly api_version="2026-03-10"
readonly repository="${WRIGHTY_REPOSITORY:-highbyte/wrighty}"
readonly release_environment="release"
readonly release_ruleset_name="release tags"
readonly app_client_id_variable="WRIGHTY_RELEASE_APP_CLIENT_ID"
readonly app_private_key_secret="WRIGHTY_RELEASE_APP_PRIVATE_KEY"
readonly app_rotation_variable="WRIGHTY_RELEASE_APP_KEY_ROTATE_BY"

usage() {
  cat <<EOF
Usage:
  scripts/repository-settings.sh [check]
  scripts/repository-settings.sh apply --confirm $repository
  scripts/repository-settings.sh record-key-rotation [YYYY-MM-DD] --confirm $repository
  scripts/repository-settings.sh set-ruleset-enforcement <name> <active|disabled> --confirm $repository

Commands:
  check                Read-only drift check (default).
  apply                Apply non-secret repository, Environment, and tag-ruleset settings.
  record-key-rotation  Record a one-year rotation deadline in the release Environment.
  set-ruleset-enforcement
                       Temporarily activate or disable one existing ruleset.

Set WRIGHTY_REPOSITORY to check a disposable repository. The --confirm value must
exactly match the resolved target for every mutating command.
EOF
}

api() {
  gh api \
    -H "Accept: application/vnd.github+json" \
    -H "X-GitHub-Api-Version: $api_version" \
    "$@"
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

require_confirmation() {
  local actual="${1:-}"
  local supplied="${2:-}"
  if [[ "$actual" != "--confirm" || "$supplied" != "$repository" ]]; then
    echo "Mutation requires: --confirm $repository" >&2
    exit 2
  fi
}

check_settings() {
  local problems=0
  local value

  pass() {
    echo "ok: $1"
  }

  drift() {
    echo "drift: $1" >&2
    problems=$((problems + 1))
  }

  echo "Checking GitHub settings for $repository"

  value="$(api "repos/$repository" --jq .allow_squash_merge)"
  [[ "$value" == "true" ]] \
    && pass "squash merging is enabled" \
    || drift "squash merging must be enabled"

  value="$(api "repos/$repository" --jq .allow_merge_commit)"
  [[ "$value" == "false" ]] \
    && pass "merge commits are disabled" \
    || drift "merge commits must be disabled"

  value="$(api "repos/$repository" --jq .allow_rebase_merge)"
  [[ "$value" == "false" ]] \
    && pass "rebase merging is disabled" \
    || drift "rebase merging must be disabled"

  value="$(api "repos/$repository" --jq .delete_branch_on_merge)"
  [[ "$value" == "true" ]] \
    && pass "merged branches are deleted automatically" \
    || drift "automatic deletion of merged branches must be enabled"

  value="$(
    api "repos/$repository/immutable-releases" --jq '.enabled // false' 2>/dev/null \
      || echo false
  )"
  if [[ "$value" == "true" ]]; then
    pass "immutable releases are enabled"
  else
    drift "immutable releases must be enabled"
  fi

  local ruleset_ids
  ruleset_ids="$(
    api --paginate "repos/$repository/rulesets" \
      --jq ".[] | select(.name == \"$release_ruleset_name\" and .target == \"tag\") | .id"
  )"
  if [[ -z "$ruleset_ids" ]]; then
    drift "tag ruleset '$release_ruleset_name' is missing"
  elif [[ "$(printf '%s\n' "$ruleset_ids" | wc -l | tr -d ' ')" != "1" ]]; then
    drift "tag ruleset '$release_ruleset_name' is ambiguous"
  else
    local ruleset_id
    ruleset_id="$ruleset_ids"
    value="$(api "repos/$repository/rulesets/$ruleset_id" --jq .enforcement)"
    [[ "$value" == "active" ]] \
      && pass "tag ruleset '$release_ruleset_name' is active" \
      || drift "tag ruleset '$release_ruleset_name' must be active"

    value="$(
      api "repos/$repository/rulesets/$ruleset_id" \
        --jq '.conditions.ref_name.include | sort | join(",")'
    )"
    [[ "$value" == "refs/tags/v*" ]] \
      && pass "tag ruleset targets refs/tags/v*" \
      || drift "tag ruleset must target only refs/tags/v*"

    value="$(
      api "repos/$repository/rulesets/$ruleset_id" \
        --jq '[.rules[].type] | sort | join(",")'
    )"
    [[ "$value" == "deletion,update" ]] \
      && pass "release tags cannot be updated or deleted" \
      || drift "tag ruleset must contain only deletion and update restrictions"
  fi

  if api "repos/$repository/environments/$release_environment" >/dev/null 2>&1; then
    pass "release Environment exists"

    value="$(
      api "repos/$repository/environments/$release_environment" \
        --jq '.deployment_branch_policy | [.protected_branches, .custom_branch_policies] | @tsv'
    )"
    [[ "$value" == $'false\ttrue' ]] \
      && pass "release Environment uses selected branches and tags" \
      || drift "release Environment must use custom branch and tag policies"

    value="$(
      api "repos/$repository/environments/$release_environment/deployment-branch-policies" \
        --jq '.branch_policies | map(.name) | sort | join(",")'
    )"
    [[ "$value" == "main,v*" ]] \
      && pass "release Environment allows main and v* tags" \
      || drift "release Environment must allow only main and v* tags"

    value="$(
      gh variable list --repo "$repository" --env "$release_environment" \
        --json name,value \
        --jq ".[] | select(.name == \"$app_client_id_variable\") | .value"
    )"
    [[ -n "$value" ]] \
      && pass "GitHub App client ID variable is configured" \
      || drift "Environment variable $app_client_id_variable is missing"

    value="$(
      gh secret list --repo "$repository" --env "$release_environment" \
        --json name \
        --jq ".[] | select(.name == \"$app_private_key_secret\") | .name"
    )"
    [[ "$value" == "$app_private_key_secret" ]] \
      && pass "GitHub App private-key secret is configured" \
      || drift "Environment secret $app_private_key_secret is missing"

    value="$(
      gh variable list --repo "$repository" --env "$release_environment" \
        --json name,value \
        --jq ".[] | select(.name == \"$app_rotation_variable\") | .value"
    )"
    if [[ -z "$value" ]]; then
      drift "Environment variable $app_rotation_variable is missing"
    elif python3 - "$value" <<'PY'
from datetime import date
import sys

try:
    deadline = date.fromisoformat(sys.argv[1])
except ValueError:
    raise SystemExit(1)
raise SystemExit(0 if deadline > date.today() else 1)
PY
    then
      pass "GitHub App private-key rotation deadline is in the future"
    else
      drift "GitHub App private-key rotation deadline is invalid, due, or overdue"
    fi
  else
    drift "release Environment is missing"
  fi

  if ((problems > 0)); then
    echo "$problems repository setting problem(s) found." >&2
    return 1
  fi

  echo "Repository settings match the documented Wrighty maintenance model."
}

apply_settings() {
  cat <<EOF
Applying this non-secret configuration to $repository:
  - squash-only merging and automatic merged-branch deletion
  - immutable releases
  - release Environment restricted to main and v* tags
  - active release-tags ruleset blocking updates and deletion
EOF

  api --method PATCH "repos/$repository" \
    -F allow_squash_merge=true \
    -F allow_merge_commit=false \
    -F allow_rebase_merge=false \
    -F delete_branch_on_merge=true \
    >/dev/null

  api --method PUT "repos/$repository/immutable-releases" >/dev/null

  api --method PUT "repos/$repository/environments/$release_environment" \
    --input - >/dev/null <<'JSON'
{
  "wait_timer": 0,
  "prevent_self_review": false,
  "reviewers": [],
  "deployment_branch_policy": {
    "protected_branches": false,
    "custom_branch_policies": true
  }
}
JSON

  local policies
  policies="$(
    api "repos/$repository/environments/$release_environment/deployment-branch-policies" \
      --jq '.branch_policies[] | [.id, .name, (.type // "")] | @tsv'
  )"
  local has_main=false
  local has_release_tags=false
  if [[ -n "$policies" ]]; then
    while IFS=$'\t' read -r policy_id policy_name policy_type; do
      if [[ "$policy_name" == "main" && -z "$policy_type" ]] \
        || [[ "$policy_name" == "main" && "$policy_type" == "branch" ]]; then
        has_main=true
      elif [[ "$policy_name" == "v*" && -z "$policy_type" ]] \
        || [[ "$policy_name" == "v*" && "$policy_type" == "tag" ]]; then
        has_release_tags=true
      else
        api --method DELETE \
          "repos/$repository/environments/$release_environment/deployment-branch-policies/$policy_id" \
          >/dev/null
      fi
    done <<<"$policies"
  fi

  if [[ "$has_main" != "true" ]]; then
    api --method POST \
      "repos/$repository/environments/$release_environment/deployment-branch-policies" \
      -f name=main \
      -f type=branch \
      >/dev/null
  fi
  if [[ "$has_release_tags" != "true" ]]; then
    api --method POST \
      "repos/$repository/environments/$release_environment/deployment-branch-policies" \
      -f name='v*' \
      -f type=tag \
      >/dev/null
  fi

  local ruleset_ids
  ruleset_ids="$(
    api --paginate "repos/$repository/rulesets" \
      --jq ".[] | select(.name == \"$release_ruleset_name\" and .target == \"tag\") | .id"
  )"
  if [[ "$(printf '%s\n' "$ruleset_ids" | sed '/^$/d' | wc -l | tr -d ' ')" -gt 1 ]]; then
    echo "Refusing to update ambiguous tag rulesets named '$release_ruleset_name'." >&2
    exit 1
  fi

  local ruleset_method=POST
  local ruleset_endpoint="repos/$repository/rulesets"
  if [[ -n "$ruleset_ids" ]]; then
    ruleset_method=PUT
    ruleset_endpoint="repos/$repository/rulesets/$ruleset_ids"
  fi

  api --method "$ruleset_method" "$ruleset_endpoint" --input - >/dev/null <<'JSON'
{
  "name": "release tags",
  "target": "tag",
  "enforcement": "active",
  "bypass_actors": [],
  "conditions": {
    "ref_name": {
      "include": ["refs/tags/v*"],
      "exclude": []
    }
  },
  "rules": [
    {"type": "deletion"},
    {"type": "update"}
  ]
}
JSON

  echo "Applied non-secret settings."
  echo "Complete the GitHub App installation and Environment variable/secret setup, then run:"
  echo "  scripts/repository-settings.sh check"
}

record_key_rotation() {
  local rotation_date="${1:-}"
  shift || true
  require_confirmation "${1:-}" "${2:-}"

  if [[ -z "$rotation_date" ]]; then
    rotation_date="$(date -u +%F)"
  fi

  local rotate_by
  rotate_by="$(
    python3 - "$rotation_date" <<'PY'
from datetime import date
import sys

rotated = date.fromisoformat(sys.argv[1])
try:
    deadline = rotated.replace(year=rotated.year + 1)
except ValueError:
    deadline = rotated.replace(year=rotated.year + 1, day=28)
print(deadline.isoformat())
PY
  )"

  gh variable set "$app_rotation_variable" \
    --repo "$repository" \
    --env "$release_environment" \
    --body "$rotate_by"
  echo "Recorded GitHub App private-key rotation deadline: $rotate_by"
}

set_ruleset_enforcement() {
  local ruleset_name="$1"
  local enforcement="$2"
  local confirmation_flag="${3:-}"
  local confirmation_repository="${4:-}"
  [[ "$enforcement" == "active" || "$enforcement" == "disabled" ]] || {
    echo "Ruleset enforcement must be active or disabled." >&2
    exit 2
  }
  require_confirmation "$confirmation_flag" "$confirmation_repository"

  local ruleset_ids
  ruleset_ids="$(
    api --paginate "repos/$repository/rulesets" \
      --jq ".[] | select(.name == \"$ruleset_name\") | .id"
  )"
  [[ -n "$ruleset_ids" ]] || {
    echo "Ruleset '$ruleset_name' was not found." >&2
    exit 1
  }
  [[ "$(printf '%s\n' "$ruleset_ids" | wc -l | tr -d ' ')" == "1" ]] || {
    echo "Ruleset '$ruleset_name' is ambiguous." >&2
    exit 1
  }

  echo "Setting ruleset '$ruleset_name' enforcement to '$enforcement' in $repository."
  api "repos/$repository/rulesets/$ruleset_ids" \
    | python3 -c '
import json
import sys

ruleset = json.load(sys.stdin)
payload = {
    key: ruleset[key]
    for key in (
        "name",
        "target",
        "bypass_actors",
        "conditions",
        "rules",
    )
}
payload["enforcement"] = sys.argv[1]
json.dump(payload, sys.stdout)
' "$enforcement" \
    | api --method PUT "repos/$repository/rulesets/$ruleset_ids" --input - >/dev/null
}

main() {
  require_tools

  local command="${1:-check}"
  case "$command" in
    check)
      [[ "$#" -eq 1 || "$#" -eq 0 ]] || {
        usage >&2
        exit 2
      }
      check_settings
      ;;
    apply)
      shift
      require_confirmation "${1:-}" "${2:-}"
      [[ "$#" -eq 2 ]] || {
        usage >&2
        exit 2
      }
      apply_settings
      ;;
    record-key-rotation)
      shift
      if [[ "${1:-}" == "--confirm" ]]; then
        record_key_rotation "" "$@"
      else
        local rotation_date="${1:-}"
        [[ -n "$rotation_date" ]] || {
          usage >&2
          exit 2
        }
        shift
        record_key_rotation "$rotation_date" "$@"
      fi
      ;;
    set-ruleset-enforcement)
      [[ "$#" -eq 5 ]] || {
        usage >&2
        exit 2
      }
      shift
      set_ruleset_enforcement "$@"
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
