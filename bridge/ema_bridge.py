import json
import os
import sys
import threading
import time
import traceback

# Ensure UTF-8 I/O regardless of Windows system code page
if hasattr(sys.stdin, "reconfigure"):
    try:
        sys.stdin.reconfigure(encoding="utf-8-sig")
        sys.stdout.reconfigure(encoding="utf-8")
        sys.stderr.reconfigure(encoding="utf-8")
    except Exception:
        pass

# Route general prints to stderr so WinForms IPC on stdout remains pure JSON
_real_stdout = sys.stdout
sys.stdout = sys.stderr

def send(payload):
    try:
        _real_stdout.write(json.dumps(payload, ensure_ascii=False) + "\n")
        _real_stdout.flush()
    except Exception:
        pass

class ProgressTracker:
    def __init__(self):
        self.current = 0
        self._stop = threading.Event()
        self._thread = None

    def start(self, enable_ticker=False):
        self.report(5)
        if enable_ticker:
            def _ticker():
                while not self._stop.wait(0.15):
                    if self.current < 92:
                        self.report(min(92, self.current + 2))
            self._thread = threading.Thread(target=_ticker, daemon=True)
            self._thread.start()

    def report(self, val):
        val = int(val)
        if val > self.current:
            self.current = val
            send({"ok": True, "status": "progress", "progress": self.current})

    def stop(self, finish=False):
        self._stop.set()
        if self._thread:
            self._thread.join(timeout=0.3)
        if finish:
            self.report(100)

from model_runtime import configure_cache, ensure_models

startup_stop = threading.Event()
startup_message = "Ses modelleri hazırlanıyor…"


def startup_status(message):
    global startup_message
    startup_message = message
    send({"ok": True, "status": "loading", "message": message})


def startup_heartbeat():
    while not startup_stop.wait(5):
        send({"ok": True, "status": "loading", "message": startup_message})


startup_thread = threading.Thread(target=startup_heartbeat, daemon=True)
startup_thread.start()

try:
    model_root = configure_cache()
    ensure_models(model_root, startup_status)
    startup_status("Ses modelleri yükleniyor…")
    import torch
    from ema_lightning import EMA
    from antalia_mini import Antalia

    device = "CUDA / " + torch.cuda.get_device_name(0) if torch.cuda.is_available() else "CPU"

    # Pre-instantiate both models persistent in memory
    ema_tts = EMA()
    antalia_tts = Antalia(model="cloud0day3/antalia-mini")

    startup_stop.set()
    startup_thread.join()
    send({
        "ok": True,
        "status": "ready",
        "device": device,
        "models": ["ema", "antalia"]
    })
except Exception as exc:
    startup_stop.set()
    startup_thread.join()
    send({
        "ok": False,
        "status": "startup_error",
        "error": f"{type(exc).__name__}: {exc}\n{traceback.format_exc()}"
    })
    sys.exit(1)

for raw in sys.stdin:
    raw = raw.strip().lstrip("\ufeff")
    if not raw:
        continue

    try:
        req = json.loads(raw)
        command = req.get("command")

        if command == "exit":
            send({"ok": True, "status": "bye"})
            break

        if command != "generate":
            send({"ok": False, "error": "Bilinmeyen komut."})
            continue

        model_choice = str(req.get("model", "ema")).lower()
        text = str(req.get("text", "")).strip()
        if not text:
            send({"ok": False, "error": "Seslendirilecek metin boş olamaz."})
            continue

        path = os.path.abspath(str(req.get("path", "output.wav")))
        speed = float(req.get("speed", 1.0))
        sample_rate = int(req.get("sample_rate", 48000))
        seed = req.get("seed", None)
        if seed is not None:
            seed = int(seed)

        steps = req.get("steps")
        if steps is not None:
            steps = int(steps)

        cfg = req.get("cfg")
        if cfg is not None:
            cfg = float(cfg)

        eq = req.get("eq")
        if eq is not None:
            eq = bool(eq)

        os.makedirs(os.path.dirname(path), exist_ok=True)

        tracker = ProgressTracker()

        if "antalia" in model_choice:
            orig_gen = antalia_tts._generator
            step_count = [0]
            target_steps = steps if steps else int(antalia_tts.defaults.get("steps", 8))
            try:
                from antalia_mini.api import split_model_text, model_text
                total_chunks = max(1, len(split_model_text(model_text(text))))
            except Exception:
                total_chunks = 1
            bs = 16 if (hasattr(antalia_tts, "device") and antalia_tts.device.type == "cuda") else 1
            total_batches = max(1, (total_chunks + bs - 1) // bs)
            total_steps = max(1, total_batches * target_steps)

            def hooked_gen(*args, **kwargs):
                step_count[0] += 1
                pct = min(92, 5 + int((step_count[0] / total_steps) * 85))
                tracker.report(pct)
                return orig_gen(*args, **kwargs)

            antalia_tts._generator = hooked_gen
            tracker.start(enable_ticker=False)
            try:
                kwargs = {
                    "path": path,
                    "speed": speed,
                    "sample_rate": sample_rate,
                }
                if seed is not None:
                    kwargs["seed"] = seed
                if steps is not None:
                    kwargs["steps"] = steps
                if cfg is not None:
                    kwargs["cfg"] = cfg
                if eq is not None:
                    kwargs["eq"] = eq

                speech = antalia_tts.say(text, **kwargs)
                tracker.report(100)
            finally:
                antalia_tts._generator = orig_gen
                tracker.stop()

            send({
                "ok": True,
                "status": "generated",
                "duration": float(speech.duration),
                "sample_rate": int(speech.sample_rate),
                "seed": int(speech.seed),
                "device": device,
                "model": "antalia"
            })
        else:
            orig_think = ema_tts._engine.think
            orig_decode = ema_tts._engine.decode

            def hooked_think(*args, **kwargs):
                tracker.report(40)
                return orig_think(*args, **kwargs)

            def hooked_decode(*args, **kwargs):
                tracker.report(75)
                return orig_decode(*args, **kwargs)

            ema_tts._engine.think = hooked_think
            ema_tts._engine.decode = hooked_decode
            tracker.start(enable_ticker=True)
            try:
                kwargs = {
                    "path": path,
                    "speed": speed,
                    "sample_rate": sample_rate,
                }
                if seed is not None:
                    kwargs["seed"] = seed

                speech = ema_tts.say(text, **kwargs)
                tracker.report(100)
            finally:
                ema_tts._engine.think = orig_think
                ema_tts._engine.decode = orig_decode
                tracker.stop()

            send({
                "ok": True,
                "status": "generated",
                "duration": float(speech.duration),
                "sample_rate": int(speech.sample_rate),
                "seed": int(speech.seed),
                "device": device,
                "model": "ema"
            })

    except Exception as exc:
        send({
            "ok": False,
            "status": "generation_error",
            "error": f"{type(exc).__name__}: {exc}\n{traceback.format_exc()}",
        })
