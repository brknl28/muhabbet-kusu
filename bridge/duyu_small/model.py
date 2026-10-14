"""DuyuSmall: the engine. Loads a published Duyu-small directory (encoder +
decoder ONNX at the chosen precision, tokenizer, fbank tables) and
transcribes 16 kHz audio on the CPU with onnxruntime. No torch.

    from duyu_small import DuyuSmall
    m = DuyuSmall()                                     # the model files next to the package, int8
    m = DuyuSmall(quant="q4")                           # fp32 | int8 | q4
    m = DuyuSmall("/path/to/Duyu-small")                # or an explicit directory ($DUYU_SMALL_DIR also works)
    text = m.transcribe(wav)                            # 16 kHz float32, any length
    m.transcribe(wav, lang="tr")                        # force Turkish (default <mix>: whatever is spoken)
    m.transcribe(wav, mode="ctc")                       # CTC greedy only (fastest, partials)
"""

import json
import time
from pathlib import Path

import numpy as np

from .audio import SAMPLE_RATE, chunks
from .decode import BeamSearch, Special, ctc_greedy_ids
from .features import Fbank

TAIL_PAD_S = 0.4          # push-to-talk cuts the last word: trailing silence for the decoder
QUANTS = ("fp32", "int8", "q4")


def default_dir() -> Path:
    """$DUYU_SMALL_DIR, else the directory that holds this package (the layout
    of the published repository: model files next to duyu_small/)."""
    import os
    env = os.environ.get("DUYU_SMALL_DIR")
    return Path(env) if env else Path(__file__).resolve().parents[1]


class DuyuSmall:
    def __init__(self, path: str | Path | None = None, quant: str = "int8", threads: int = 4, beam: int = 5,
                 lang: str | None = "mix", ctc_weight: float = 0.3):
        import onnxruntime as ort
        import sentencepiece as spm

        self.dir = Path(path) if path is not None else default_dir()
        if not (self.dir / "engine.json").exists():
            raise FileNotFoundError(f"no Duyu-small model files in {self.dir} (engine.json missing): pass the model "
                                    f"directory, DuyuSmall('/path/to/Duyu-small'), or set DUYU_SMALL_DIR")
        if quant not in QUANTS:
            raise ValueError(f"quant must be one of {QUANTS}")
        self.quant, self.beam, self.ctc_weight = quant, beam, ctc_weight
        meta = json.loads((self.dir / "engine.json").read_text())
        self.meta = meta
        self.sp = Special(pad=meta["pad"], bos=meta["bos"], eos=meta["eos"], unk=meta["unk"],
                          lang=meta["lang"], style=meta["style"])
        if lang is not None and lang not in self.sp.lang:
            raise ValueError(f"unknown lang {lang!r} (available: {sorted(self.sp.lang)}, None = auto)")
        self.lang = lang
        self.tok = spm.SentencePieceProcessor(model_file=str(self.dir / "spm.model"))
        self.fbank = Fbank(self.dir / "fbank.npz")
        so = ort.SessionOptions()
        so.intra_op_num_threads = int(threads)
        so.log_severity_level = 3
        qdir = self.dir / quant                                 # int8/ q4/ fp32/: one full set of graphs each
        if not qdir.is_dir():
            raise FileNotFoundError(f"{qdir} missing: this package has {[d.name for d in self.dir.iterdir() if d.is_dir() and (d / 'encoder_pass.onnx').exists()]}")
        sess = lambda n: ort.InferenceSession(str(qdir / f"{n}.onnx"), so, providers=["CPUExecutionProvider"])  # noqa: E731
        self.pre, self.pas, self.post = sess("encoder_pre"), sess("encoder_pass"), sess("encoder_post")
        z = np.load(self.dir / "passes.npz")
        self.gates, self.pass_embed = z["gates"].astype(np.float32), z["pass_embed"].astype(np.float32)
        self.dec = None
        if (qdir / "decoder.onnx").exists():
            self.dec = sess("decoder")
            self.search = BeamSearch(self._run_decoder, self.sp, meta["max_positions"])

    # --- graphs -----------------------------------------------------------------
    def encode(self, wav: np.ndarray):
        """-> h (1, T', d) float32, ctc log-probs (T', V) float32"""
        feats = self.fbank(wav)[None]
        x = self.pre.run(None, {"feats": feats})[0]
        for p in range(len(self.gates)):                       # shared refinement passes
            x_in = x
            y = self.pas.run(None, {"x": x_in + self.pass_embed[p][None, None, :]})[0]
            x = y if p == 0 else x_in + self.gates[p] * (y - x_in)
        logp = self.post.run(None, {"x": x})[0]
        return x, logp[0]

    def _run_decoder(self, tokens: np.ndarray, h: np.ndarray) -> np.ndarray:
        # h stays batch 1: the graph broadcasts it over the K beams (cross-attention
        # projections once per step instead of K times; logits identical)
        return self.dec.run(None, {"tokens": tokens.astype(np.int64), "memory": h})[0]

    # --- decoding ---------------------------------------------------------------
    def decode_ids(self, ids: list[int]) -> str:
        return self.tok.decode([int(i) for i in ids if i not in (self.sp.pad, self.sp.bos, self.sp.eos)
                                and i not in self.sp.control]).strip()

    def transcribe_utterance(self, wav: np.ndarray, mode: str = "auto", lang: str | None = "unset",
                             beam: int | None = None, tail_pad: bool = True) -> str:
        """One utterance (<= ~30 s). mode: auto = attention beam search when the
        package has a decoder, ctc = CTC greedy. tail_pad: 0.4 s of silence after
        the audio (push-to-talk recordings end on the last word); off = the exact
        benchmark numerics."""
        lang = self.lang if lang == "unset" else lang
        x = np.asarray(wav, dtype=np.float32)
        if tail_pad:
            x = np.concatenate([x, np.zeros(int(TAIL_PAD_S * SAMPLE_RATE), np.float32)])
        h, logp = self.encode(x)
        if mode == "ctc" or self.dec is None:
            return self.decode_ids(ctc_greedy_ids(logp, self.sp.pad))
        ids, _ = self.search(h, logp, beam=beam or self.beam, lang=lang, ctc_weight=self.ctc_weight)
        return self.decode_ids(ids)

    def transcribe(self, wav: np.ndarray, mode: str = "auto", lang: str | None = "unset",
                   beam: int | None = None, return_stats: bool = False, tail_pad: bool = True):
        """Any length: long audio is cut into <= 25 s chunks at energy minima."""
        t0 = time.perf_counter()
        parts = chunks(np.asarray(wav, dtype=np.float32))
        texts = [self.transcribe_utterance(p, mode, lang, beam, tail_pad) for p in parts]
        text = " ".join(t for t in texts if t)
        if not return_stats:
            return text
        dt = time.perf_counter() - t0
        secs = len(wav) / SAMPLE_RATE
        return text, {"seconds": round(secs, 2), "infer_s": round(dt, 3), "rtf": round(dt / secs, 4) if secs else None,
                      "chunks": len(parts), "quant": self.quant, "mode": "ctc" if (mode == "ctc" or self.dec is None) else "attn"}

    def transcribe_file(self, path: str | Path, **kw):
        from .audio import load_audio
        return self.transcribe(load_audio(path), **kw)
