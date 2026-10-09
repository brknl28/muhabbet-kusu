"""Setup prefetch; failures are recoverable on first launch."""
import argparse
import traceback
from pathlib import Path

from model_runtime import configure_cache, ensure_models


def main():
    parser = argparse.ArgumentParser()
    parser.add_argument("--models-dir", required=True)
    args = parser.parse_args()
    root = configure_cache(args.models_dir)
    with (root / "setup-download.log").open("a", encoding="utf-8") as log:
        try:
            def status(message):
                print(message, flush=True)
                log.write(message + "\n")
                log.flush()
            ensure_models(root, status)
            status("Ses modelleri hazır.")
            return 0
        except Exception:
            log.write(traceback.format_exc())
            print("İndirme tamamlanamadı. Uygulama ilk açılışta yeniden deneyecek.", flush=True)
            return 1


if __name__ == "__main__":
    raise SystemExit(main())
