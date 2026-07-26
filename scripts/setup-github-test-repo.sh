#!/usr/bin/env bash

set -euo pipefail

SCRIPT_DIR=$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)
REPO_ROOT=$(cd "$SCRIPT_DIR/.." && pwd)
# shellcheck source=scripts/ensure-github-test-repo.sh
source "$SCRIPT_DIR/ensure-github-test-repo.sh"

RESET=false
RECREATE=false
ISSUE_FORMS=false
SOURCE_REPOSITORY=""
REPOSITORY=""
PROJECT_OWNER=""
PROJECT_TITLE="Wrighty integration fixture"
ISSUE_TITLE="[integration fixture] Claim protocol"
FIXTURE_LABEL="wrighty-fixture"
CONFIG_PATH="$REPO_ROOT/.wrighty.integration-fixture.json"

usage() {
    printf '%s\n' \
        "Usage: scripts/setup-github-test-repo.sh [options]" \
        "" \
        "Provision Wrighty's shared private <owner>/<repo>-test repository and its" \
        "claim-fencing integration fixture." \
        "" \
        "Options:" \
        "  --source-repo OWNER/REPO Source repository used to derive OWNER/REPO-test;" \
        "                           defaults to the current gh repository." \
        "  --repo OWNER/REPO-test   Explicit private test repository target." \
        "  --owner LOGIN            Project owner; defaults to the repository owner." \
        "  --project-title TITLE    Exact fixture Project title." \
        "  --issue-title TITLE      Exact fixture issue title." \
        "  --config PATH            Generated Wrighty fixture configuration path." \
        "  --reset                  Delete only this fixture's labelled issues and remove" \
        "                           its Project items, then reprovision it." \
        "  --recreate               Delete the entire test repository and every Project" \
        "                           linked to it, then rebuild the fixture." \
        "  --issue-forms            Publish Wrighty's managed issue forms to the test repo." \
        "  -h, --help               Show this help." \
        "" \
        "Normal setup and --reset never delete the repository or Project. --recreate is a" \
        "destructive full rebuild and requires the active gh token to have delete_repo." \
        "Every target must be private, writable, and end in -test."
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
        --reset)
            RESET=true
            shift
            ;;
        --source-repo)
            (($# >= 2)) || die "--source-repo requires OWNER/REPO"
            SOURCE_REPOSITORY=$2
            shift 2
            ;;
        --repo)
            (($# >= 2)) || die "--repo requires OWNER/REPO-test"
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
        --config)
            (($# >= 2)) || die "--config requires a path"
            CONFIG_PATH=$2
            shift 2
            ;;
        --issue-forms)
            ISSUE_FORMS=true
            shift
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
require_command jq
require_command git

[[ "$RESET" == false || "$RECREATE" == false ]] ||
    die "--reset and --recreate cannot be used together"

validate_title "$PROJECT_TITLE"
validate_title "$ISSUE_TITLE"

gh auth status >/dev/null

if [[ -n "$REPOSITORY" ]]; then
    [[ "$REPOSITORY" =~ ^[A-Za-z0-9_.-]+/[A-Za-z0-9_.-]+-test$ ]] ||
        die "--repo must use OWNER/REPO-test format"
    [[ -z "$SOURCE_REPOSITORY" ]] ||
        die "--source-repo and --repo cannot be used together"
else
    SOURCE_REPOSITORY=$(_egtr_resolve_source "$SOURCE_REPOSITORY") ||
        die "could not resolve the source repository"
    REPOSITORY=$(github_test_repo_name "$SOURCE_REPOSITORY") ||
        die "could not derive the test repository"
fi

if [[ -z "$PROJECT_OWNER" ]]; then
    PROJECT_OWNER=${REPOSITORY%%/*}
fi
[[ "$PROJECT_OWNER" == "${REPOSITORY%%/*}" ]] ||
    die "Project owner '$PROJECT_OWNER' must match test repository owner '${REPOSITORY%%/*}'"

CONFIG_DIRECTORY=$(dirname "$CONFIG_PATH")
[[ -d "$CONFIG_DIRECTORY" ]] || die "the parent directory for --config must exist"
CONFIG_PATH="$(cd "$CONFIG_DIRECTORY" && pwd)/$(basename "$CONFIG_PATH")"

require_delete_repo_scope() {
    local scopes
    scopes=$(gh auth status \
        --active \
        --hostname github.com \
        --json hosts \
        --jq '.hosts["github.com"][] | select(.active) | .scopes')
    case ",${scopes// /}," in
        *,delete_repo,*) ;;
        *)
            die "the active gh token lacks delete_repo; run 'gh auth refresh -s delete_repo' before --recreate"
            ;;
    esac
}

find_linked_projects() {
    local owner=${REPOSITORY%%/*}
    local name=${REPOSITORY#*/}
    gh api graphql --paginate \
        -f query='
          query($owner: String!, $name: String!, $endCursor: String) {
            repository(owner: $owner, name: $name) {
              projectsV2(first: 100, after: $endCursor) {
                nodes { number title }
                pageInfo { hasNextPage endCursor }
              }
            }
          }' \
        -F owner="$owner" \
        -F name="$name" \
        --jq '.data.repository.projectsV2.nodes[] | [.number, .title] | @tsv'
}

recreate_test_repository() {
    require_delete_repo_scope

    if ! gh repo view "$REPOSITORY" --json nameWithOwner >/dev/null 2>&1; then
        printf 'Test repository %s does not exist; creating it instead of deleting anything.\n' \
            "$REPOSITORY"
        return
    fi

    assert_github_test_repo "$REPOSITORY" >/dev/null ||
        die "test repository validation failed before recreate"

    local linked_projects
    linked_projects=$(find_linked_projects) ||
        die "could not enumerate Projects linked to '$REPOSITORY'; nothing was deleted"

    local project_number project_title
    while IFS=$'\t' read -r project_number project_title; do
        [[ -n "$project_number" ]] || continue
        printf 'Deleting linked Project #%s (%s)...\n' "$project_number" "$project_title"
        gh project delete "$project_number" --owner "$PROJECT_OWNER" >/dev/null
    done <<< "$linked_projects"

    printf 'Deleting test repository %s...\n' "$REPOSITORY"
    gh repo delete "$REPOSITORY" --yes
    for attempt in {1..6}; do
        if ! gh repo view "$REPOSITORY" --json nameWithOwner >/dev/null 2>&1; then
            return
        fi
        [[ "$attempt" != "6" ]] ||
            die "GitHub still reports '$REPOSITORY' after deletion; rerun setup once deletion settles"
        sleep 2
    done
}

if [[ "$RECREATE" == true ]]; then
    printf 'Recreating the dedicated GitHub test repository and all linked fixtures.\n'
    recreate_test_repository
fi

REPOSITORY=$(ensure_github_test_repo "$REPOSITORY") ||
    die "could not provision the dedicated test repository"
REPOSITORY_ISSUES_ENABLED=$(gh repo view "$REPOSITORY" --json hasIssuesEnabled --jq .hasIssuesEnabled)
[[ "$REPOSITORY_ISSUES_ENABLED" == "true" ]] ||
    die "issues are disabled in test repository '$REPOSITORY'"

find_projects() {
    gh project list --owner "$PROJECT_OWNER" --limit 1000 --format json \
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
if [[ "$RESET" == true ]]; then
    printf 'Resetting only the claim-fencing integration fixture...\n'
    while IFS= read -r project_number; do
        [[ -n "$project_number" ]] || continue
        while IFS=$'\t' read -r item_id issue_number; do
            [[ -n "$item_id" ]] || continue
            [[ -n "$issue_number" ]] && append_unique_issue "$issue_number"
            printf 'Removing fixture Project item %s...\n' "$item_id"
            gh project item-delete "$project_number" \
                --owner "$PROJECT_OWNER" \
                --id "$item_id" >/dev/null
        done < <(gh project item-list "$project_number" \
            --owner "$PROJECT_OWNER" \
            --limit 1000 \
            --format json \
            --jq ".items[] | select(.content.type == \"Issue\" and .content.repository == \"$REPOSITORY\") | [.id, .content.number] | @tsv")
    done < <(find_projects)

    while IFS= read -r issue_number; do
        [[ -n "$issue_number" ]] && append_unique_issue "$issue_number"
    done < <(find_issues_by_title)
    while IFS= read -r issue_number; do
        [[ -n "$issue_number" ]] && append_unique_issue "$issue_number"
    done < <(find_labeled_issues)

    for issue_number in "${ISSUES_TO_DELETE[@]-}"; do
        [[ -n "$issue_number" ]] || continue
        printf 'Deleting fixture issue #%s and its comments...\n' "$issue_number"
        gh issue delete "$issue_number" --repo "$REPOSITORY" --yes
    done
fi

PROJECT_MATCHES=()
while IFS= read -r project_number; do
    [[ -n "$project_number" ]] && PROJECT_MATCHES+=("$project_number")
done < <(find_projects)

if ((${#PROJECT_MATCHES[@]} > 1)); then
    die "multiple Projects are named '$PROJECT_TITLE'; remove duplicates or use --recreate for a full rebuild"
fi

if ((${#PROJECT_MATCHES[@]} == 0)); then
    printf 'Creating private Project %s...\n' "$PROJECT_TITLE"
    PROJECT_NUMBER=$(gh project create \
        --owner "$PROJECT_OWNER" \
        --title "$PROJECT_TITLE" \
        --format json \
        --jq .number)
    gh project edit "$PROJECT_NUMBER" \
        --owner "$PROJECT_OWNER" \
        --visibility PRIVATE >/dev/null
else
    PROJECT_NUMBER=${PROJECT_MATCHES[0]}
    printf 'Reusing Project #%s (%s).\n' "$PROJECT_NUMBER" "$PROJECT_TITLE"
fi

PROJECT_PUBLIC=$(gh api graphql \
    -f query='query($owner: String!, $number: Int!) {
      repositoryOwner(login: $owner) {
        ... on User { projectV2(number: $number) { public } }
        ... on Organization { projectV2(number: $number) { public } }
      }
    }' \
    -F owner="$PROJECT_OWNER" \
    -F number="$PROJECT_NUMBER" \
    --jq '.data.repositoryOwner.projectV2.public')
[[ "$PROJECT_PUBLIC" == "false" ]] ||
    die "fixture Project #$PROJECT_NUMBER is not private"

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
FORM_WORK_ROOT=""
cleanup_local_files() {
    rm -f "$CONFIG_TEMP"
    if [[ -n "$FORM_WORK_ROOT" ]]; then
        case "$FORM_WORK_ROOT" in
            "${TMPDIR:-/tmp}"/wrighty-test-repo-forms.*)
                rm -rf "$FORM_WORK_ROOT"
                ;;
            *)
                printf 'warning: refusing to remove unexpected temporary path %s\n' \
                    "$FORM_WORK_ROOT" >&2
                ;;
        esac
    fi
}
trap cleanup_local_files EXIT
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
    '    "claimHistoryLimit": 10,' \
    '    "gitHubHost": "github.com"' \
    '  }' \
    '}' > "$CONFIG_TEMP"
mv "$CONFIG_TEMP" "$CONFIG_PATH"

printf 'Linking repository and initializing Wrighty-managed Project fields...\n'
dotnet run --project "$REPO_ROOT/src/Highbyte.Wrighty.Cli" -- \
    init --config "$CONFIG_PATH" --create-view --skip-issue-forms --yes >/dev/null

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
    die "multiple issues are titled '$ISSUE_TITLE'; rerun with --reset or remove duplicates"
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

for context_field in \
    "Wrighty dispatch - state" \
    "Wrighty dispatch - not before" \
    "Wrighty dispatch - agent" \
    "Wrighty dispatch - detail" \
    "Wrighty claim - agent" \
    "Wrighty claim - claimant type" \
    "Wrighty claim - claimant" \
    "Wrighty claim - session ID" \
    "Wrighty claim - workspace path" \
    "Wrighty creation - attempt ID"; do
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

if [[ "$ISSUE_FORMS" == true ]]; then
    printf 'Publishing Wrighty-managed issue forms to %s...\n' "$REPOSITORY"
    FORM_WORK_ROOT=$(mktemp -d "${TMPDIR:-/tmp}/wrighty-test-repo-forms.XXXXXX")
    FORM_CLONE="$FORM_WORK_ROOT/repository"
    gh repo clone "$REPOSITORY" "$FORM_CLONE" -- --quiet
    if ! git -C "$FORM_CLONE" rev-parse --verify HEAD >/dev/null 2>&1; then
        git -C "$FORM_CLONE" symbolic-ref HEAD refs/heads/main
    fi
    (
        cd "$FORM_CLONE"
        dotnet run --project "$REPO_ROOT/src/Highbyte.Wrighty.Cli" -- \
            init \
            --config "$CONFIG_PATH" \
            --create-view \
            --publish-issue-forms \
            --yes >/dev/null
    )
fi

PROJECT_URL=$(gh project view "$PROJECT_NUMBER" --owner "$PROJECT_OWNER" --format json --jq .url)
printf '%s\n' \
    "GitHub integration fixture is ready:" \
    "  Project: $PROJECT_URL" \
    "  Issue:   $ISSUE_URL" \
    "  Config:  $CONFIG_PATH"
