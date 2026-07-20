#!/usr/bin/env bash

set -euo pipefail

SCRIPT_DIR=$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)
REPO_ROOT=$(cd "$SCRIPT_DIR/.." && pwd)

RECREATE=false
REPOSITORY=""
PROJECT_OWNER=""
PROJECT_TITLE="Wrighty MVP"
ISSUE_TITLE="[MVP test] Claim protocol fixture"
FIXTURE_LABEL="wrighty-fixture"
CONFIG_PATH="$REPO_ROOT/.wrighty.integration-fixture.json"

usage() {
    printf '%s\n' \
        "Usage: scripts/setup-github-integration-fixture.sh [options]" \
        "" \
        "Create or reuse the disposable GitHub Project and issue used for integration tests." \
        "" \
        "Options:" \
        "  --recreate               Delete matching fixture projects/issues, then recreate them." \
        "  --repo OWNER/REPO        Repository to use; defaults to the current gh repository." \
        "  --owner LOGIN            Project owner; defaults to the repository owner." \
        "  --project-title TITLE    Exact fixture Project title." \
        "  --issue-title TITLE      Exact fixture issue title." \
        "  -h, --help               Show this help." \
        "" \
        "WARNING: --recreate permanently deletes matching Projects, their repository issues," \
        "fixture-labeled issues, and all associated comments. It also rewrites" \
        ".wrighty.integration-fixture.json with the new Project number."
}

die() {
    printf 'error: %s\n' "$*" >&2
    exit 1
}

require_command() {
    command -v "$1" >/dev/null 2>&1 || die "required command '$1' was not found"
}

validate_title() {
    case "$1" in
        *$'\n'*|*$'\r'*|*$'\t'*) die "fixture titles cannot contain tabs or line breaks" ;;
        *) ;;
    esac
}

while (($# > 0)); do
    case "$1" in
        --recreate)
            RECREATE=true
            shift
            ;;
        --repo)
            (($# >= 2)) || die "--repo requires OWNER/REPO"
            REPOSITORY=$2
            shift 2
            ;;
        --owner)
            (($# >= 2)) || die "--owner requires a login"
            PROJECT_OWNER=$2
            shift 2
            ;;
        --project-title)
            (($# >= 2)) || die "--project-title requires a title"
            PROJECT_TITLE=$2
            shift 2
            ;;
        --issue-title)
            (($# >= 2)) || die "--issue-title requires a title"
            ISSUE_TITLE=$2
            shift 2
            ;;
        -h|--help)
            usage
            exit 0
            ;;
        *)
            die "unknown option '$1'"
            ;;
    esac
done

require_command gh
require_command dotnet

validate_title "$PROJECT_TITLE"
validate_title "$ISSUE_TITLE"

if [[ -z "$REPOSITORY" ]]; then
    REPOSITORY=$(gh repo view --json nameWithOwner --jq .nameWithOwner)
fi

[[ "$REPOSITORY" =~ ^[A-Za-z0-9_.-]+/[A-Za-z0-9_.-]+$ ]] ||
    die "repository must use OWNER/REPO format"
if [[ -z "$PROJECT_OWNER" ]]; then
    PROJECT_OWNER=${REPOSITORY%%/*}
fi

gh auth status >/dev/null

find_projects() {
    gh project list --owner "$PROJECT_OWNER" --limit 100 --format json \
        --template '{{range .projects}}{{printf "%v\t%s\n" .number .title}}{{end}}' |
        while IFS=$'\t' read -r number title; do
            if [[ "$title" == "$PROJECT_TITLE" ]]; then
                printf '%s\n' "$number"
            fi
        done
}

find_issues_by_title() {
    gh issue list --repo "$REPOSITORY" --state all --limit 1000 --json number,title \
        --template '{{range .}}{{printf "%v\t%s\n" .number .title}}{{end}}' |
        while IFS=$'\t' read -r number title; do
            if [[ "$title" == "$ISSUE_TITLE" ]]; then
                printf '%s\n' "$number"
            fi
        done
}

find_labeled_issues() {
    gh issue list --repo "$REPOSITORY" --state all --limit 1000 \
        --label "$FIXTURE_LABEL" --json number --jq '.[].number' 2>/dev/null || true
}

append_unique_issue() {
    local candidate=$1
    local existing
    for existing in "${ISSUES_TO_DELETE[@]-}"; do
        if [[ "$existing" == "$candidate" ]]; then
            return
        fi
    done
    ISSUES_TO_DELETE+=("$candidate")
}

ISSUES_TO_DELETE=()
if [[ "$RECREATE" == true ]]; then
    printf 'Recreating disposable GitHub integration fixture...\n'

    while IFS= read -r project_number; do
        [[ -n "$project_number" ]] || continue
        while IFS= read -r issue_number; do
            [[ -n "$issue_number" ]] && append_unique_issue "$issue_number"
        done < <(gh project item-list "$project_number" \
            --owner "$PROJECT_OWNER" \
            --limit 1000 \
            --format json \
            --jq ".items[] | select(.content.type == \"Issue\" and .content.repository == \"$REPOSITORY\") | .content.number")
        printf 'Deleting Project #%s (%s)...\n' "$project_number" "$PROJECT_TITLE"
        gh project delete "$project_number" --owner "$PROJECT_OWNER" >/dev/null
    done < <(find_projects)

    while IFS= read -r issue_number; do
        [[ -n "$issue_number" ]] && append_unique_issue "$issue_number"
    done < <(find_issues_by_title)
    while IFS= read -r issue_number; do
        [[ -n "$issue_number" ]] && append_unique_issue "$issue_number"
    done < <(find_labeled_issues)

    for issue_number in "${ISSUES_TO_DELETE[@]-}"; do
        [[ -n "$issue_number" ]] || continue
        printf 'Deleting issue #%s and its comments...\n' "$issue_number"
        gh issue delete "$issue_number" --repo "$REPOSITORY" --yes
    done
fi

PROJECT_MATCHES=()
while IFS= read -r project_number; do
    [[ -n "$project_number" ]] && PROJECT_MATCHES+=("$project_number")
done < <(find_projects)

if ((${#PROJECT_MATCHES[@]} > 1)); then
    die "multiple Projects are named '$PROJECT_TITLE'; rerun with --recreate or remove duplicates"
fi

if ((${#PROJECT_MATCHES[@]} == 0)); then
    printf 'Creating Project %s...\n' "$PROJECT_TITLE"
    PROJECT_NUMBER=$(gh project create \
        --owner "$PROJECT_OWNER" \
        --title "$PROJECT_TITLE" \
        --format json \
        --jq .number)
else
    PROJECT_NUMBER=${PROJECT_MATCHES[0]}
    printf 'Reusing Project #%s (%s).\n' "$PROJECT_NUMBER" "$PROJECT_TITLE"
fi

field_count() {
    local field_name=$1
    gh project field-list "$PROJECT_NUMBER" --owner "$PROJECT_OWNER" --limit 100 \
        --format json --jq ".fields | map(select(.name == \"$field_name\")) | length"
}

field_type() {
    local field_name=$1
    gh project field-list "$PROJECT_NUMBER" --owner "$PROJECT_OWNER" --limit 100 \
        --format json --jq ".fields[] | select(.name == \"$field_name\") | .type"
}

ensure_single_select_field() {
    local field_name=$1
    local options=$2
    local count
    count=$(field_count "$field_name")
    if [[ "$count" == "0" ]]; then
        printf 'Creating %s field...\n' "$field_name"
        gh project field-create "$PROJECT_NUMBER" \
            --owner "$PROJECT_OWNER" \
            --name "$field_name" \
            --data-type SINGLE_SELECT \
            --single-select-options "$options" >/dev/null
        return
    fi

    [[ "$count" == "1" ]] || die "Project contains duplicate '$field_name' fields"
    [[ "$(field_type "$field_name")" == "ProjectV2SingleSelectField" ]] ||
        die "Project field '$field_name' has the wrong type; rerun with --recreate"

    local required
    local available
    available=$(gh project field-list "$PROJECT_NUMBER" --owner "$PROJECT_OWNER" --limit 100 \
        --format json --jq ".fields[] | select(.name == \"$field_name\") | .options[].name")
    IFS=',' read -r -a required_options <<< "$options"
    for required in "${required_options[@]}"; do
        if ! printf '%s\n' "$available" | grep -Fxq "$required"; then
            die "Project field '$field_name' is missing option '$required'; rerun with --recreate"
        fi
    done
}

ensure_single_select_field "Status" "Todo,In Progress,Done"
ensure_single_select_field "Priority" "P0,P1,P2,P3"

CONFIG_TEMP="$CONFIG_PATH.tmp.$$"
trap 'rm -f "$CONFIG_TEMP"' EXIT
printf '%s\n' \
    '{' \
    '  "backend": "github",' \
    '  "defaultPickFrom": "Todo",' \
    '  "defaultPickTo": "In Progress",' \
    '  "defaultFinishTo": "Done",' \
    '  "leaseMinutes": 60,' \
    '  "archive": { "onStatuses": [] },' \
    '  "github": {' \
    "    \"repository\": \"$REPOSITORY\"," \
    "    \"projectOwner\": \"$PROJECT_OWNER\"," \
    "    \"projectNumber\": $PROJECT_NUMBER," \
    '    "linkRepository": true,' \
    '    "statusField": "Status",' \
    '    "priorityField": "Priority",' \
    '    "agentTypeField": "Current agent type",' \
    '    "sessionIdField": "Current session ID",' \
    '    "creationAttemptIdField": "Creation attempt ID",' \
    '    "claimHistoryLimit": 10,' \
    '    "gitHubHost": "github.com"' \
    '  }' \
    '}' > "$CONFIG_TEMP"
mv "$CONFIG_TEMP" "$CONFIG_PATH"

printf 'Linking repository and initializing Wrighty-managed Project fields...\n'
dotnet run --project "$REPO_ROOT/src/Highbyte.Wrighty.Cli" -- \
    init --config "$CONFIG_PATH" --create-view >/dev/null

gh label create "$FIXTURE_LABEL" \
    --repo "$REPOSITORY" \
    --description "Disposable Wrighty integration fixture" \
    --color "BFD4F2" \
    --force >/dev/null

ISSUE_MATCHES=()
while IFS= read -r issue_number; do
    [[ -n "$issue_number" ]] && ISSUE_MATCHES+=("$issue_number")
done < <(find_issues_by_title)

if ((${#ISSUE_MATCHES[@]} > 1)); then
    die "multiple issues are titled '$ISSUE_TITLE'; rerun with --recreate or remove duplicates"
fi

if ((${#ISSUE_MATCHES[@]} == 0)); then
    printf 'Creating fixture issue...\n'
    ISSUE_URL=$(gh issue create \
        --repo "$REPOSITORY" \
        --title "$ISSUE_TITLE" \
        --body "Disposable live-validation fixture for Wrighty. The setup script may delete this issue and all of its comments." \
        --label "$FIXTURE_LABEL")
    ISSUE_NUMBER=${ISSUE_URL##*/}
else
    ISSUE_NUMBER=${ISSUE_MATCHES[0]}
    ISSUE_URL=$(gh issue view "$ISSUE_NUMBER" --repo "$REPOSITORY" --json url --jq .url)
    gh issue edit "$ISSUE_NUMBER" --repo "$REPOSITORY" --add-label "$FIXTURE_LABEL" >/dev/null
    printf 'Reusing fixture issue #%s.\n' "$ISSUE_NUMBER"
fi

PROJECT_ID=$(gh project view "$PROJECT_NUMBER" --owner "$PROJECT_OWNER" --format json --jq .id)
ITEM_ID=$(gh project item-add "$PROJECT_NUMBER" \
    --owner "$PROJECT_OWNER" \
    --url "$ISSUE_URL" \
    --format json \
    --jq .id)

STATUS_FIELD_ID=$(gh project field-list "$PROJECT_NUMBER" --owner "$PROJECT_OWNER" --limit 100 \
    --format json --jq '.fields[] | select(.name == "Status") | .id')
TODO_OPTION_ID=$(gh project field-list "$PROJECT_NUMBER" --owner "$PROJECT_OWNER" --limit 100 \
    --format json --jq '.fields[] | select(.name == "Status") | .options[] | select(.name == "Todo") | .id')
PRIORITY_FIELD_ID=$(gh project field-list "$PROJECT_NUMBER" --owner "$PROJECT_OWNER" --limit 100 \
    --format json --jq '.fields[] | select(.name == "Priority") | .id')
P1_OPTION_ID=$(gh project field-list "$PROJECT_NUMBER" --owner "$PROJECT_OWNER" --limit 100 \
    --format json --jq '.fields[] | select(.name == "Priority") | .options[] | select(.name == "P1") | .id')

gh project item-edit \
    --id "$ITEM_ID" \
    --project-id "$PROJECT_ID" \
    --field-id "$STATUS_FIELD_ID" \
    --single-select-option-id "$TODO_OPTION_ID" >/dev/null
gh project item-edit \
    --id "$ITEM_ID" \
    --project-id "$PROJECT_ID" \
    --field-id "$PRIORITY_FIELD_ID" \
    --single-select-option-id "$P1_OPTION_ID" >/dev/null

for context_field in "Current agent type" "Current session ID" "Creation attempt ID"; do
    context_field_id=$(gh project field-list "$PROJECT_NUMBER" --owner "$PROJECT_OWNER" --limit 100 \
        --format json --jq ".fields[] | select(.name == \"$context_field\") | .id")
    gh project item-edit \
        --id "$ITEM_ID" \
        --project-id "$PROJECT_ID" \
        --field-id "$context_field_id" \
        --clear >/dev/null
done

dotnet run --project "$REPO_ROOT/src/Highbyte.Wrighty.Cli" -- \
    init --config "$CONFIG_PATH" --check >/dev/null

PROJECT_URL=$(gh project view "$PROJECT_NUMBER" --owner "$PROJECT_OWNER" --format json --jq .url)
printf '%s\n' \
    "GitHub integration fixture is ready:" \
    "  Project: $PROJECT_URL" \
    "  Issue:   $ISSUE_URL" \
    "  Config:  $CONFIG_PATH"
