"""Gate complete Trivy JSON reports; never silently omit unfixed findings."""
import argparse
import datetime as dt
import json
from pathlib import Path
import re


def evaluate(report, exceptions, today=None):
    today = today or dt.datetime.now(dt.timezone.utc).date()
    allowed = {}
    for entry in exceptions:
        required = ("id", "package", "target", "rationale", "owner", "issue", "expires")
        if any(not isinstance(entry.get(key), str) or not entry[key].strip() for key in required):
            raise ValueError("Exceptions require id, package, target, rationale, owner, issue and expires")
        if not re.fullmatch(r"https://github\.com/SCarlsen7757/BobFX/issues/[1-9][0-9]*", entry["issue"]):
            raise ValueError("Exception must link a BobFX tracking issue")
        if dt.date.fromisoformat(entry["expires"]) <= today:
            raise ValueError(f"Expired exception: {entry['id']}")
        key = entry["id"], entry["package"], entry["target"]
        if key in allowed:
            raise ValueError(f"Duplicate exception: {key}")
        allowed[key] = entry
    if report.get("SchemaVersion") != 2 or not isinstance(report.get("Results"), list):
        raise ValueError("Expected a complete Trivy schema v2 report")
    blocked, suppressed = [], []
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


def main():
    parser = argparse.ArgumentParser()
    parser.add_argument("report", type=Path)
    parser.add_argument("--exceptions", type=Path, default=Path(".github/vulnerability-exceptions.json"))
    parser.add_argument("--summary", type=Path, required=True)
    args = parser.parse_args()
    result = evaluate(json.loads(args.report.read_text(encoding="utf-8")),
                      json.loads(args.exceptions.read_text(encoding="utf-8")))
    args.summary.write_text(json.dumps(result, indent=2) + "\n", encoding="utf-8")
    if result["blocked"]:
        raise SystemExit("Unexcepted HIGH/CRITICAL image vulnerabilities found; see full report and policy summary")


if __name__ == "__main__":
    main()
