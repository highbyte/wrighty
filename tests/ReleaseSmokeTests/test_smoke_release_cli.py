from __future__ import annotations

import importlib.util
from pathlib import Path
import unittest
from unittest.mock import patch


REPOSITORY_ROOT = Path(__file__).resolve().parents[2]
SCRIPT = REPOSITORY_ROOT / "scripts" / "smoke-release-cli.py"
SPEC = importlib.util.spec_from_file_location("smoke_release_cli", SCRIPT)
if SPEC is None or SPEC.loader is None:
    raise RuntimeError(f"Could not load {SCRIPT}")
SMOKE_RELEASE_CLI = importlib.util.module_from_spec(SPEC)
SPEC.loader.exec_module(SMOKE_RELEASE_CLI)


class ReleaseSmokeTests(unittest.TestCase):
    def test_archive_cleanup_tolerates_transient_windows_executable_locks(self) -> None:
        with (
            patch.object(SMOKE_RELEASE_CLI.os, "name", "nt"),
            patch.object(SMOKE_RELEASE_CLI.tempfile, "TemporaryDirectory") as temporary,
        ):
            SMOKE_RELEASE_CLI.archive_temporary_directory()

        temporary.assert_called_once_with(
            prefix="wrighty-release-archive-",
            ignore_cleanup_errors=True,
        )

    def test_archive_cleanup_remains_strict_on_other_platforms(self) -> None:
        with (
            patch.object(SMOKE_RELEASE_CLI.os, "name", "posix"),
            patch.object(SMOKE_RELEASE_CLI.tempfile, "TemporaryDirectory") as temporary,
        ):
            SMOKE_RELEASE_CLI.archive_temporary_directory()

        temporary.assert_called_once_with(
            prefix="wrighty-release-archive-",
            ignore_cleanup_errors=False,
        )


if __name__ == "__main__":
    unittest.main()
