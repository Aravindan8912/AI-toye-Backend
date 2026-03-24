from faster_whisper import WhisperModel
import os
import sys

# Use CPU to avoid CUDA/cublas64_12.dll requirement when GPU drivers are missing
model = WhisperModel("base", device="cpu", compute_type="int8")

audio_file = sys.argv[1]

# ESP32 / toy mic: noisy, short clips. Defaults (no_speech_threshold=0.6) often classify whole clip as
# "no speech" → zero segments → empty stdout. Lower threshold + no VAD fixes most empty results.
# See: https://github.com/SYSTRAN/faster-whisper
segments, info = model.transcribe(
    audio_file,
    language="en",
    vad_filter=False,
    beam_size=5,
    no_speech_threshold=0.25,
    compression_ratio_threshold=2.8,
    condition_on_previous_text=False,
)

parts = []
for segment in segments:
    t = (segment.text or "").strip()
    if t:
        parts.append(t)

result = " ".join(parts)

if not result:
    try:
        fsize = os.path.getsize(audio_file)
    except OSError:
        fsize = -1
    dur = getattr(info, "duration", None)
    lang = getattr(info, "language", None)
    prob = getattr(info, "language_probability", None)
    print(
        f"empty_transcript file_bytes={fsize} duration_s={dur} "
        f"language={lang} language_probability={prob} "
        f"(if duration~0 or file tiny, WAV/PCM is bad; if duration OK but empty, speak louder or fix mic)",
        file=sys.stderr,
    )

print(result)