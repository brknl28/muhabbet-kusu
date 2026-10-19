"""Ahenk's upstream inference with the application's WAV and sample-rate options."""
import math

from trnorm import normalize

STYLES = ("neutral", "angry", "calm", "cheerful", "empathetic", "sad", "surprised")


def ahenk_speech(voice, text, *, steps=16, cfg=2.0, style="neutral", **options):
    # Keep the training-order style IDs. The upstream eighth style was not trained.
    if style not in STYLES:
        raise ValueError("Desteklenmeyen Ahenk Cep üslubu.")
    if not 1 <= steps <= 64 or not math.isfinite(cfg) or not 0.5 <= cfg <= 10:
        raise ValueError("Geçersiz Ahenk Cep adım veya CFG değeri.")
    voice.model.style = STYLES.index(style)
    voice.model.steps, voice.model.cfg_scale = steps, cfg
    # Normalize the complete input once, before EMA's chunking and alphabet pass.
    return voice.engine.say(normalize(text), **options)
