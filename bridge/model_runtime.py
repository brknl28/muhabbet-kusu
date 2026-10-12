"""Application-local model storage, shared by Setup and the speech bridge."""
import json
import hashlib
import os
from pathlib import Path

ASSETS = (
    ("EMA Lightning", "canberkkkkkk/ema-lightning", ("config.json", "ema.pt", "decoder.pt")),
    ("Antalia-2 Mini", "cloud0day3/antalia-mini", ("config.json", "acoustic.safetensors", "vocoder.safetensors")),
)


def configure_cache(root=None):
    root = Path(root or os.environ.get("MUHABBET_MODELS_DIR") or Path(__file__).resolve().parent.parent / "models").resolve()
    root.mkdir(parents=True, exist_ok=True)
    os.environ.update({
        "HF_HOME": str(root / "huggingface"),
        "HF_HUB_CACHE": str(root / "huggingface" / "hub"),
        "XDG_CACHE_HOME": str(root / "cache"),
        "TORCH_HOME": str(root / "torch"),
        "ANTALIA_MINI_CACHE": str(root / "antalia"),
        "HF_HUB_DISABLE_SYMLINKS_WARNING": "1",
        "HF_HUB_DISABLE_SYMLINKS": "1",
        "HF_HUB_DISABLE_XET": "1",
        "HF_HUB_DISABLE_TELEMETRY": "1",
        "HF_HUB_DOWNLOAD_TIMEOUT": "30",
        "HF_HUB_ETAG_TIMEOUT": "10",
    })
    # Read before importing huggingface_hub: complete installs need no network.
    if assets_ready(root):
        os.environ["HF_HUB_OFFLINE"] = "1"
    return root


def assets_ready(root):
    root = Path(root).resolve()
    try:
        entries = json.loads((root / "ready.json").read_text(encoding="utf-8"))
        expected = {(repo, name) for _, repo, names in ASSETS for name in names}
        if {(e["repo"], e["name"]) for e in entries} != expected:
            return False
        for _, repo, _ in ASSETS:
            cache = root / "huggingface" / "hub" / ("models--" + repo.replace("/", "--"))
            reference = cache / "refs" / "main"
            if not reference.is_file() or not reference.read_text().strip():
                return False
            snapshot = cache / "snapshots" / reference.read_text().strip()
            if any((root / entry["path"]).parent != snapshot for entry in entries if entry["repo"] == repo):
                return False
        for entry in entries:
            cached_path = root / entry["path"]
            if cached_path.is_symlink():
                return False
            path = cached_path.resolve()
            if not path.is_relative_to(root) or not file_matches(path, entry):
                return False
        return True
    except (OSError, ValueError, TypeError, KeyError):
        return False


def file_digest(path):
    with Path(path).open("rb") as stream:
        return hashlib.file_digest(stream, "sha256").hexdigest()


def file_matches(path, entry):
    if not path.is_file() or entry["size"] <= 0 or path.stat().st_size != entry["size"]:
        return False
    return not entry.get("sha256") or file_digest(path) == entry["sha256"]


def ensure_models(root, on_status=lambda message: None):
    root = Path(root).resolve()
    from huggingface_hub import hf_hub_download
    from huggingface_hub.errors import LocalEntryNotFoundError

    try:
        previous = {(entry["repo"], entry["name"]): entry for entry in json.loads((root / "ready.json").read_text(encoding="utf-8"))}
    except (OSError, ValueError, TypeError, KeyError):
        previous = {}

    entries = []
    total = sum(len(names) for _, _, names in ASSETS)
    completed = 0
    for label, repo, names in ASSETS:
        for name in names:
            try:
                path = Path(hf_hub_download(repo, name, local_files_only=True))
                known = previous.get((repo, name))
                if path.is_symlink() or (known and not file_matches(path, known)):
                    on_status(f"{label} onarılıyor ({completed + 1}/{total})…")
                    # Hub force_download refreshes the blob but can leave an old snapshot
                    # pointer in place. Remove only the invalid application-local entry.
                    if not path.parent.resolve().is_relative_to(root):
                        raise ValueError("Model dosyası uygulama klasörünün dışında.")
                    path.unlink()
                    path = Path(hf_hub_download(repo, name, force_download=True))
            except LocalEntryNotFoundError:
                on_status(f"{label} indiriliyor ({completed + 1}/{total})…")
                path = Path(hf_hub_download(repo, name))
            completed += 1
            on_status(f"{label} hazırlanıyor ({completed}/{total})…")
            entries.append({"repo": repo, "name": name, "path": str(path.relative_to(root)), "size": path.stat().st_size, "sha256": file_digest(path)})
    temporary = root / f"ready.{os.getpid()}.tmp"
    temporary.write_text(json.dumps(entries, indent=2), encoding="utf-8")
    temporary.replace(root / "ready.json")
    return entries
