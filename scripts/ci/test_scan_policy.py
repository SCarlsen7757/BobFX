import contextlib
import datetime as dt
import io
import json
import os
from pathlib import Path
import tempfile
import unittest
from unittest.mock import patch

import scan_policy
from scan_policy import evaluate

REPOSITORY = "SCarlsen7757/BobFX"


def report(severity="HIGH"):
    return {"SchemaVersion": 2, "Results": [{"Target": "image (ubuntu)", "Vulnerabilities": [{
        "VulnerabilityID": "CVE-2026-1234", "PkgName": "libexample", "Severity": severity, "InstalledVersion": "1.0"
    }]}]}


def exception(**changes):
    return {"id": "CVE-2026-1234", "package": "libexample", "target": "image (ubuntu)",
            "rationale": "Reviewed applicability", "owner": "SCarlsen7757",
            "issue": f"https://github.com/{REPOSITORY}/issues/12", "expires": "2026-10-14", **changes}


class VulnerabilityPolicyTests(unittest.TestCase):
    def setUp(self):
        self.today = dt.date(2026, 10, 13)
        self.report = report()
        self.exception = exception()

    def evaluate(self, exceptions, report=None):
        # The repository is passed explicitly so the result does not depend on where the tests run.
        return evaluate(report or self.report, exceptions, self.today, REPOSITORY)

    def test_unfixed_and_fixable_findings_both_block(self):
        for fixed in (None, "2.0"):
            self.report["Results"][0]["Vulnerabilities"][0]["FixedVersion"] = fixed
            self.assertEqual(1, len(self.evaluate([])["blocked"]))

    def test_critical_blocks_and_medium_does_not(self):
        self.assertTrue(self.evaluate([], report("CRITICAL"))["blocked"])
        self.assertFalse(self.evaluate([], report("MEDIUM"))["blocked"])

    def test_reviewed_exception_preserves_suppressed_evidence(self):
        result = self.evaluate([self.exception])
        self.assertFalse(result["blocked"])
        self.assertEqual(self.exception, result["suppressed"][0]["exception"])

    def test_exception_does_not_hide_other_package_or_target(self):
        for key in ("package", "target"):
            self.assertTrue(self.evaluate([exception(**{key: "other"})])["blocked"])

    def test_expired_incomplete_duplicate_and_invalid_exceptions_fail(self):
        for change in ({"expires": "2026-10-13"}, {"expires": "2026-10-12"}, {"owner": ""}, {"issue": "untracked"}):
            with self.assertRaises(ValueError):
                self.evaluate([exception(**change)])
        with self.assertRaises(ValueError):
            self.evaluate([self.exception, self.exception])

    def test_malformed_report_fails_closed(self):
        for malformed in ({}, {"SchemaVersion": 2}, {"SchemaVersion": 1, "Results": []}):
            with self.assertRaises(ValueError):
                evaluate(malformed, [], self.today, REPOSITORY)


class TrackingRepositoryTests(unittest.TestCase):
    def setUp(self):
        self.today = dt.date(2026, 10, 13)

    def test_issue_must_belong_to_the_repository_running_the_check(self):
        with patch.dict(os.environ, {"GITHUB_REPOSITORY": "someone/fork"}):
            self.assertEqual("someone/fork", scan_policy.tracking_repository())
            with self.assertRaisesRegex(ValueError, "someone/fork"):
                evaluate(report(), [exception()], self.today)
            fork = exception(issue="https://github.com/someone/fork/issues/3")
            self.assertFalse(evaluate(report(), [fork], self.today)["blocked"])

    def test_local_runs_default_to_this_repository(self):
        for environment in ({}, {"GITHUB_REPOSITORY": ""}):
            with patch.dict(os.environ, environment, clear=True):
                self.assertEqual(REPOSITORY, scan_policy.tracking_repository())
                self.assertFalse(evaluate(report(), [exception()], self.today)["blocked"])

    def test_repository_name_is_matched_literally(self):
        lookalike = exception(issue="https://github.com/SCarlsen7757/BobFXX/issues/12")
        trailing = exception(issue=f"https://github.com/{REPOSITORY}/issues/12/../../../other")
        for entry in (lookalike, trailing):
            with self.assertRaises(ValueError):
                evaluate(report(), [entry], self.today, REPOSITORY)
        with self.assertRaises(ValueError):
            evaluate(report(), [exception(issue="https://github.com/a/bXc/issues/1")], self.today, "a/b.c")


class CommandLineTests(unittest.TestCase):
    def setUp(self):
        directory = tempfile.TemporaryDirectory()
        self.addCleanup(directory.cleanup)
        self.directory = Path(directory.name)
        environment = patch.dict(os.environ, {"GITHUB_REPOSITORY": REPOSITORY})
        environment.start()
        self.addCleanup(environment.stop)

    def run_main(self, scan, exceptions):
        paths = {name: self.directory / f"{name}.json" for name in ("scan", "exceptions", "policy")}
        paths["scan"].write_text(json.dumps(scan), encoding="utf-8")
        paths["exceptions"].write_text(json.dumps(exceptions), encoding="utf-8")
        try:
            scan_policy.main([str(paths["scan"]), "--exceptions", str(paths["exceptions"]), "--summary", str(paths["policy"])])
        finally:
            self.summary = json.loads(paths["policy"].read_text(encoding="utf-8")) if paths["policy"].exists() else None

    def test_blocked_finding_fails_after_writing_the_summary(self):
        with self.assertRaises(SystemExit) as exit_:
            self.run_main(report(), [])
        self.assertIn("HIGH/CRITICAL", str(exit_.exception.code))
        self.assertEqual("CVE-2026-1234", self.summary["blocked"][0]["id"])
        self.assertEqual([], self.summary["suppressed"])

    def test_clean_or_excepted_report_passes_and_records_suppressions(self):
        self.run_main(report("MEDIUM"), [])
        self.assertEqual({"blocked": [], "suppressed": []}, self.summary)
        self.run_main(report(), [exception(expires="2999-01-01")])
        self.assertEqual([], self.summary["blocked"])
        self.assertEqual("libexample", self.summary["suppressed"][0]["package"])

    def test_invalid_exception_fails_without_a_summary(self):
        with self.assertRaises(ValueError):
            self.run_main(report(), [exception(expires="2000-01-01")])
        self.assertIsNone(self.summary)

    def test_summary_is_required(self):
        with self.assertRaises(SystemExit), contextlib.redirect_stderr(io.StringIO()):
            scan_policy.main([str(self.directory / "scan.json")])


if __name__ == "__main__":
    unittest.main()
