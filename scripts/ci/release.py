"""Verify exact registry digests, then promote the complete index without rebuilding."""
import argparse
import json
import os
from pathlib import Path
import re
import subprocess
import time

ARCHITECTURES = ("amd64", "arm64")


def run(*args):
    result = subprocess.run(args, check=True, text=True, encoding="utf-8", capture_output=True)
    return result.stdout.strip()


def digest(value):
    if not re.fullmatch(r"sha256:[0-9a-f]{64}", value):
        raise ValueError(f"Invalid image digest: {value}")
    return value


def inspect(reference, field="Manifest"):
    return json.loads(run("docker", "buildx", "imagetools", "inspect", reference, "--format", "{{json ." + field + "}}"))


def existing_digest(reference):
    try:
        return digest(inspect(reference)["digest"])
    except subprocess.CalledProcessError as error:
        # Authentication, networking and registry failures must not masquerade as absence.
        if re.search(r"manifest unknown|not found", error.stderr, re.IGNORECASE):
            return ""
        raise


def select(image, version):
    existing = existing_digest(f"{image}:{version}")
    candidate = f"{image}:candidate-{os.environ['GITHUB_SHA']}-{os.environ['GITHUB_RUN_ID']}-{os.environ['GITHUB_RUN_ATTEMPT']}"
    with open(os.environ["GITHUB_OUTPUT"], "a", encoding="utf-8") as output:
        output.write(f"existing={existing}\ncandidate={candidate}\n")


def platforms(index):
    images = {}
    attestations = set()
    for manifest in index.get("manifests", []):
        platform = manifest.get("platform", {})
        annotations = manifest.get("annotations", {})
        if annotations.get("vnd.docker.reference.type") == "attestation-manifest":
            attestations.add(annotations.get("vnd.docker.reference.digest"))
        elif platform.get("os") == "linux" and platform.get("architecture") in ARCHITECTURES:
            architecture = platform["architecture"]
            if architecture in images:
                raise ValueError(f"Duplicate platform: {architecture}")
            images[architecture] = digest(manifest["digest"])
        else:
            raise ValueError(f"Unexpected manifest platform: {platform}")
    if set(images) != set(ARCHITECTURES):
        raise ValueError("Release requires exactly linux/amd64 and linux/arm64")
    if not set(images.values()).issubset(attestations):
        raise ValueError("Each architecture requires an associated attestation manifest")
    return images


def metadata(config, architecture, version, revision):
    if config["Os"] != "linux" or config["Architecture"] != architecture:
        raise ValueError("Pulled image platform does not match the candidate index")
    labels = config["Config"].get("Labels") or {}
    if labels.get("org.opencontainers.image.version") != version:
        raise ValueError("OCI version differs from release tag")
    if labels.get("org.opencontainers.image.revision") != revision:
        raise ValueError("OCI source revision differs from tagged commit")


def smoke(reference, architecture, version=None, evidence=Path("release-evidence")):
    name = f"bobfx-smoke-{architecture}"
    volume = f"{name}-data"
    command = ("docker", "run", "-d", "--name", name, "--platform", f"linux/{architecture}",
        "--read-only", "--cap-drop", "ALL", "--security-opt", "no-new-privileges",
        "--mount", f"type=volume,source={volume},target=/app/data",
        "--tmpfs", "/tmp:rw,noexec,nosuid,size=64m",
        "--tmpfs", "/home/app/.aspnet:rw,noexec,nosuid,size=16m,uid=1654,gid=1654",
        "-e", "ASPNETCORE_ENVIRONMENT=Production", "-e", "AllowedHosts=localhost",
        "-e", "Security__AllowedClientNetworks__0=127.0.0.0/8",
        "-e", "Settings__DataPath=/app/data", reference)
    run("docker", "volume", "create", volume)
    run(*command)
    try:
        for _ in range(30):
            status = run("docker", "inspect", "--format", "{{.State.Health.Status}}", name)
            if status == "healthy":
                if run("docker", "exec", name, "id", "-u") != "1654":
                    raise ValueError("Runtime must use UID 1654")
                settings_url = "http://127.0.0.1:8080/api/installation"
                settings = json.loads(run("docker", "exec", name, "curl", "--fail", "--silent", settings_url))
                settings["animation"]["framesPerSecond"] = 23
                run("docker", "exec", name, "curl", "--fail", "--silent", "-X", "PUT",
                    "-H", "Content-Type: application/json", "--data-binary", json.dumps(settings), settings_url)
                run("docker", "rm", "-f", name)
                run(*command)
                for _ in range(30):
                    if run("docker", "inspect", "--format", "{{.State.Health.Status}}", name) == "healthy":
                        break
                    time.sleep(3)
                else:
                    raise ValueError("Recreated container failed to become healthy")
                restored = json.loads(run("docker", "exec", name, "curl", "--fail", "--silent", settings_url))
                if restored != settings:
                    raise ValueError("Installation settings did not survive container recreation")
                evidence.mkdir(parents=True, exist_ok=True)
                (evidence / f"persistence-{architecture}.json").write_text(json.dumps({"settingsSurvivedRecreation": True, "uid": 1654}) + "\n", encoding="utf-8")
                if version:
                    assembly = evidence / f"BobFx.Core-{architecture}.dll"
                    run("docker", "cp", f"{name}:/app/BobFx.Core.dll", str(assembly))
                    result = run("dotnet", "run", "--file", "scripts/ci/AssemblyVersion.cs", "--", str(assembly), version)
                    (evidence / f"assembly-{architecture}.txt").write_text(result + "\n", encoding="utf-8")
                    assembly.unlink()
                return
            time.sleep(3)
        raise ValueError("Hardened runtime failed to become healthy")
    finally:
        evidence.mkdir(parents=True, exist_ok=True)
        try:
            (evidence / f"smoke-{architecture}.log").write_text(run("docker", "logs", name), encoding="utf-8")
        finally:
            run("docker", "rm", "-f", name)
            run("docker", "volume", "rm", volume)


def attestation(data, platform, key):
    # The release index has two platforms; one platform's predicate cannot cover the other.
    value = data.get(platform)
    if not isinstance(value, dict) or not value.get(key):
        raise ValueError(f"Missing {key} attestation for {platform}")


def verify(image, candidate_digest, version, revision):
    evidence = Path("release-evidence")
    evidence.mkdir(exist_ok=True)
    (evidence / "verification.json").unlink(missing_ok=True)
    reference = f"{image}@{digest(candidate_digest)}"
    index = inspect(reference)
    if index["digest"] != candidate_digest:
        raise ValueError("Registry returned a different candidate digest")
    images = platforms(index)
    sbom, provenance = inspect(reference, "SBOM"), inspect(reference, "Provenance")
    for architecture in ARCHITECTURES:
        attestation(sbom, f"linux/{architecture}", "SPDX")
        attestation(provenance, f"linux/{architecture}", "SLSA")
    for filename, value in (("index.json", index), ("sbom.json", sbom), ("provenance.json", provenance)):
        (evidence / filename).write_text(json.dumps(value, indent=2) + "\n", encoding="utf-8")
    for architecture, child_digest in images.items():
        child = f"{image}@{child_digest}"
        run("docker", "pull", "--platform", f"linux/{architecture}", child)
        config = json.loads(run("docker", "image", "inspect", child))[0]
        metadata(config, architecture, version, revision)
        # Complete report: no severity, unfixed or ignore-file filtering before policy evaluation.
        report = evidence / f"scan-{architecture}.json"
        run("trivy", "image", "--image-src", "docker", "--scanners", "vuln", "--format", "json",
            "--ignorefile", "scripts/ci/empty-ignore", "--output", str(report), child)
        run("python3", "scripts/ci/scan_policy.py", str(report), "--summary", str(evidence / f"policy-{architecture}.json"))
        smoke(child, architecture, version, evidence)
    summary = {"version": version, "revision": revision, "indexDigest": candidate_digest, "platforms": images,
               "verified": ["vulnerability policy", "hardened runtime", "assembly version", "OCI metadata", "SBOM", "provenance"]}
    (evidence / "verification.json").write_text(json.dumps(summary, indent=2) + "\n", encoding="utf-8")


def promote(image, version, candidate_digest):
    candidate_digest = digest(candidate_digest)
    # Promotion requires evidence from this run, including both architecture gates.
    summary = json.loads(Path("release-evidence/verification.json").read_text(encoding="utf-8"))
    if summary["indexDigest"] != candidate_digest or summary["version"] != version or set(summary["platforms"]) != set(ARCHITECTURES):
        raise ValueError("Promotion does not match verified release evidence")
    stable = f"{image}:{version}"
    existing = existing_digest(stable)
    if existing and existing != candidate_digest:
        raise ValueError("Refusing to replace an existing stable version with a different digest")
    if not existing:
        run("docker", "buildx", "imagetools", "create", "--tag", stable, f"{image}@{candidate_digest}")
    if existing_digest(stable) != candidate_digest:
        raise ValueError("Stable image digest differs from the tested candidate")


def main():
    parser = argparse.ArgumentParser()
    parser.add_argument("operation", choices=("select", "verify", "promote", "smoke"))
    parser.add_argument("--image", required=True)
    parser.add_argument("--version")
    parser.add_argument("--digest")
    parser.add_argument("--revision")
    parser.add_argument("--architecture", choices=ARCHITECTURES)
    args = parser.parse_args()
    if args.operation == "select":
        select(args.image, args.version)
    elif args.operation == "verify":
        verify(args.image, args.digest, args.version, args.revision)
    elif args.operation == "promote":
        promote(args.image, args.version, args.digest)
    else:
        smoke(args.image, args.architecture)


if __name__ == "__main__":
    try:
        main()
    except subprocess.CalledProcessError as error:
        raise SystemExit(error.stderr or str(error)) from error
