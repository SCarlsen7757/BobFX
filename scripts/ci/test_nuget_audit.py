import contextlib
import io
import json
from pathlib import Path
import tempfile
import unittest

import nuget_audit

ADVISORY = {"severity": "High", "advisoryurl": "https://github.com/advisories/GHSA-xxxx-xxxx-xxxx"}


def report(top_level=(), transitive=()):
    """The shape `dotnet list package --vulnerable --include-transitive --format json` writes."""
    framework = {"framework": "net10.0"}
    if top_level:
        framework["topLevelPackages"] = list(top_level)
    if transitive:
        framework["transitivePackages"] = list(transitive)
    return {"version": 1, "parameters": "--vulnerable --include-transitive",
            "sources": ["https://api.nuget.org/v3/index.json"],
            "projects": [{"path": "BobFx.Core/BobFx.Core.csproj", "frameworks": [framework]},
                         # A project without vulnerable packages has no frameworks at all.
                         {"path": "BobFx.Core.Tests/BobFx.Core.Tests.csproj"}]}


def package(name, *vulnerabilities):
    result = {"id": name, "resolvedVersion": "1.0.0"}
    if vulnerabilities:
        result["vulnerabilities"] = list(vulnerabilities)
    return result


class VulnerablePackageTests(unittest.TestCase):
    def test_direct_and_transitive_vulnerable_packages_are_found(self):
        direct, indirect = package("Direct", ADVISORY), package("Indirect", ADVISORY)
        self.assertEqual([direct], nuget_audit.vulnerable_packages(report(top_level=[direct])))
        self.assertEqual([indirect], nuget_audit.vulnerable_packages(report(transitive=[indirect])))
        self.assertEqual([direct, indirect], nuget_audit.vulnerable_packages(report([direct], [indirect])))

    def test_packages_without_advisories_pass(self):
        self.assertEqual([], nuget_audit.vulnerable_packages(report()))
        self.assertEqual([], nuget_audit.vulnerable_packages(report([package("Clean")], [package("Empty") | {"vulnerabilities": []}])))
        self.assertEqual([], nuget_audit.vulnerable_packages({}))

    def test_every_project_and_framework_is_checked(self):
        second = package("Second", ADVISORY)
        both = report([package("First", ADVISORY)])
        both["projects"][1]["frameworks"] = [{"framework": "net9.0"}, {"framework": "net10.0", "transitivePackages": [second]}]
        self.assertEqual(["First", "Second"], [item["id"] for item in nuget_audit.vulnerable_packages(both)])


class CommandLineTests(unittest.TestCase):
    def run_main(self, content, encoding="utf-8"):
        with tempfile.TemporaryDirectory() as directory, contextlib.redirect_stdout(io.StringIO()) as output:
            path = Path(directory) / "vulnerabilities.json"
            path.write_text(json.dumps(content), encoding=encoding)
            try:
                nuget_audit.main([str(path)])
            finally:
                self.output = output.getvalue()

    def test_vulnerable_report_fails_and_lists_the_packages(self):
        with self.assertRaises(SystemExit) as exit_:
            self.run_main(report(transitive=[package("Indirect", ADVISORY)]))
        self.assertEqual("Known vulnerable NuGet dependencies found", exit_.exception.code)
        self.assertEqual("Indirect", json.loads(self.output)[0]["id"])

    def test_clean_report_passes_silently(self):
        self.run_main(report([package("Clean")]))
        self.assertEqual("", self.output)

    def test_report_with_a_byte_order_mark_is_read(self):
        with self.assertRaises(SystemExit):
            self.run_main(report([package("Direct", ADVISORY)]), encoding="utf-8-sig")
        self.run_main(report(), encoding="utf-8-sig")


if __name__ == "__main__":
    unittest.main()
