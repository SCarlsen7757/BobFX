"""Fail when the Docker SDK image is not the .NET SDK version that global.json selects."""
import argparse
from collections.abc import Sequence
import json
from pathlib import Path
import re


def global_json_sdk(text: str) -> str:
    return json.loads(text)["sdk"]["version"]


def dockerfile_sdk(text: str) -> str:
    """The version at the start of the SDK image tag, for example 10.0.403 in sdk:10.0.403-noble@sha256:..."""
    versions = re.findall(r"^FROM\s+(?:--platform=\S+\s+)?mcr\.microsoft\.com/dotnet/sdk:(\d+(?:\.\d+)*)", text,
                          re.IGNORECASE | re.MULTILINE)
    if len(set(versions)) != 1:
        raise ValueError(f"Expected one .NET SDK image version in the Dockerfile, found: {versions}")
    return versions[0]


def main(argv: Sequence[str] | None = None) -> None:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--global-json", type=Path, default=Path("global.json"))
    parser.add_argument("--dockerfile", type=Path, default=Path("BobFx.Core/Dockerfile"))
    args = parser.parse_args(argv)
    expected = global_json_sdk(args.global_json.read_text(encoding="utf-8-sig"))
    actual = dockerfile_sdk(args.dockerfile.read_text(encoding="utf-8"))
    if actual != expected:
        raise SystemExit(f"{args.dockerfile} builds with .NET SDK {actual}, but {args.global_json} selects {expected}")


if __name__ == "__main__":
    main()
