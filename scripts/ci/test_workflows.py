"""Structure checks for the workflows and Dependabot, which cannot be run before they are pushed."""
import json
from pathlib import Path
import re
import unittest

import yaml

ROOT = Path(__file__).resolve().parents[2]
WORKFLOWS = sorted((ROOT / ".github/workflows").glob("*.yml"))
BUILD = ROOT / ".github/workflows/build-and-release.yml"
GATES = {"dotnet", "frontend", "e2e", "policy", "container"}


def load(path):
    return yaml.safe_load(path.read_text(encoding="utf-8"))


def step(job, name):
    """Find a step by its name, so reordering a job does not change what a test looks at."""
    return next(s for s in job["steps"] if s.get("name") == name)


def commands(job):
    return "\n".join(s.get("run", "") for s in job["steps"])


class WorkflowPolicyTests(unittest.TestCase):
    def test_every_action_is_pinned_to_a_commit_with_its_version(self):
        pinned = re.compile(r"uses: [\w.-]+/[\w.-]+(/[\w./-]+)?@[0-9a-f]{40} # v\d+\.\d+\.\d+")
        for path in WORKFLOWS:
            uses = [line.strip().removeprefix("- ") for line in path.read_text(encoding="utf-8").splitlines() if "uses:" in line]
            self.assertTrue(uses)
            for line in uses:
                self.assertRegex(line, pinned, path.name)

    def test_one_version_per_action_across_workflows(self):
        versions = {}
        for path in WORKFLOWS:
            for repository, commit in re.findall(r"uses: ([\w.-]+/[\w.-]+)[\w./-]*@([0-9a-f]{40})", path.read_text(encoding="utf-8")):
                versions.setdefault(repository, set()).add(commit)
        self.assertEqual({}, {repository: commits for repository, commits in versions.items() if len(commits) > 1})

    def test_every_job_has_a_pinned_runner_and_a_timeout(self):
        for path in WORKFLOWS:
            for name, job in load(path)["jobs"].items():
                self.assertEqual("ubuntu-24.04", job["runs-on"], f"{path.name}: {name}")
                self.assertIsInstance(job.get("timeout-minutes"), int, f"{path.name}: {name}")

    def test_checkouts_do_not_keep_credentials(self):
        for path in WORKFLOWS:
            for job in load(path)["jobs"].values():
                checkouts = [s for s in job["steps"] if s.get("uses", "").startswith("actions/checkout@")]
                self.assertEqual(1, len(checkouts))
                self.assertIs(False, checkouts[0]["with"]["persist-credentials"])


class BuildAndReleaseTests(unittest.TestCase):
    def setUp(self):
        self.workflow = load(BUILD)
        self.jobs = self.workflow["jobs"]

    def test_release_waits_for_every_other_job(self):
        self.assertEqual(GATES | {"release"}, set(self.jobs))
        self.assertEqual(GATES, set(self.jobs["release"]["needs"]))
        for name in GATES:
            self.assertNotIn("if", self.jobs[name], name)
            self.assertNotIn("continue-on-error", self.jobs[name], name)

    def test_scanner_version_is_defined_once(self):
        version = self.workflow["env"]["TRIVY_VERSION"]
        self.assertEqual(1, BUILD.read_text(encoding="utf-8").count(version))
        scanners = [s for job in self.jobs.values() for s in job["steps"] if s.get("uses", "").startswith("aquasecurity/")]
        self.assertEqual(3, len(scanners))
        for scanner in scanners:
            self.assertEqual("${{ env.TRIVY_VERSION }}", scanner["with"]["version"])

    def test_dotnet_job_builds_with_the_frontend_and_keeps_test_results(self):
        job = self.jobs["dotnet"]
        self.assertEqual("dotnet restore BobFx.slnx --locked-mode", step(job, "Restore locked dependencies")["run"])
        build = step(job, "Build with analyzers")["run"]
        self.assertIn("--warnaserror", build)
        # The HTTP tests request the built Vue pages, so this build must not skip them.
        self.assertNotIn("SkipFrontendBuild", build)
        self.assertEqual("dotnet test --solution BobFx.slnx --configuration Release --no-build --report-xunit-trx", step(job, "Test")["run"])
        upload = step(job, "Upload .NET test results")
        self.assertEqual("${{ !cancelled() }}", upload["if"])
        self.assertIn("TestResults", upload["with"]["path"])

    def test_frontend_job_installs_its_own_dependencies(self):
        job = self.jobs["frontend"]
        self.assertEqual("frontend", job["defaults"]["run"]["working-directory"])
        self.assertEqual(["npm ci", "npm run typecheck", "npm test"], [s["run"] for s in job["steps"] if "run" in s])
        self.assertNotIn("dotnet", commands(job))

    def test_browser_job_is_self_contained(self):
        job = self.jobs["e2e"]
        uses = " ".join(s.get("uses", "") for s in job["steps"])
        for action in ("actions/setup-dotnet@", "actions/setup-node@"):
            self.assertIn(action, uses)
        names = [s.get("name") for s in job["steps"]]
        order = ["Restore locked dependencies", "Build the app and its Vue frontend", "Install browser test dependencies",
                 "Cache Playwright browsers", "Install Chromium and WebKit", "Test published Vue app in Chromium and iPad WebKit"]
        self.assertEqual(order, [name for name in names if name in order])
        self.assertNotIn("SkipFrontendBuild", commands(job))
        self.assertIn("steps.playwright.outputs.version", step(job, "Cache Playwright browsers")["with"]["key"])
        self.assertEqual("${{ !cancelled() }}", step(job, "Upload browser test report and failure traces")["if"])

    def test_policy_job_runs_every_script_gate(self):
        job = self.jobs["policy"]
        run = commands(job)
        self.assertIn("pip install -r scripts/ci/requirements.txt", run)
        for script in ("sdk_version.py", "nuget_audit.py vulnerabilities.json"):
            self.assertIn(f"python3 scripts/ci/{script}", run)
        compose = step(job, "Validate development and production Compose")
        self.assertEqual(3, compose["run"].count("config --quiet"))
        self.assertIn("-f compose.prod.yaml -f compose.busybar.yaml", compose["run"])
        self.assertIn("BUSYBAR_TOKEN_FILE", compose["env"])
        scan = step(job, "Scan repository configuration and secrets")
        self.assertEqual("1", scan["with"]["exit-code"])

    def test_called_scripts_exist(self):
        scripts = set(re.findall(r"python3 (scripts/ci/\w+\.py)", BUILD.read_text(encoding="utf-8")))
        self.assertEqual({"scripts/ci/release.py", "scripts/ci/scan_policy.py", "scripts/ci/sdk_version.py", "scripts/ci/nuget_audit.py"}, scripts)
        for script in scripts:
            self.assertTrue((ROOT / script).is_file(), script)

    def test_release_build_reads_both_architecture_caches(self):
        container = step(self.jobs["container"], "Build test image")["with"]
        self.assertEqual("type=gha,mode=max,scope=${{ matrix.architecture }}", container["cache-to"])
        release = step(self.jobs["release"], "Build multi-architecture candidate once")["with"]
        sources = release["cache-from"].split()
        for architecture in self.jobs["container"]["strategy"]["matrix"]["architecture"]:
            self.assertIn(f"type=gha,scope={architecture}", sources)
            self.assertIn(f"linux/{architecture}", release["platforms"])


class CodeQlTests(unittest.TestCase):
    def test_csharp_is_built_without_the_frontend_and_typescript_without_a_build(self):
        job = load(ROOT / ".github/workflows/codeql.yml")["jobs"]["analyze"]
        self.assertEqual([{"language": "csharp", "build-mode": "manual"}, {"language": "javascript-typescript", "build-mode": "none"}],
                         job["strategy"]["matrix"]["include"])
        build = step(job, "Build C#")
        self.assertIn("-p:SkipFrontendBuild=true", build["run"])
        self.assertEqual("matrix.build-mode == 'manual'", build["if"])


class DependabotTests(unittest.TestCase):
    def setUp(self):
        self.updates = load(ROOT / ".github/dependabot.yml")["updates"]

    def test_every_dependency_manifest_is_watched_with_existing_labels(self):
        labels = {item["name"] for item in json.loads((ROOT / ".github/labels.json").read_text(encoding="utf-8"))}
        manifests = {("npm", "/frontend"): "frontend/package-lock.json", ("npm", "/e2e"): "e2e/package-lock.json",
                     ("nuget", "/"): "Directory.Packages.props", ("dotnet-sdk", "/"): "global.json",
                     ("docker", "/BobFx.Core"): "BobFx.Core/Dockerfile", ("pip", "/scripts/ci"): "scripts/ci/requirements.txt",
                     ("github-actions", "/"): ".github/workflows"}
        self.assertEqual(sorted(manifests), sorted((u["package-ecosystem"], u["directory"]) for u in self.updates))
        for update in self.updates:
            self.assertTrue((ROOT / manifests[update["package-ecosystem"], update["directory"]]).exists())
            self.assertTrue(set(update["labels"]) <= labels)
            self.assertIn("dependencies", update["labels"])

    def test_grouped_packages_are_dependencies_and_in_one_group_only(self):
        for update in self.updates:
            if update["package-ecosystem"] != "npm":
                continue
            package = json.loads((ROOT / update["directory"].strip("/") / "package.json").read_text(encoding="utf-8"))
            installed = set(package.get("dependencies", {})) | set(package.get("devDependencies", {}))
            grouped = [name for group in update["groups"].values() for name in group["patterns"]]
            self.assertEqual(len(grouped), len(set(grouped)))
            self.assertTrue(set(grouped) <= installed, set(grouped) - installed)


if __name__ == "__main__":
    unittest.main()
