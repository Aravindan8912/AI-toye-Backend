from faster_whisper import WhisperModel
import sys

# Use CPU to avoid CUDA/cublas64_12.dll requirement when GPU drivers are missing
model = WhisperModel("base", device="cpu", compute_type="int8")

audio_file = sys.argv[1]

segments, _ = model.transcribe(audio_file)

result = ""
for segment in segments:
    result += segment.text

print(result)