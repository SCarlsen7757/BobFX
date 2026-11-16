from pathlib import Path
import tempfile
import unittest

import sdk_version

GLOBAL_JSON = '{ "sdk": { "version": "10.0.403", "rollForward": "disable" }, "test": { "runner": "Microsoft.Testing.Platform" } }'
DOCKERFILE = """# syntax=docker/dockerfile:1
FROM node:26-bookworm-slim@sha256:aaaa AS frontend
FROM mcr.microsoft.com/dotnet/sdk:{version}-noble@sha256:bbbb AS build
FROM mcr.microsoft.com/dotnet/aspnet:10.0-noble@sha256:cccc AS base
"""


class SdkVersionTests(unittest.TestCase):
    def test_versions_are_read_from_both_files(self):
        self.assertEqual("10.0.403", sdk_version.global_json_sdk(GLOBAL_JSON))
        self.assertEqual("10.0.403", sdk_version.dockerfile_sdk(DOCKERFILE.format(version="10.0.403")))
        self.assertEqual("10.0.403", sdk_version.dockerfile_sdk("FROM --platform=$BUILDPLATFORM mcr.microsoft.com/dotnet/sdk:10.0.403 AS build\n"))

    def test_dockerfile_must_name_exactly_one_sdk_version(self):
        for text in ("FROM mcr.microsoft.com/dotnet/aspnet:10.0-noble AS base\n",
                     "FROM mcr.microsoft.com/dotnet/sdk:latest AS build\n",
                     DOCKERFILE.format(version="10.0.403") + "FROM mcr.microsoft.com/dotnet/sdk:10.0.404 AS other\n"):
            with self.assertRaises(ValueError):
                sdk_version.dockerfile_sdk(text)

    def check(self, docker_version):
        with tempfile.TemporaryDirectory() as directory:
            global_json, dockerfile = Path(directory) / "global.json", Path(directory) / "Dockerfile"
            # Editors on Windows may save global.json with a byte order mark.
            global_json.write_text(GLOBAL_JSON, encoding="utf-8-sig")
            dockerfile.write_text(DOCKERFILE.format(version=docker_version), encoding="utf-8")
            sdk_version.main(["--global-json", str(global_json), "--dockerfile", str(dockerfile)])

    def test_matching_versions_pass(self):
        self.check("10.0.403")

    def test_differing_versions_fail_and_name_both(self):
        for version in ("10.0.404", "10.0", "10.0.4030"):
            with self.assertRaises(SystemExit) as exit_:
                self.check(version)
            self.assertIn(version, str(exit_.exception.code))
            self.assertIn("10.0.403", str(exit_.exception.code))


if __name__ == "__main__":
    unittest.main()
