from __future__ import annotations

import json
import subprocess
import sys
import tempfile
import unittest
from pathlib import Path


REPOSITORY_ROOT = Path(__file__).resolve().parents[2]
SCRIPT = REPOSITORY_ROOT / "scripts" / "update-package-manager-manifests.py"
HASHES = {
    "osx-arm64": "A" * 64,
    "linux-x64": "b" * 64,
    "linux-arm64": "c" * 64,
    "win-x64": "d" * 64,
    "win-arm64": "e" * 64,
}


class PackageManagerManifestTests(unittest.TestCase):
    def run_generator(
        self,
        root: Path,
        *,
        version: str = "0.1.0-alpha",
        tag: str = "v0.1.0-alpha",
        hashes: dict[str, str] | None = None,
    ) -> subprocess.CompletedProcess[str]:
        selected_hashes = hashes or HASHES
        arguments = [
            sys.executable,
            str(SCRIPT),
            "--version",
            version,
            "--tag",
            tag,
            "--homebrew-directory",
            str(root / "homebrew"),
            "--scoop-directory",
            str(root / "scoop"),
        ]
        for runtime, value in selected_hashes.items():
            arguments.extend([f"--{runtime}-hash", value])

        return subprocess.run(
            arguments,
            cwd=REPOSITORY_ROOT,
            capture_output=True,
            text=True,
            check=False,
        )

    def test_generates_alpha_homebrew_formula_and_scoop_manifest(self) -> None:
        with tempfile.TemporaryDirectory() as temporary_directory:
            root = Path(temporary_directory)

            result = self.run_generator(root)

            self.assertEqual(0, result.returncode, result.stderr)
            formula = (root / "homebrew" / "Formula" / "wrighty.rb").read_text()
            self.assertIn('version "0.1.0-alpha"', formula)
            self.assertIn(
                "releases/download/v0.1.0-alpha/wrighty-0.1.0-alpha-osx-arm64.zip",
                formula,
            )
            self.assertIn(f'sha256 "{"a" * 64}"', formula)
            self.assertIn('license "MIT"', formula)
            self.assertIn('libexec.install Dir["*"]', formula)
            self.assertIn('(bin/"wrighty").write <<~EOS', formula)
            self.assertIn('exec "#{libexec}/wrighty" "$@"', formula)

            manifest = json.loads(
                (root / "scoop" / "bucket" / "wrighty.json").read_text()
            )
            self.assertEqual("0.1.0-alpha", manifest["version"])
            self.assertEqual("MIT", manifest["license"])
            self.assertEqual("wrighty.exe", manifest["bin"])
            self.assertEqual(HASHES["win-x64"], manifest["architecture"]["64bit"]["hash"])
            self.assertEqual(
                "https://github.com/highbyte/wrighty/releases/download/"
                "v$version/wrighty-$version-win-arm64.zip",
                manifest["autoupdate"]["architecture"]["arm64"]["url"],
            )

    def test_unprefixed_tag_produces_unprefixed_autoupdate_urls(self) -> None:
        with tempfile.TemporaryDirectory() as temporary_directory:
            root = Path(temporary_directory)

            result = self.run_generator(root, tag="0.1.0-alpha")

            self.assertEqual(0, result.returncode, result.stderr)
            manifest = json.loads(
                (root / "scoop" / "bucket" / "wrighty.json").read_text()
            )
            self.assertIn(
                "download/$version/wrighty-$version-win-x64.zip",
                manifest["autoupdate"]["architecture"]["64bit"]["url"],
            )

    def test_rejects_invalid_version(self) -> None:
        with tempfile.TemporaryDirectory() as temporary_directory:
            result = self.run_generator(
                Path(temporary_directory), version="v0.1.0", tag="v0.1.0"
            )

            self.assertNotEqual(0, result.returncode)
            self.assertIn("semantic version without a leading v", result.stderr)

    def test_rejects_mismatched_tag(self) -> None:
        with tempfile.TemporaryDirectory() as temporary_directory:
            result = self.run_generator(Path(temporary_directory), tag="v0.1.1-alpha")

            self.assertNotEqual(0, result.returncode)
            self.assertIn("--tag must equal --version", result.stderr)

    def test_rejects_invalid_hash(self) -> None:
        with tempfile.TemporaryDirectory() as temporary_directory:
            hashes = HASHES | {"linux-x64": "not-a-sha256"}

            result = self.run_generator(Path(temporary_directory), hashes=hashes)

            self.assertNotEqual(0, result.returncode)
            self.assertIn("--linux-x64-hash must be a SHA-256 hash", result.stderr)


if __name__ == "__main__":
    unittest.main()
