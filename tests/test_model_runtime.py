import json
import os
import sys
import tempfile
import types
import unittest
from pathlib import Path
from unittest.mock import patch

sys.path.insert(0, str(Path(__file__).resolve().parents[1] / "bridge"))
from model_runtime import ASSETS, assets_ready, configure_cache, ensure_models

TEMP_ROOT = Path(__file__).resolve().parents[1] / "artifacts" / "test-temp"
TEMP_ROOT.mkdir(parents=True, exist_ok=True)


class ModelRuntimeTests(unittest.TestCase):
    def make_cache(self, root):
        entries = []
        for _, repo, names in ASSETS:
            cache = root / "huggingface" / "hub" / ("models--" + repo.replace("/", "--"))
            (cache / "refs").mkdir(parents=True)
            (cache / "refs" / "main").write_text("revision")
            snapshot = cache / "snapshots" / "revision"
            snapshot.mkdir(parents=True)
            for name in names:
                path = snapshot / name
                path.write_bytes(b"test model")
                entries.append({"repo": repo, "name": name, "path": str(path.relative_to(root)), "size": path.stat().st_size})
        (root / "ready.json").write_text(json.dumps(entries))
        return entries

    def test_fresh_install_uses_application_cache_and_allows_download(self):
        with tempfile.TemporaryDirectory(dir=TEMP_ROOT) as temp, patch.dict(os.environ, {}, clear=True):
            root = configure_cache(Path(temp) / "models")
            self.assertFalse(assets_ready(root))
            self.assertNotIn("HF_HUB_OFFLINE", os.environ)
            for key in ("HF_HOME", "HF_HUB_CACHE", "TORCH_HOME", "XDG_CACHE_HOME", "ANTALIA_MINI_CACHE"):
                self.assertTrue(Path(os.environ[key]).is_relative_to(root))

    def test_complete_install_starts_offline(self):
        with tempfile.TemporaryDirectory(dir=TEMP_ROOT) as temp, patch.dict(os.environ, {}, clear=True):
            root = Path(temp)
            self.make_cache(root)
            configure_cache(root)
            self.assertEqual("1", os.environ["HF_HUB_OFFLINE"])

    def test_deleted_or_truncated_weight_requires_download(self):
        with tempfile.TemporaryDirectory(dir=TEMP_ROOT) as temp:
            root = Path(temp)
            entries = self.make_cache(root)
            path = root / entries[0]["path"]
            path.write_bytes(b"short")
            self.assertFalse(assets_ready(root))
            path.unlink()
            self.assertFalse(assets_ready(root))

    def test_missing_revision_requires_repair(self):
        with tempfile.TemporaryDirectory(dir=TEMP_ROOT) as temp:
            root = Path(temp)
            self.make_cache(root)
            next(root.glob("huggingface/hub/*/refs/main")).unlink()
            self.assertFalse(assets_ready(root))

    def test_invalid_manifest_is_not_ready(self):
        with tempfile.TemporaryDirectory(dir=TEMP_ROOT) as temp:
            root = Path(temp)
            (root / "ready.json").write_text("broken")
            self.assertFalse(assets_ready(root))
            entries = self.make_cache(root)
            entries[0]["path"] = "../outside.pt"
            (root / "ready.json").write_text(json.dumps(entries))
            self.assertFalse(assets_ready(root))

    def test_failed_download_can_resume_without_replacing_completed_files(self):
        class MissingFile(Exception):
            pass
        with tempfile.TemporaryDirectory(dir=TEMP_ROOT) as temp:
            root = Path(temp)
            blocked = True
            downloads = []
            def download(repo, name, local_files_only=False):
                cache = root / "huggingface" / "hub" / ("models--" + repo.replace("/", "--"))
                snapshot = cache / "snapshots" / "revision"
                path = snapshot / name
                if path.exists():
                    return str(path)
                if local_files_only:
                    raise MissingFile()
                if blocked and name == "ema.pt":
                    raise OSError("Connection interrupted")
                snapshot.mkdir(parents=True, exist_ok=True)
                (cache / "refs").mkdir(exist_ok=True)
                (cache / "refs" / "main").write_text("revision")
                path.write_bytes(b"downloaded")
                downloads.append((repo, name))
                return str(path)
            modules = {"huggingface_hub": types.SimpleNamespace(hf_hub_download=download), "huggingface_hub.errors": types.SimpleNamespace(LocalEntryNotFoundError=MissingFile)}
            with patch.dict(sys.modules, modules):
                with self.assertRaises(OSError):
                    ensure_models(root)
                self.assertFalse((root / "ready.json").exists())
                blocked = False
                ensure_models(root)
                self.assertTrue(assets_ready(root))
                self.assertEqual(6, len(downloads))


if __name__ == "__main__":
    unittest.main()
