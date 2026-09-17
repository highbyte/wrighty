from __future__ import annotations

from contextlib import redirect_stdout
import importlib.util
import io
import os
from pathlib import Path
import subprocess
import sys
import tempfile
import unittest
from unittest.mock import patch


SCRIPT = Path(__file__).resolve().parents[2] / "scripts" / "install-release-package.py"
SPEC = importlib.util.spec_from_file_location("install_release_package", SCRIPT)
assert SPEC is not None and SPEC.loader is not None
INSTALL = importlib.util.module_from_spec(SPEC)
SPEC.loader.exec_module(INSTALL)


class PackageInstallTests(unittest.TestCase):
    def setUp(self) -> None:
        temporary = tempfile.TemporaryDirectory()
        self.addCleanup(temporary.cleanup)
        self.root = Path(temporary.name)
        self.package = self.root / "package"
        self.cli = self.root / "wrighty"
        self.logs = self.root / "logs"
        self.summary = self.root / "summary.md"
        self.version = "0.19.0-alpha+" + "a" * 40
        self.command = ["package-manager", "install", "wrighty"]
        self.enterContext(patch.dict(os.environ, {"GITHUB_STEP_SUMMARY": str(self.summary)}))
        self.enterContext(redirect_stdout(io.StringIO()))
        self.sleep = self.enterContext(patch.object(INSTALL.time, "sleep"))

    def result(self, code: int, output: str) -> subprocess.CompletedProcess[str]:
        return subprocess.CompletedProcess(self.command, code, output)

    def complete_files(self) -> None:
        self.package.mkdir(exist_ok=True)
        self.cli.touch()

    def install(self) -> None:
        INSTALL.install("fixture", self.command, self.package, self.cli, self.version, self.logs)

    def test_broken_pipe_then_success_keeps_both_logs_and_reports_recovery(self) -> None:
        def execute(command):
            if command == [str(self.cli), "--version"]:
                return self.result(0, self.version + "\n")
            if not (self.logs / "fixture-attempt-1.log").exists():
                return self.result(1, "Broken pipe\n")
            self.complete_files()
            return self.result(0, "Installed\n")
        with patch.object(INSTALL, "run", side_effect=execute) as run:
            self.install()
        self.assertEqual(3, run.call_count)
        self.sleep.assert_called_once_with(10)
        self.assertIn("Broken pipe", (self.logs / "fixture-attempt-1.log").read_text())
        self.assertIn("Installed", (self.logs / "fixture-attempt-2.log").read_text())
        self.assertIn("verified on attempt 2/3", self.summary.read_text())

    def test_transient_failure_stops_after_three_attempts(self) -> None:
        with patch.object(INSTALL, "run", return_value=self.result(1, "HTTP 503 Service Unavailable")) as run:
            with self.assertRaisesRegex(RuntimeError, "persisted after 3 attempts"):
                self.install()
        self.assertEqual(3, run.call_count)
        self.assertEqual([10, 30], [call.args[0] for call in self.sleep.call_args_list])
        self.assertEqual(3, len(list(self.logs.glob("*.log"))))

    def test_common_download_failures_are_distinguished_from_permanent_errors(self) -> None:
        for message in ["curl: (28) Operation too slow", "Could not resolve host: github.com",
                        "The remote server returned an error: (503) Server Unavailable",
                        "Response status code does not indicate success: 502 (Bad Gateway)"]:
            with self.subTest(message=message):
                self.assertTrue(INSTALL.retryable(1, message))
        for message in ["The remote server returned an error: (403) Forbidden\nBroken pipe",
                        "SSL certificate verify failed\ncurl: (56) Failure",
                        "HTTP 429 Too Many Requests", "Hash check failed!\nconnection reset"]:
            with self.subTest(message=message):
                self.assertFalse(INSTALL.retryable(1, message))

    def test_integrity_permission_unknown_and_cancellation_fail_without_retry(self) -> None:
        for code, output in [(1, "SHA256 mismatch\nBroken pipe"), (1, "Permission denied\nconnection reset"),
                             (1, "HTTP 403 Forbidden"), (1, "Formula syntax error"), (130, "Broken pipe"),
                             (-15, "connection reset")]:
            with self.subTest(code=code, output=output):
                with patch.object(INSTALL, "run", return_value=self.result(code, output)) as run:
                    with self.assertRaisesRegex(RuntimeError, "non-retryable"):
                        self.install()
                run.assert_called_once_with(self.command)
        self.sleep.assert_not_called()

    def test_partial_install_is_not_retried_or_deleted(self) -> None:
        def execute(command):
            self.package.mkdir()
            return self.result(1, "Connection reset by peer")
        with patch.object(INSTALL, "run", side_effect=execute) as run:
            with self.assertRaisesRegex(RuntimeError, "Partial Wrighty installation"):
                self.install()
        self.assertTrue(self.package.exists())
        run.assert_called_once()
        self.sleep.assert_not_called()

    def test_completed_install_after_transient_failure_is_verified_without_reinstall(self) -> None:
        def execute(command):
            if command == self.command:
                self.complete_files()
                return self.result(1, "Broken pipe")
            return self.result(0, self.version)
        with patch.object(INSTALL, "run", side_effect=execute) as run:
            self.install()
        self.assertEqual(2, run.call_count)
        self.sleep.assert_not_called()
        self.assertIn("command failed", self.summary.read_text())
        self.assertIn("functional smoke test still required", self.summary.read_text())

    def test_wrong_version_or_commit_is_fatal_even_after_transient_failure(self) -> None:
        for version in ["0.18.0-alpha+" + "a" * 40, "0.19.0-alpha+" + "b" * 40]:
            with self.subTest(version=version):
                self.cli.unlink(missing_ok=True)
                if self.package.exists():
                    self.package.rmdir()
                def execute(command):
                    self.complete_files()
                    return self.result(1, "Broken pipe") if command == self.command else self.result(0, version)
                with patch.object(INSTALL, "run", side_effect=execute):
                    with self.assertRaisesRegex(RuntimeError, "failed version verification"):
                        self.install()
        self.sleep.assert_not_called()

    def test_success_exit_without_package_is_not_success(self) -> None:
        with patch.object(INSTALL, "run", return_value=self.result(0, "Already installed")):
            with self.assertRaisesRegex(RuntimeError, "Wrighty is missing"):
                self.install()
        self.sleep.assert_not_called()

    def test_preexisting_install_is_not_touched(self) -> None:
        self.package.mkdir()
        with patch.object(INSTALL, "run") as run:
            with self.assertRaisesRegex(RuntimeError, "clean disposable runner"):
                self.install()
        run.assert_not_called()

    def test_late_completion_is_checked_before_another_attempt(self) -> None:
        self.sleep.side_effect = lambda seconds: self.complete_files()
        with patch.object(INSTALL, "run", side_effect=[self.result(1, "Broken pipe"), self.result(0, self.version)]) as run:
            self.install()
        self.assertEqual(2, run.call_count)
        self.assertIn("completed during retry delay", self.summary.read_text())

    @unittest.skipIf(os.name == "nt", "POSIX executable fixture; Windows exercises the Scoop bridge")
    def test_homebrew_helper_entry_point_installs_and_verifies_executable(self) -> None:
        executable_directory = self.root / "bin"
        executable_directory.mkdir()
        brew = executable_directory / "brew"
        cli_program = f"#!{sys.executable}\nprint({self.version!r})\n"
        brew.write_text(
            f"#!{sys.executable}\n"
            "import os, pathlib, sys\n"
            "root = pathlib.Path(os.environ['PACKAGE_FIXTURE'])\n"
            "if sys.argv[1] == '--cellar': print(root / 'Cellar')\n"
            "elif sys.argv[1] == '--prefix': print(root)\n"
            "else:\n"
            " assert sys.argv[1:] == ['install', '--verbose', 'highbyte/tap/wrighty']\n"
            " (root / 'Cellar' / 'wrighty').mkdir(parents=True)\n"
            " cli = root / 'bin' / 'wrighty'\n"
            f" cli.write_text({cli_program!r})\n"
            " cli.chmod(0o755)\n"
            " print('Installed fixture')\n"
        )
        brew.chmod(0o755)
        environment = {**os.environ, "PACKAGE_FIXTURE": str(self.root),
                       "PATH": str(executable_directory) + os.pathsep + os.environ.get("PATH", "")}
        result = subprocess.run(
            [sys.executable, str(SCRIPT), "--manager", "homebrew", "--version", "0.19.0-alpha",
             "--source-sha", "a" * 40, "--log-directory", str(self.logs)],
            capture_output=True, text=True, env=environment,
        )
        self.assertEqual(0, result.returncode, result.stdout + result.stderr)
        self.assertIn("version verified on attempt 1/3", result.stdout)
        self.assertIn("Installed fixture", (self.logs / "homebrew-attempt-1.log").read_text())

    @unittest.skipUnless(os.name == "nt", "Windows native Scoop command bridge")
    def test_scoop_cmd_bridge_preserves_exit_and_does_not_interpret_path(self) -> None:
        directory = self.root / "scoop space & quote'"
        directory.mkdir()
        shim = directory / "scoop.cmd"
        shim.write_text('@echo off\necho Broken pipe\nexit /b 23\n')
        with patch.dict(os.environ, {"SCOOP_CMD": str(shim)}):
            result = INSTALL.run(INSTALL.install_command("scoop"))
        self.assertEqual(23, result.returncode)
        self.assertIn("Broken pipe", result.stdout)


if __name__ == "__main__":
    unittest.main()
