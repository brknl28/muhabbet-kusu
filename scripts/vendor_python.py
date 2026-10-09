"""Bundle the installed, tested dependency closure into an embedded Python runtime."""
import argparse
import importlib.metadata as metadata
import json
import shutil
from pathlib import Path

from packaging.requirements import Requirement
from packaging.utils import canonicalize_name

ROOT_PACKAGES = ("ema-lightning==1.0.1", "antalia-mini[hub]==1.0.0")


def main():
    parser = argparse.ArgumentParser()
    parser.add_argument("target", type=Path)
    args = parser.parse_args()
    target = args.target.resolve()
    target.mkdir(parents=True, exist_ok=True)
    pending = [Requirement(name) for name in ROOT_PACKAGES]
    visited = {}
    copied = set()
    while pending:
        requirement = pending.pop()
        name = canonicalize_name(requirement.name)
        extras = requirement.extras | {""}
        if name in visited and extras <= visited[name]:
            continue
        visited.setdefault(name, set()).update(extras)
        distribution = metadata.distribution(requirement.name)
        if distribution.version not in requirement.specifier:
            raise RuntimeError(f"{name} {distribution.version} does not satisfy {requirement}")
        for value in distribution.requires or []:
            dependency = Requirement(value)
            if not dependency.marker or any(dependency.marker.evaluate({"extra": extra}) for extra in extras):
                pending.append(dependency)
        if name in copied:
            continue
        source_root = Path(distribution.locate_file("")).resolve()
        for relative in distribution.files or []:
            source = Path(distribution.locate_file(relative)).resolve()
            # Do not copy developer executables, external paths, or stale bytecode.
            if not source.is_relative_to(source_root) or not source.is_file() or source.suffix == ".pyc":
                continue
            destination = target / source.relative_to(source_root)
            destination.parent.mkdir(parents=True, exist_ok=True)
            if not destination.exists() or (destination.stat().st_size, destination.stat().st_mtime_ns) != (source.stat().st_size, source.stat().st_mtime_ns):
                shutil.copy2(source, destination)
        copied.add(name)
        print(f"Bundled {name}=={distribution.version}", flush=True)
    manifest = {name: metadata.version(name) for name in sorted(copied)}
    (target.parent.parent / "packages.json").write_text(json.dumps(manifest, indent=2), encoding="utf-8")


if __name__ == "__main__":
    main()
