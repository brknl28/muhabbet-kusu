"""Exercise the installed private runtime and both models with network disabled."""
import argparse
import json
import os
import subprocess
import threading
import wave
from pathlib import Path


def main():
    parser = argparse.ArgumentParser()
    parser.add_argument("root", type=Path)
    args = parser.parse_args()
    root = args.root.resolve()
    python = root / "runtime" / "python" / "python.exe"
    environment = os.environ.copy()
    environment.update({"PATH": str(Path(os.environ["SystemRoot"]) / "System32"), "HF_HUB_OFFLINE": "1", "PYTHONNOUSERSITE": "1", "MUHABBET_MODELS_DIR": str(root / "models")})
    environment.pop("PYTHONHOME", None)
    environment.pop("PYTHONPATH", None)
    subprocess.run([str(python), "-I", "-c", "import sys,torch,ema_lightning,antalia_mini; print(sys.executable); print(torch.__file__)"], env=environment, check=True)
    with (root / "smoke-bridge.log").open("w", encoding="utf-8") as errors:
        process = subprocess.Popen([str(python), "-u", str(root / "bridge" / "ema_bridge.py")], stdin=subprocess.PIPE, stdout=subprocess.PIPE, stderr=errors, text=True, encoding="utf-8", env=environment)
        timeout = threading.Timer(120, process.kill)
        timeout.start()
        try:
            def read_until(status):
                for line in process.stdout:
                    reply = json.loads(line)
                    if reply.get("status") == status:
                        assert reply["ok"], reply
                        return reply
                    if not reply.get("ok", False):
                        raise RuntimeError(reply)
                raise RuntimeError("Bridge exited before " + status)

            ready = read_until("ready")
            results = {"device": ready["device"], "offline": True, "models": {}}
            (root / "outputs").mkdir(exist_ok=True)
            for model in ("ema", "antalia"):
                output = root / "outputs" / ("setup-smoke-" + model + ".wav")
                request = {"command": "generate", "model": model, "text": "Merhaba, muhabbet kuşu.", "path": str(output), "speed": 1.0, "sample_rate": 24000, "seed": 42}
                process.stdin.write(json.dumps(request) + "\n")
                process.stdin.flush()
                reply = read_until("generated")
                with wave.open(str(output)) as audio:
                    assert audio.getframerate() == 24000
                    assert audio.getnframes() > 0
                results["models"][model] = {"duration": reply["duration"], "bytes": output.stat().st_size}
            mp3 = root / "outputs" / "setup-smoke.mp3"
            subprocess.run([str(root / "runtime" / "ffmpeg" / "ffmpeg.exe"), "-y", "-hide_banner", "-loglevel", "error", "-i", str(output), "-codec:a", "libmp3lame", str(mp3)], env=environment, check=True, timeout=30)
            assert mp3.stat().st_size > 1000
            results["mp3_bytes"] = mp3.stat().st_size
            process.stdin.write('{"command":"exit"}\n')
            process.stdin.flush()
            process.wait(timeout=10)
            assert process.returncode == 0
            (root / "smoke-results.json").write_text(json.dumps(results, indent=2), encoding="utf-8")
            print(json.dumps(results))
        finally:
            timeout.cancel()
            if process.poll() is None:
                process.kill()
                process.wait()


if __name__ == "__main__":
    main()
