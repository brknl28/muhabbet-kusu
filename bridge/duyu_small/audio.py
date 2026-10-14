"""Audio input for the engine: any file ffmpeg reads -> 16 kHz mono float32;
long recordings are cut into <= 25 s chunks at the quietest point near each
boundary (the model is trained on <= 30 s utterances)."""

import shutil
import subprocess
from pathlib import Path

import numpy as np

SAMPLE_RATE = 16000


def load_audio(path: str | Path, sr: int = SAMPLE_RATE) -> np.ndarray:
    path = Path(path)
    if path.suffix.lower() == ".wav":
        try:
            import wave
            with wave.open(str(path), "rb") as w:
                if w.getframerate() == sr and w.getnchannels() == 1 and w.getsampwidth() == 2:
                    raw = w.readframes(w.getnframes())
                    return np.frombuffer(raw, dtype=np.int16).astype(np.float32) / 32768.0
        except Exception:  # noqa: BLE001 - fall through to ffmpeg
            pass
    if shutil.which("ffmpeg") is None:
        raise RuntimeError("ffmpeg not found: install it or pass 16 kHz mono 16-bit WAV files")
    cmd = ["ffmpeg", "-v", "error", "-i", str(path), "-ac", "1", "-ar", str(sr), "-f", "f32le", "-"]
    raw = subprocess.run(cmd, check=True, capture_output=True).stdout
    return np.frombuffer(raw, dtype=np.float32).copy()


def chunks(audio: np.ndarray, max_s: float = 25.0, limit_s: float = 30.0, search_s: float = 4.0,
           sr: int = SAMPLE_RATE) -> list[np.ndarray]:
    """<= limit_s: one chunk. Longer: a cut every ~max_s at the lowest-energy
    20 ms frame within +-search_s of the boundary."""
    if len(audio) <= limit_s * sr:
        return [audio]
    frame = sr // 50
    rms = np.sqrt(np.convolve(audio.astype(np.float64) ** 2, np.ones(frame) / frame, mode="same") + 1e-12)
    out, start = [], 0
    while len(audio) - start > limit_s * sr:
        target = start + int(max_s * sr)
        lo = max(start + sr, target - int(search_s * sr))
        hi = min(len(audio) - sr, target + int(search_s * sr))
        cut = lo + int(np.argmin(rms[lo:hi])) if hi > lo else target
        out.append(audio[start:cut])
        start = cut
    out.append(audio[start:])
    return out
