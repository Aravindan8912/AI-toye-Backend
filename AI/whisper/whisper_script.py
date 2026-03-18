from faster_whisper import WhisperModel
import sys

# Use CPU to avoid CUDA/cublas64_12.dll requirement when GPU drivers are missing
model = WhisperModel("base", device="cpu", compute_type="int8")

audio_file = sys.argv[1]

# language="en" helps get full English sentences; use None for auto-detect
# Consume all segments so the full transcription is returned (not just first word)
segments, _ = model.transcribe(audio_file, language="en", vad_filter=True)

parts = []
for segment in segments:
    t = (segment.text or "").strip()
    if t:
        parts.append(t)

# Full speech-to-text output, joined with spaces (what you said)
result = " ".join(parts)

print(result)