"""Gate complete Trivy JSON reports; never silently omit unfixed findings."""
import argparse
from collections.abc import Sequence
import datetime as dt
import json
import os
from pathlib import Path
import re
from typing import Any

# Used when GITHUB_REPOSITORY is not set, so a local run behaves like CI in this repository.
DEFAULT_REPOSITORY = "SCarlsen7757/BobFX"
REQUIRED_EXCEPTION_FIELDS = ("id", "package", "target", "rationale", "owner", "issue", "expires")


def tracking_repository() -> str:
    """The owner/repo whose issues may track an exception."""
    return os.environ.get("GITHUB_REPOSITORY") or DEFAULT_REPOSITORY


def evaluate(report: dict[str, Any], exceptions: list[dict[str, Any]], today: dt.date | None = None,
             repository: str | None = None) -> dict[str, list[dict[str, Any]]]:
    today = today or dt.datetime.now(dt.timezone.utc).date()
    repository = repository or tracking_repository()
    issue_url = re.compile(rf"https://github\.com/{re.escape(repository)}/issues/[1-9][0-9]*")
    allowed: dict[tuple[str, str, str], dict[str, Any]] = {}
    for entry in exceptions:
        if any(not isinstance(entry.get(key), str) or not entry[key].strip() for key in REQUIRED_EXCEPTION_FIELDS):
            raise ValueError("Exceptions require id, package, target, rationale, owner, issue and expires")
        if not issue_url.fullmatch(entry["issue"]):
            raise ValueError(f"Exception must link a {repository} tracking issue")
        if dt.date.fromisoformat(entry["expires"]) <= today:
            raise ValueError(f"Expired exception: {entry['id']}")
        key = entry["id"], entry["package"], entry["target"]
        if key in allowed:
            raise ValueError(f"Duplicate exception: {key}")
        allowed[key] = entry
    if report.get("SchemaVersion") != 2 or not isinstance(report.get("Results"), list):
        raise ValueError("Expected a complete Trivy schema v2 report")
    blocked: list[dict[str, Any]] = []
    suppressed: list[dict[str, Any]] = []
    for result in report["Results"]:
        for finding in result.get("Vulnerabilities") or []:
            if finding.get("Severity") not in ("HIGH", "CRITICAL"):
                continue
            key = finding["VulnerabilityID"], finding["PkgName"], result["Target"]
            item = {"id": key[0], "package": key[1], "target": key[2], "severity": finding["Severity"],
                    "installedVersion": finding.get("InstalledVersion"), "fixedVersion": finding.get("FixedVersion")}
            if key in allowed:
                suppressed.append({**item, "exception": allowed[key]})
            else:
                blocked.append(item)
    return {"blocked": blocked, "suppressed": suppressed}


def main(argv: Sequence[str] | None = None) -> None:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("report", type=Path)
    parser.add_argument("--exceptions", type=Path, default=Path(".github/vulnerability-exceptions.json"))
    parser.add_argument("--summary", type=Path, required=True)
    args = parser.parse_args(argv)
    result = evaluate(json.loads(args.report.read_text(encoding="utf-8")),
                      json.loads(args.exceptions.read_text(encoding="utf-8")))
    args.summary.write_text(json.dumps(result, indent=2) + "\n", encoding="utf-8")
    if result["blocked"]:
        raise SystemExit("Unexcepted HIGH/CRITICAL image vulnerabilities found; see full report and policy summary")


if __name__ == "__main__":
    main()
