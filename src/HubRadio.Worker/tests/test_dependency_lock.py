from __future__ import annotations

import hashlib
import importlib.metadata
import json
import re
import struct
import sys
import tomllib
from pathlib import Path

from hubradio_worker.tasks import transcribe as asr


WORKER_ROOT = Path(__file__).resolve().parents[1]
RUNTIME_LOCK = WORKER_ROOT / "requirements.lock"
TEST_LOCK = WORKER_ROOT / "requirements-test.lock"
WHEEL_MATRIX = WORKER_ROOT / "wheel-matrix.json"
REPO_ROOT = WORKER_ROOT.parents[1]
_PACKAGE_NAME = re.compile(r"^[a-z0-9](?:[a-z0-9.-]*[a-z0-9])?$")
_VERSION = re.compile(r"^[A-Za-z0-9][A-Za-z0-9.!+_-]*$")
_SHA256 = re.compile(r"^[0-9a-f]{64}$")


def _normalize_name(value: str) -> str:
    return re.sub(r"[-_.]+", "-", value).lower()


def _read_lock(path: Path) -> tuple[dict[str, tuple[str, str]], tuple[str, ...]]:
    lines = path.read_text(encoding="utf-8").splitlines()
    requirements: dict[str, tuple[str, str]] = {}
    includes: list[str] = []
    options: set[str] = set()
    index = 0

    while index < len(lines):
        line = lines[index].strip()
        index += 1
        if not line or line.startswith("#"):
            continue
        if line in {"--only-binary=:all:", "--require-hashes"}:
            options.add(line)
            continue
        if line.startswith("-r "):
            includes.append(line.removeprefix("-r ").strip())
            continue

        assert line.endswith("\\"), f"Unpinned lock entry in {path.name}: {line}"
        requirement = line.removesuffix("\\").strip()
        name, separator, version = requirement.partition("==")
        normalized = _normalize_name(name)
        assert separator == "=="
        assert _PACKAGE_NAME.fullmatch(normalized)
        assert _VERSION.fullmatch(version)
        assert normalized not in requirements
        assert index < len(lines)
        hash_line = lines[index].strip()
        index += 1
        prefix = "--hash=sha256:"
        assert hash_line.startswith(prefix)
        digest = hash_line.removeprefix(prefix)
        assert _SHA256.fullmatch(digest)
        requirements[normalized] = (version, digest)

    assert options == {"--only-binary=:all:", "--require-hashes"}
    return requirements, tuple(includes)


def _matrix() -> dict[str, object]:
    return json.loads(WHEEL_MATRIX.read_text(encoding="utf-8"))


def test_python_direct_dependencies_and_build_backend_are_exact() -> None:
    project = tomllib.loads((WORKER_ROOT / "pyproject.toml").read_text(encoding="utf-8"))

    assert project["project"]["requires-python"] == ">=3.12,<3.13"
    assert project["build-system"]["requires"] == ["setuptools==84.0.0"]
    assert project["project"]["dependencies"] == [
        "ctranslate2==4.8.1",
        "faster-whisper==1.2.1",
    ]
    assert project["project"]["optional-dependencies"]["test"] == ["pytest==9.0.0"]
    assert sys.implementation.name == "cpython"
    assert sys.version_info[:2] == (3, 12)
    assert sys.platform == "win32"
    assert struct.calcsize("P") * 8 == 64


def test_canonical_build_consumes_the_lock_without_dependency_or_index_fallback() -> None:
    build_script = (REPO_ROOT / "scripts" / "build.ps1").read_text(encoding="utf-8")

    assert "'--require-hashes', '--only-binary=:all:'" in build_script
    assert "'-r', $workerTestLock" in build_script
    assert "'--no-index', '--no-deps', '--no-build-isolation', '-e', '.'" in build_script
    assert "'.[test]'" not in build_script


def test_hash_locks_and_wheel_matrix_have_the_same_closed_artifact_set() -> None:
    runtime, runtime_includes = _read_lock(RUNTIME_LOCK)
    test, test_includes = _read_lock(TEST_LOCK)
    matrix = _matrix()
    packages = matrix["packages"]
    assert isinstance(packages, list)

    matrix_records = {
        _normalize_name(package["name"]): (
            package["version"],
            package["sha256"],
            package["lock"],
        )
        for package in packages
    }
    assert len(matrix_records) == len(packages) == 29
    assert runtime_includes == ()
    assert test_includes == ("requirements.lock",)
    assert len(runtime) == 25
    assert len(test) == 4
    assert set(runtime).isdisjoint(test)
    assert set(matrix_records) == set(runtime) | set(test)

    for name, (version, digest) in runtime.items():
        assert matrix_records[name] == (version, digest, "runtime")
    for name, (version, digest) in test.items():
        assert matrix_records[name] == (version, digest, "test")


def test_wheel_matrix_manifest_and_target_are_canonical() -> None:
    matrix = _matrix()
    target = matrix["target"]
    resolver = matrix["resolverEvidence"]
    packages = matrix["packages"]
    assert isinstance(target, dict)
    assert isinstance(resolver, dict)
    assert isinstance(packages, list)

    assert matrix["schemaVersion"] == 1
    assert target == {
        "operatingSystem": "Windows",
        "architecture": "x64",
        "pythonImplementation": "CPython",
        "pythonVersion": "3.12",
        "verifiedInterpreterVersion": "3.12.10",
        "wheelPlatform": "win_amd64",
    }
    assert resolver["wheelCount"] == 29
    assert resolver["noCache"] is True
    assert resolver["onlyBinary"] is True

    records = "".join(
        f"{package['fileName']}\0{package['byteSize']}\0{package['sha256']}\n"
        for package in sorted(packages, key=lambda item: item["fileName"])
    )
    assert hashlib.sha256(records.encode("utf-8")).hexdigest() == resolver[
        "artifactSetSha256"
    ]

    for package in packages:
        assert package["byteSize"] > 0
        assert _SHA256.fullmatch(package["sha256"])
        assert package["fileName"].endswith(".whl")
        assert "win32" not in package["fileName"]


def test_current_environment_matches_every_locked_distribution() -> None:
    runtime, _ = _read_lock(RUNTIME_LOCK)
    test, _ = _read_lock(TEST_LOCK)

    for name, (expected_version, _) in (runtime | test).items():
        assert importlib.metadata.version(name) == expected_version


def test_unadopted_models_and_gpu_extras_are_excluded_from_the_lock() -> None:
    runtime, _ = _read_lock(RUNTIME_LOCK)
    test, _ = _read_lock(TEST_LOCK)
    locked = set(runtime) | set(test)
    prohibited = {
        "mediapipe",
        "opencv-contrib-python",
        "opencv-python",
        "pyannote-audio",
        "scenedetect",
        "torch",
        "transformers",
    }

    assert locked.isdisjoint(prohibited)
    assert asr.PINNED_CONFIGURATION.device == "cpu"
    assert asr.PINNED_CONFIGURATION.compute_type == "int8"
    assert asr.PINNED_CONFIGURATION.vad_filter is False

    matrix = _matrix()
    excluded = matrix["excludedCandidates"]
    remaining = matrix["remainingGates"]
    assert isinstance(excluded, list)
    assert isinstance(remaining, list)
    excluded_text = json.dumps(excluded, ensure_ascii=False)
    assert "A-010 condition-false" in excluded_text
    assert "A-012 held" in excluded_text
    assert "A-013 held" in excluded_text
    assert "A-015 condition-false" in excluded_text
    assert any("CUDA redistribution remains disabled" in gate for gate in remaining)