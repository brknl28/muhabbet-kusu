"""Local file transcription with mihu Turkish STT. JSON lines on stdout."""
import argparse
import json
import os
from pathlib import Path
import subprocess
import sys
import tempfile
import time
import shutil


def send(**values):
    print(json.dumps(values, ensure_ascii=False), flush=True)


def main():
    parser = argparse.ArgumentParser()
    parser.add_argument("audio", type=Path)
    parser.add_argument("--models-dir", type=Path, required=True)
    parser.add_argument("--packages", type=Path)
    parser.add_argument("--ffmpeg")
    parser.add_argument("--model", choices=["mihu", "uran"], default="mihu")
    args = parser.parse_args()
    if args.packages:
        sys.path.insert(0, str(args.packages.resolve()))
    if not args.audio.is_file():
        raise ValueError("Ses dosyası bulunamadı.")
    cache = args.models_dir.resolve() / "stt"
    os.environ.update({"HF_HOME": str(cache), "HF_HUB_DISABLE_SYMLINKS": "1",
                       "HF_HUB_DISABLE_SYMLINKS_WARNING": "1", "HF_HUB_DISABLE_XET": "1",
                       "HF_HUB_DISABLE_TELEMETRY": "1", "HF_HUB_DOWNLOAD_TIMEOUT": "60"})
    from huggingface_hub import snapshot_download
    send(status="loading", message="STT modeli hazırlanıyor. İlk kullanımda indirme yapılır.")
    if args.model == "uran":
        files = ["model.bin", "config.json", "tokenizer.json", "tokenizer_config.json", "vocabulary.*", "preprocessor_config.json", "LICENSE", "NOTICE"]
        repository = "curiositytech/uran-stt-base"
    else:
        files = ["tokens.txt", "encoder.int8.onnx", "decoder.onnx", "joiner.int8.onnx", "LICENSE", "NOTICE"]
        repository = "mihuai/turkish-stt"
    model_dir = snapshot_download(repository, allow_patterns=files, cache_dir=str(cache / "hub"))
    if args.model == "uran" and not (Path(model_dir) / "tokenizer.json").exists():
        # Uran publishes the Whisper vocabulary but no tokenizer.json.
        from huggingface_hub import hf_hub_download
        tokenizer = hf_hub_download("openai/whisper-base", "tokenizer.json", cache_dir=str(cache / "hub"))
        shutil.copyfile(tokenizer, Path(model_dir) / "tokenizer.json")
    send(status="loading", message="Ses metne dönüştürülüyor…")
    started = time.perf_counter()
    with tempfile.TemporaryDirectory(prefix="muhabbet-stt-") as temp:
        source = str(args.audio.resolve())
        if args.ffmpeg:
            source = str(Path(temp) / "audio.wav")
            subprocess.run([args.ffmpeg, "-nostdin", "-y", "-hide_banner", "-loglevel", "error",
                            "-i", str(args.audio.resolve()), "-vn", "-ac", "1", "-ar", "16000", source],
                           check=True, capture_output=True, timeout=300)
        if args.model == "uran":
            from faster_whisper import WhisperModel
            model = WhisperModel(model_dir, device="cpu", compute_type="int8", cpu_threads=4)
            segments, info = model.transcribe(source, language="tr", beam_size=5, vad_filter=True,
                                              condition_on_previous_text=False)
            text = " ".join(segment.text.strip() for segment in segments)
        else:
            import mihu_stt
            text = mihu_stt.transcribe(source, model_dir=model_dir)
    send(status="transcribed", ok=True, text=text, elapsed=time.perf_counter() - started)


if __name__ == "__main__":
    try:
        main()
    except Exception as error:
        print(str(error), file=sys.stderr)
        send(status="error", ok=False, message="Ses metne dönüştürülemedi. Model indirmesini ve dosya biçimini kontrol edin.")
        sys.exit(1)
