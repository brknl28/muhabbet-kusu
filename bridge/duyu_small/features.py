"""Log-mel fbank exactly as the model was trained on (w2v-BERT 2.0 front end:
Kaldi framing 25 ms / 10 ms, DC removal, pre-emphasis 0.97, Povey window,
512-point power spectrum, 80 mel bins, log, per-utterance mean/variance
normalization, two frames stacked -> 160-dim vectors at 50 Hz). Pure numpy;
the window and mel filters ship in fbank.npz (dumped by build.py from the
training feature extractor), so the engine needs no transformers/torch."""

from pathlib import Path

import numpy as np

WIN, HOP, NFFT, MELS = 400, 160, 512, 80
EPS = 1.192092955078125e-07


class Fbank:
    def __init__(self, tables: Path):
        z = np.load(tables)
        self.window = z["window"].astype(np.float64)          # (400,)
        self.mel = z["mel"].astype(np.float64)                # (257, 80)

    def __call__(self, wav: np.ndarray) -> np.ndarray:
        """16 kHz float32 waveform in [-1, 1] -> (T', 160) float32."""
        x = np.asarray(wav, dtype=np.float64) * 32768.0
        if x.shape[0] < WIN:
            x = np.pad(x, (0, WIN - x.shape[0]))
        n = (x.shape[0] - WIN) // HOP + 1
        idx = np.arange(WIN)[None, :] + HOP * np.arange(n)[:, None]
        frames = x[idx]                                        # (N, 400)
        frames = frames - frames.mean(1, keepdims=True)
        pre = np.concatenate([frames[:, :1] * (1 - 0.97), frames[:, 1:] - 0.97 * frames[:, :-1]], axis=1)
        spec = np.abs(np.fft.rfft(pre * self.window, n=NFFT)) ** 2       # (N, 257)
        logmel = np.log(np.maximum(spec @ self.mel, EPS))               # (N, 80)
        mean = logmel.mean(0, keepdims=True)
        var = logmel.var(0, ddof=1, keepdims=True) if n > 1 else np.ones_like(mean)
        logmel = (logmel - mean) / np.sqrt(var + 1e-7)
        t = n - n % 2
        return logmel[:t].reshape(t // 2, 2 * MELS).astype(np.float32)
