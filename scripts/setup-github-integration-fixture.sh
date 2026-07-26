#!/usr/bin/env bash
#
# Compatibility entry point. The dedicated repository lifecycle and integration
# fixture now share scripts/setup-github-test-repo.sh.

set -euo pipefail

SCRIPT_DIR=$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)
exec "$SCRIPT_DIR/setup-github-test-repo.sh" "$@"
