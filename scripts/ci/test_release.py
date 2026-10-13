import datetime as dt
import json
from pathlib import Path
import subprocess
import tempfile
import unittest
from unittest.mock import patch

import yaml

import release
from scan_policy import evaluate

ROOT = Path(__file__).resolve().parents[2]
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


class VulnerabilityPolicyTests(unittest.TestCase):
    def setUp(self):
        self.today = dt.date(2026, 10, 13)
        self.report = {"SchemaVersion": 2, "Results": [{"Target": "image (ubuntu)", "Vulnerabilities": [{
            "VulnerabilityID": "CVE-2026-1234", "PkgName": "libexample", "Severity": "HIGH", "InstalledVersion": "1.0"
        }]}]}
        self.exception = {"id": "CVE-2026-1234", "package": "libexample", "target": "image (ubuntu)",
                          "rationale": "Reviewed applicability", "owner": "SCarlsen7757",
                          "issue": "https://github.com/SCarlsen7757/BobFX/issues/12", "expires": "2026-10-14"}

    def test_unfixed_and_fixable_findings_both_block(self):
        for fixed in (None, "2.0"):
            self.report["Results"][0]["Vulnerabilities"][0]["FixedVersion"] = fixed
            self.assertEqual(1, len(evaluate(self.report, [], self.today)["blocked"]))

    def test_critical_blocks_and_medium_does_not(self):
        finding = self.report["Results"][0]["Vulnerabilities"][0]
        finding["Severity"] = "CRITICAL"
        self.assertTrue(evaluate(self.report, [], self.today)["blocked"])
        finding["Severity"] = "MEDIUM"
        self.assertFalse(evaluate(self.report, [], self.today)["blocked"])

    def test_reviewed_exception_preserves_suppressed_evidence(self):
        result = evaluate(self.report, [self.exception], self.today)
        self.assertFalse(result["blocked"])
        self.assertEqual(self.exception, result["suppressed"][0]["exception"])

    def test_exception_does_not_hide_other_package_or_target(self):
        for key in ("package", "target"):
            changed = {**self.exception, key: "other"}
            self.assertTrue(evaluate(self.report, [changed], self.today)["blocked"])

    def test_expired_incomplete_duplicate_and_invalid_exceptions_fail(self):
        for change in ({"expires": "2026-10-13"}, {"expires": "2026-10-12"}, {"owner": ""}, {"issue": "untracked"}):
            with self.assertRaises(ValueError):
                evaluate(self.report, [{**self.exception, **change}], self.today)
        with self.assertRaises(ValueError):
            evaluate(self.report, [self.exception, self.exception], self.today)

    def test_malformed_report_fails_closed(self):
        for report in ({}, {"SchemaVersion": 2}, {"SchemaVersion": 1, "Results": []}):
            with self.assertRaises(ValueError):
                evaluate(report, [], self.today)


class ArtifactTests(unittest.TestCase):
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

    def promote_with(self, existing):
        summary = json.dumps({"version": "0.7.0", "indexDigest": DIGEST, "platforms": {"amd64": AMD, "arm64": ARM}})
        return patch.object(Path, "read_text", return_value=summary), patch.object(release, "existing_digest", side_effect=existing)

    def test_promotion_copies_exact_index_and_matching_rerun_does_not_rebuild(self):
        for existing, copies in ((["", DIGEST], 1), ([DIGEST, DIGEST], 0)):
            evidence, registry = self.promote_with(existing)
            with evidence, registry, patch.object(release, "run") as command:
                release.promote("image", "0.7.0", DIGEST)
                self.assertEqual(copies, command.call_count)
                if copies:
                    command.assert_called_once_with("docker", "buildx", "imagetools", "create", "--tag", "image:0.7.0", f"image@{DIGEST}")

    def test_conflicting_stable_digest_or_failed_promotion_blocks(self):
        for existing in ([AMD], ["", AMD]):
            evidence, registry = self.promote_with(existing)
            with evidence, registry, patch.object(release, "run"):
                with self.assertRaises(ValueError):
                    release.promote("image", "0.7.0", DIGEST)

    def test_missing_or_mismatched_verification_blocks_promotion(self):
        with patch.object(Path, "read_text", side_effect=FileNotFoundError), patch.object(release, "run") as command:
            with self.assertRaises(FileNotFoundError):
                release.promote("image", "0.7.0", DIGEST)
            command.assert_not_called()
        evidence, registry = self.promote_with([])
        with evidence, registry, patch.object(release, "run") as command:
            with self.assertRaises(ValueError):
                release.promote("image", "0.7.1", DIGEST)
            command.assert_not_called()

    def test_architecture_failure_cannot_produce_promotion_evidence(self):
        def inspect(reference, field="Manifest"):
            if field == "Manifest":
                return index()
            key = "SPDX" if field == "SBOM" else "SLSA"
            return {f"linux/{arch}": {key: {"present": True}} for arch in release.ARCHITECTURES}

        old_cwd = Path.cwd()
        with tempfile.TemporaryDirectory() as directory:
            try:
                import os
                os.chdir(directory)
                Path("release-evidence").mkdir()
                Path("release-evidence/verification.json").write_text("stale evidence")
                config = json.dumps([{"Os": "linux", "Architecture": "amd64", "Config": {"Labels": {}}}])
                with patch.object(release, "inspect", side_effect=inspect), patch.object(release, "metadata"), \
                        patch.object(release, "run", return_value=config), patch.object(release, "smoke", side_effect=ValueError("arm64 failed")):
                    with self.assertRaises(ValueError):
                        release.verify("image", DIGEST, "0.7.0", "commit")
                self.assertFalse(Path("release-evidence/verification.json").exists())
            finally:
                os.chdir(old_cwd)


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
        workflow = yaml.safe_load((ROOT / ".github/workflows/build-and-release.yml").read_text())
        steps = workflow["jobs"]["release"]["steps"]
        step = next(s for s in steps if s.get("name") == "Create release")
        self.assertTrue(step["with"]["generate_release_notes"])
        self.assertIn("BOBFX_IMAGE_DIGEST", step["with"]["body"])
        self.assertIn("steps.verified.outputs.digest", step["with"]["body"])
        self.assertEqual("Promote tested index without rebuilding", steps[steps.index(step) - 2]["name"])


if __name__ == "__main__":
    unittest.main()
