import sys
import types
import unittest
from pathlib import Path

sys.path.insert(0, str(Path(__file__).resolve().parents[1] / "bridge"))
from trnorm import normalize
from trtts import TurkishTTS
from tts_frontends import ahenk_speech


class Engine:
    def __init__(self):
        self._frontend = types.SimpleNamespace(spoken=lambda text: "native normalizer")
        self.calls = []

    def say(self, text, **options):
        self.calls.append((text, options))
        return self.calls[-1]

    def stream(self, text, **options):
        yield self.say(text, **options)


class TextFrontendTests(unittest.TestCase):
    def test_numbers_dates_times_and_currency_keep_spoken_context(self):
        text = normalize("Randevunuz 14.10.2026 saat 09:30'da; ücret 1.250 TL.")
        self.assertFalse(any(char.isdigit() for char in text))
        self.assertIn("bin iki yüz elli lira", text)
        self.assertIn("Ekim", text)

    def test_ema_normalizes_full_input_once_and_bypasses_native_normalizer(self):
        engine = Engine()
        voice = TurkishTTS(engine=engine)
        raw = "Dr. Ahmet saat 09:30'da geldi. Ücret 1.250 TL."
        spoken, options = voice.say(raw, path="voice.wav", sample_rate=24000, seed=42)
        self.assertEqual(normalize(raw), spoken)
        self.assertEqual(spoken, engine._frontend.spoken(spoken))
        self.assertEqual(24000, options["sample_rate"])
        self.assertEqual("voice.wav", options["path"])
        self.assertEqual(42, options["seed"])
        self.assertEqual(1, len(engine.calls))

    def test_ema_stream_and_batch_use_the_same_normalizer(self):
        engine = Engine()
        voice = TurkishTTS(engine=engine)
        self.assertEqual(normalize("5 TL mi?"), next(voice.stream("5 TL mi?"))[0])
        self.assertEqual([normalize("%15"), normalize("3. kat")], voice.say(["%15", "3. kat"])[0])

    def test_ahenk_style_and_synthesis_controls_reach_engine(self):
        voice = types.SimpleNamespace(model=types.SimpleNamespace(), engine=Engine())
        spoken, options = ahenk_speech(voice, "5 TL mi?", steps=8, cfg=1.5, style="sad",
                                      seed=42, sample_rate=16000, speed=1.2, path="voice.wav")
        self.assertEqual(normalize("5 TL mi?"), spoken)
        self.assertEqual((5, 8, 1.5), (voice.model.style, voice.model.steps, voice.model.cfg_scale))
        self.assertEqual(16000, options["sample_rate"])
        self.assertEqual(1.2, options["speed"])

    def test_untrained_style_and_invalid_controls_are_rejected(self):
        voice = types.SimpleNamespace(model=types.SimpleNamespace(), engine=Engine())
        for options in ({"style": "laughter"}, {"steps": 0}, {"cfg": float("nan")}):
            with self.subTest(options=options), self.assertRaises(ValueError):
                ahenk_speech(voice, "Merhaba", **options)
        self.assertEqual([], voice.engine.calls)


if __name__ == "__main__":
    unittest.main()
