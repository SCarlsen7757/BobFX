import contextlib
import io
import json
from pathlib import Path
import re
import shlex
import subprocess
import tempfile
import unittest
from unittest.mock import patch

import yaml

import release

ROOT = Path(__file__).resolve().parents[2]
WORKFLOW = ROOT / ".github/workflows/build-and-release.yml"
DIGEST = "sha256:" + "1" * 64
AMD = "sha256:" + "2" * 64
ARM = "sha256:" + "3" * 64


def index():
    return {"digest": DIGEST, "manifests": [
        {"digest": child, "platform": {"os": "linux", "architecture": arch}}
        for arch, child in (("amd64", AMD), ("arm64", ARM))
    ] + [
        {"platform": {"os": "unknown", "architecture": "unknown"}, "annotations": {
            "vnd.docker.reference.type": "attestation-manifest", "vnd.docker.reference.digest": child}}
        for child in (AMD, ARM)
    ]}


class EvidenceTestCase(unittest.TestCase):
    """Gives each test its own evidence directory instead of the working directory's."""

    def setUp(self):
        directory = tempfile.TemporaryDirectory()
        self.addCleanup(directory.cleanup)
        self.evidence = Path(directory.name) / "release-evidence"

    def write_verification(self, text=None):
        self.evidence.mkdir(exist_ok=True)
        text = text or json.dumps({"version": "0.7.0", "indexDigest": DIGEST, "platforms": {"amd64": AMD, "arm64": ARM}})
        (self.evidence / "verification.json").write_text(text, encoding="utf-8")


class SmokeTests(EvidenceTestCase):
    def setUp(self):
        super().setUp()
        self.saved = {"version": 1, "animation": {"framesPerSecond": 30}}
        self.health = "healthy"
        sleep = patch.object(release.time, "sleep")
        self.sleep = sleep.start()
        self.addCleanup(sleep.stop)

    def command(self, *args):
        if args[1:3] == ("inspect", "--format"):
            return self.health
        if args[1] == "exec" and args[3:5] == ("id", "-u"):
            return "1654"
        if args[1] == "exec" and "curl" in args:
            if any(str(arg).endswith("/game/fullscreen") for arg in args):
                return '<script type="module" src="/ui/assets/index-test.js"></script>'
            if any("/ui/assets/" in str(arg) for arg in args):
                return ""
            if "--data-binary" in args:
                self.saved.update(json.loads(args[args.index("--data-binary") + 1]))
                return ""
            return json.dumps(self.saved)
        return ""

    def test_smoke_verifies_nonroot_settings_survive_recreation(self):
        with patch.object(release, "run", side_effect=self.command) as commands:
            release.smoke("image", "arm64", evidence=self.evidence)
        launches = [call.args for call in commands.call_args_list if call.args[:2] == ("docker", "run")]
        self.assertEqual(2, len(launches))
        self.assertEqual(launches[0], launches[1])
        self.assertIn("type=volume,source=bobfx-smoke-arm64-data,target=/app/data", launches[0])
        self.assertEqual(23, self.saved["animation"]["framesPerSecond"])
        self.assertTrue(json.loads((self.evidence / "persistence-arm64.json").read_text())["settingsSurvivedRecreation"])

    def test_requests_run_curl_inside_the_container_with_the_allowed_host(self):
        with patch.object(release, "run", return_value="") as command:
            release.exec_curl("name", "/api/installation")
            command.assert_called_once_with("docker", "exec", "name", "curl", "--fail", "--silent",
                                            "-H", "Host: localhost", "http://127.0.0.1:8080/api/installation")
        with patch.object(release, "run", return_value="") as command:
            release.exec_curl("name", "/api/installation", "-X", "PUT", data="{}")
            command.assert_called_once_with("docker", "exec", "name", "curl", "--fail", "--silent", "-X", "PUT",
                                            "-H", "Host: localhost", "-H", "Content-Type: application/json",
                                            "--data-binary", "{}", "http://127.0.0.1:8080/api/installation")

    def test_unhealthy_container_fails_and_is_removed_with_its_log(self):
        self.health = "starting"
        with patch.object(release, "run", side_effect=self.command) as commands:
            with self.assertRaisesRegex(ValueError, "failed to become healthy"):
                release.smoke("image", "amd64", evidence=self.evidence)
        self.assertEqual(release.HEALTH_ATTEMPTS, self.sleep.call_count)
        self.assertTrue((self.evidence / "smoke-amd64.log").exists())
        self.assertEqual([("docker", "rm", "-f", "bobfx-smoke-amd64"), ("docker", "volume", "rm", "bobfx-smoke-amd64-data")],
                         [call.args for call in commands.call_args_list[-2:]])


class ArtifactTests(EvidenceTestCase):
    def test_exact_platforms_and_attestations_are_required(self):
        self.assertEqual({"amd64": AMD, "arm64": ARM}, release.platforms(index()))
        for position in (0, 1, 2, 3):
            altered = index()
            altered["manifests"].pop(position)
            with self.assertRaises(ValueError):
                release.platforms(altered)
        duplicated = index()
        duplicated["manifests"].append(duplicated["manifests"][0])
        with self.assertRaises(ValueError):
            release.platforms(duplicated)

    def test_version_revision_and_platform_mismatches_fail(self):
        config = {"Os": "linux", "Architecture": "amd64", "Config": {"Labels": {
            "org.opencontainers.image.version": "0.7.0", "org.opencontainers.image.revision": "commit"}}}
        release.metadata(config, "amd64", "0.7.0", "commit")
        for args in (("arm64", "0.7.0", "commit"), ("amd64", "0.8.0", "commit"), ("amd64", "0.7.0", "other")):
            with self.assertRaises(ValueError):
                release.metadata(config, *args)

    def test_attestations_must_cover_both_platforms(self):
        release.attestation({"linux/amd64": {"SPDX": {"spdxVersion": "SPDX-2.3"}}}, "linux/amd64", "SPDX")
        with self.assertRaises(ValueError):
            release.attestation({"linux/amd64": {"SPDX": {"spdxVersion": "SPDX-2.3"}}}, "linux/arm64", "SPDX")

    def test_registry_errors_do_not_count_as_missing_tags(self):
        for message in ("unauthorized", "dial tcp: timeout", "registry unavailable"):
            with patch.object(release, "inspect", side_effect=subprocess.CalledProcessError(1, "docker", stderr=message)):
                with self.assertRaises(subprocess.CalledProcessError):
                    release.existing_digest("image:0.7.0")
        with patch.object(release, "inspect", side_effect=subprocess.CalledProcessError(1, "docker", stderr="manifest unknown")):
            self.assertEqual("", release.existing_digest("image:0.7.0"))

    def test_promotion_copies_exact_index_and_matching_rerun_does_not_rebuild(self):
        self.write_verification()
        for existing, copies in ((["", DIGEST], 1), ([DIGEST, DIGEST], 0)):
            with patch.object(release, "existing_digest", side_effect=existing), patch.object(release, "run") as command:
                release.promote("image", "0.7.0", DIGEST, self.evidence)
                self.assertEqual(copies, command.call_count)
                if copies:
                    command.assert_called_once_with("docker", "buildx", "imagetools", "create", "--tag", "image:0.7.0", f"image@{DIGEST}")

    def test_conflicting_stable_digest_or_failed_promotion_blocks(self):
        self.write_verification()
        for existing in ([AMD], ["", AMD]):
            with patch.object(release, "existing_digest", side_effect=existing), patch.object(release, "run"):
                with self.assertRaises(ValueError):
                    release.promote("image", "0.7.0", DIGEST, self.evidence)

    def test_missing_or_mismatched_verification_blocks_promotion(self):
        with patch.object(release, "existing_digest") as registry, patch.object(release, "run") as command:
            with self.assertRaises(FileNotFoundError):
                release.promote("image", "0.7.0", DIGEST, self.evidence)
            self.write_verification()
            with self.assertRaises(ValueError):
                release.promote("image", "0.7.1", DIGEST, self.evidence)
            registry.assert_not_called()
            command.assert_not_called()

    def test_architecture_failure_cannot_produce_promotion_evidence(self):
        def inspect(reference, field="Manifest"):
            if field == "Manifest":
                return index()
            key = "SPDX" if field == "SBOM" else "SLSA"
            return {f"linux/{arch}": {key: {"present": True}} for arch in release.ARCHITECTURES}

        self.write_verification("stale evidence")
        config = json.dumps([{"Os": "linux", "Architecture": "amd64", "Config": {"Labels": {}}}])
        with patch.object(release, "inspect", side_effect=inspect), patch.object(release, "metadata"), \
                patch.object(release, "run", return_value=config), patch.object(release, "smoke", side_effect=ValueError("arm64 failed")):
            with self.assertRaises(ValueError):
                release.verify("image", DIGEST, "0.7.0", "commit", self.evidence)
        self.assertFalse((self.evidence / "verification.json").exists())
        self.assertTrue((self.evidence / "index.json").exists())


class CommandLineTests(unittest.TestCase):
    def rejected(self, *argv):
        with contextlib.redirect_stderr(io.StringIO()) as errors, self.assertRaises(SystemExit) as exit_:
            release.parse_args(argv)
        self.assertEqual(2, exit_.exception.code)
        return errors.getvalue()

    def test_each_operation_requires_its_own_arguments(self):
        self.assertIn("--version", self.rejected("select", "--image", "image"))
        self.assertIn("--digest", self.rejected("promote", "--image", "image", "--version", "0.7.0"))
        self.assertIn("--revision", self.rejected("verify", "--image", "image", "--version", "0.7.0", "--digest", DIGEST))
        self.assertIn("--architecture", self.rejected("smoke", "--image", "image"))
        self.assertIn("--image", self.rejected("smoke", "--architecture", "amd64"))
        self.rejected("smoke", "--image", "image", "--architecture", "riscv64")
        self.rejected("--image", "image")

    def test_operations_receive_their_arguments(self):
        calls = {"select": ("image", "0.7.0"), "verify": ("image", DIGEST, "0.7.0", "commit"),
                 "promote": ("image", "0.7.0", DIGEST), "smoke": ("image", "arm64")}
        arguments = {"select": ["--version", "0.7.0"], "verify": ["--version", "0.7.0", "--digest", DIGEST, "--revision", "commit"],
                     "promote": ["--version", "0.7.0", "--digest", DIGEST], "smoke": ["--architecture", "arm64"]}
        for operation, expected in calls.items():
            with patch.object(release, operation) as function:
                release.main([operation, "--image", "image", *arguments[operation]])
                function.assert_called_once_with(*expected)

    def test_every_workflow_call_parses(self):
        commands = re.findall(r"python3 scripts/ci/release\.py (.+)", WORKFLOW.read_text(encoding="utf-8"))
        operations = set()
        for command in commands:
            # The shell variables are filled in by the workflow; only the architecture is restricted.
            argv = ["amd64" if value == "$ARCHITECTURE" else value for value in shlex.split(command)]
            operations.add(release.parse_args(argv).operation)
        self.assertEqual({"select", "verify", "promote", "smoke"}, operations)


class ReleaseNotesTests(unittest.TestCase):
    def test_existing_labels_categorize_each_pr_once(self):
        config = yaml.safe_load((ROOT / ".github/release.yml").read_text())
        categories = config["changelog"]["categories"]
        labels = {item["name"] for item in json.loads((ROOT / ".github/labels.json").read_text())}
        fixtures = [([], "Other changes"), (["api", "wled"], "Other changes"),
                    (["bug", "security", "breaking change"], "Breaking changes"),
                    (["security", "bug"], "Security"), (["feature", "bug", "web"], "Features"),
                    (["bug", "chore"], "Bug fixes"), (["performance", "refactor"], "Performance"),
                    (["dependencies", "chore", "infra"], "Dependencies"),
                    (["documentation", "chore"], "Documentation"), (["refactor", "chore"], "Maintenance")]
        for category in categories:
            self.assertTrue(set(category["labels"]) <= labels | {"*"})
        for pr_labels, expected in fixtures:
            selected = [c["title"] for c in categories if
                        ("*" in c["labels"] or set(c["labels"]) & set(pr_labels)) and
                        not (set(c.get("exclude", {}).get("labels", [])) & set(pr_labels))]
            self.assertEqual([expected], selected)
        self.assertNotIn("exclude", config["changelog"])  # includes Dependabot and unlabeled PRs

    def test_release_preserves_generated_notes_digest_and_deployment_instructions(self):
        workflow = yaml.safe_load(WORKFLOW.read_text(encoding="utf-8"))
        names = [step.get("name") for step in workflow["jobs"]["release"]["steps"]]
        step = workflow["jobs"]["release"]["steps"][names.index("Create release")]
        self.assertTrue(step["with"]["generate_release_notes"])
        self.assertIn("BOBFX_IMAGE_DIGEST", step["with"]["body"])
        self.assertIn("steps.verified.outputs.digest", step["with"]["body"])
        # A release is published only after the verified image has been promoted.
        order = ["Verify both exact architecture digests and release metadata", "Promote tested index without rebuilding", "Create release"]
        self.assertEqual(order, [name for name in names if name in order])


if __name__ == "__main__":
    unittest.main()
