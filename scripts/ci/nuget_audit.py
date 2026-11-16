"""Fail when `dotnet list package --vulnerable --format json` reports a vulnerable package."""
import argparse
from collections.abc import Sequence
import json
from pathlib import Path
from typing import Any


def vulnerable_packages(report: dict[str, Any]) -> list[dict[str, Any]]:
    """Direct and transitive packages, of every project and framework, that carry an advisory."""
    return [
        package
        for project in report.get("projects", [])
        for framework in project.get("frameworks", [])
        for key in ("topLevelPackages", "transitivePackages")
        for package in framework.get(key, [])
        if package.get("vulnerabilities")
    ]


def main(argv: Sequence[str] | None = None) -> None:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("report", type=Path)
    args = parser.parse_args(argv)
    # The .NET CLI can write a byte order mark when its output is redirected.
    vulnerabilities = vulnerable_packages(json.loads(args.report.read_text(encoding="utf-8-sig")))
    if vulnerabilities:
        print(json.dumps(vulnerabilities, indent=2))
        raise SystemExit("Known vulnerable NuGet dependencies found")


if __name__ == "__main__":
    main()
