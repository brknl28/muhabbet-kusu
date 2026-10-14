"""Decoding on top of the ONNX graphs, in numpy. A port of the training
decoder (train/asr/decode.py) kept rule-for-rule:

  * CTC greedy: argmax, collapse repeats, drop blanks (partials, fallback).
  * Attention beam search: step 0 forces (or auto-detects) the language
    token, step 1 the <cased> style, content steps never emit blank / BOS /
    control tokens, finished beams absorb </s> at zero cost, the search is
    bounded by the CTC greedy length (a loop cannot run 500 tokens).
  * Final ranking: length-normalized (1 - w) * attention + w * CTC score of
    every beam (CTC forward algorithm over the hypothesis; an unalignable
    hypothesis is vetoed, not scored 0).
  * Guards: silence veto (CTC emits <= 1 token but the decoder writes a
    sentence -> ""), runaway veto (hypothesis far longer than the CTC path
    or an n-gram repeated >= 6 times -> next clean beam, else the CTC text).

The decoder graph has no KV cache: every step re-runs the whole prefix for
the K beams in one batch. With 3 layers at d 256 that is cheap (a 10 s
utterance decodes in ~0.1-0.3 s on a laptop core) and keeps the export
trivial.
"""

from dataclasses import dataclass

import numpy as np

NEG = -1e30


@dataclass
class Special:
    pad: int
    bos: int
    eos: int
    unk: int
    lang: dict          # name -> id
    style: dict         # name -> id

    @property
    def control(self) -> list[int]:
        return sorted(set(self.lang.values()) | set(self.style.values()))


def log_softmax(x: np.ndarray, axis: int = -1) -> np.ndarray:
    m = x.max(axis=axis, keepdims=True)
    y = x - m
    return y - np.log(np.exp(y).sum(axis=axis, keepdims=True))


def ctc_greedy_ids(logp: np.ndarray, blank: int) -> list[int]:
    """logp (T, V) -> collapsed argmax token ids."""
    ids = logp.argmax(-1)
    out, prev = [], -1
    for t in ids.tolist():
        if t != prev and t != blank:
            out.append(t)
        prev = t
    return out


# --- runaway / silence guards (train/asr/decode.py constants) -----------------
MAX_REPEAT_RUN = 6
RUNAWAY_RATIO = 1.5
RUNAWAY_SLACK = 10
RUNAWAY_SLACK_SHORT = 4
RUNAWAY_SHORT = 8


def runaway_bound(ctc_len: int) -> int:
    slack = RUNAWAY_SLACK_SHORT if ctc_len < RUNAWAY_SHORT else RUNAWAY_SLACK
    return int(RUNAWAY_RATIO * ctc_len + slack)


def is_runaway(seq: list[int], ctc_len: int) -> bool:
    if len(seq) > runaway_bound(ctc_len):
        return True
    for n in (1, 2, 3, 4):
        run = 1
        for i in range(n, len(seq) - n + 1, n):
            if seq[i:i + n] == seq[i - n:i]:
                run += 1
                if run >= MAX_REPEAT_RUN:
                    return True
            else:
                run = 1
    return False


def ctc_nll(logp: np.ndarray, seq: list[int], blank: int) -> float:
    """-log p_CTC(seq | logp) by the forward algorithm (log domain). +inf when
    the sequence cannot be aligned to the frames (longer than the frames allow)."""
    T = logp.shape[0]
    if not seq:
        return float(-logp[:, blank].sum())
    ext = [blank]
    for s in seq:
        ext += [s, blank]
    S = len(ext)
    ext = np.asarray(ext)
    if T < (S + 1) // 2:
        return float("inf")
    # positions that may skip the blank between two different labels
    skip = np.zeros(S, dtype=bool)
    skip[2:] = (ext[2:] != blank) & (ext[2:] != ext[:-2])
    alpha = np.full(S, NEG)
    alpha[0] = logp[0, blank]
    if S > 1:
        alpha[1] = logp[0, ext[1]]
    emit = logp[:, ext]                                   # (T, S)
    for t in range(1, T):
        a1 = np.concatenate([[NEG], alpha[:-1]])
        a2 = np.concatenate([[NEG, NEG], alpha[:-2]])
        a2 = np.where(skip, a2, NEG)
        m = np.maximum(alpha, np.maximum(a1, a2))
        alpha = m + np.log(np.exp(alpha - m) + np.exp(a1 - m) + np.exp(a2 - m)) + emit[t]
    tail = alpha[-1] if S == 1 else np.logaddexp(alpha[-1], alpha[-2])
    return float(-tail) if tail > NEG / 2 else float("inf")


class BeamSearch:
    def __init__(self, run_decoder, sp: Special, max_positions: int):
        """run_decoder(tokens (K, L) int64, h (1, T, d)) -> logits (K, L, V) float32."""
        self.run_decoder = run_decoder
        self.sp = sp
        self.max_positions = max_positions

    def __call__(self, h: np.ndarray, ctc_logp: np.ndarray, beam: int = 5, lang: str | None = "mix",
                 style: str = "cased", ctc_weight: float = 0.3, alpha: float = 1.0) -> tuple[str, list[int]]:
        """One utterance: h (1, T, d), ctc_logp (T, V). Returns (token ids, language id used)."""
        sp = self.sp
        T, V = ctc_logp.shape
        ctc_seq = ctc_greedy_ids(ctc_logp, sp.pad)
        max_len = int(min(self.max_positions - 4, 8 + T // 3))
        if ctc_weight > 0:
            max_len = min(max_len, runaway_bound(len(ctc_seq)))
        K = beam
        n_prefix = 3
        control = np.asarray(sp.control)
        lang_ids = np.asarray(sorted(sp.lang.values()))
        tokens = np.full((K, 1), sp.bos, dtype=np.int64)
        scores = np.full(K, NEG); scores[0] = 0.0
        finished = np.zeros(K, dtype=bool)
        for t in range(max_len + n_prefix - 1):
            logits = self.run_decoder(tokens, h)[:, -1].astype(np.float32)      # (K, V)
            logp = log_softmax(logits)
            if t == 0:
                keep = np.full(V, NEG)
                keep[lang_ids if lang is None else [sp.lang[lang]]] = 0.0
                logp = logp + keep
            elif t == 1:
                forced = np.full_like(logp, NEG)
                forced[:, sp.style[style]] = 0.0
                logp = forced
            else:
                logp[:, sp.pad] = NEG
                logp[:, sp.bos] = NEG
                logp[:, control] = NEG
            if finished.any():
                logp[finished] = NEG
                logp[finished, sp.eos] = 0.0
            cand = (scores[:, None] + logp).reshape(-1)
            top = np.argpartition(-cand, K - 1)[:K]
            top = top[np.argsort(-cand[top])]
            beam_idx, tok_idx = top // V, top % V
            tokens = np.concatenate([tokens[beam_idx], tok_idx[:, None]], axis=1)
            finished = finished[beam_idx] | (tok_idx == sp.eos)
            scores = cand[top]
            if t >= n_prefix and finished.all():
                break
        return self._finalize(tokens, scores, ctc_logp, ctc_seq, ctc_weight, alpha, n_prefix)

    def _finalize(self, tokens, scores, ctc_logp, ctc_seq, ctc_weight, alpha, n_prefix):
        sp = self.sp
        K = tokens.shape[0]
        hyps, lens = [], np.zeros(K)
        for k in range(K):
            seq = tokens[k, n_prefix:].tolist()
            if sp.eos in seq:
                seq = seq[:seq.index(sp.eos)]
            hyps.append(seq)
            lens[k] = len(seq) + 1
        total = scores.astype(np.float64).copy()
        if ctc_weight > 0:
            nll = np.asarray([ctc_nll(ctc_logp, s, sp.pad) for s in hyps])
            total = (1 - ctc_weight) * total - ctc_weight * nll
        total = np.where(np.isfinite(total), total / np.maximum(lens, 1) ** alpha, -np.inf)
        order = np.argsort(-total)
        lang_used = int(tokens[0, 1])
        if ctc_weight > 0:
            if len(ctc_seq) <= 1 and len(hyps[int(order[0])]) > len(ctc_seq) + 1:
                return [], lang_used                                   # silence veto
            for j in order:
                if np.isfinite(total[j]) and not is_runaway(hyps[int(j)], len(ctc_seq)):
                    return hyps[int(j)], int(tokens[int(j), 1])
            return ctc_seq, lang_used                                  # every beam loops: the CTC text
        return hyps[int(order[0])], int(tokens[int(order[0]), 1])
