import whisper
import sys
import json

model_name = sys.argv[1] if len(sys.argv) > 1 else "base"
audio_path = sys.argv[2]

model = whisper.load_model(model_name)
result = model.transcribe(audio_path, language=None, verbose=False)

segments = []
for seg in result["segments"]:
    start = seg["start"]
    end = seg["end"]
    text = seg["text"].strip()
    segments.append({
        "start": start,
        "end": end,
        "text": text
    })

print(json.dumps({"segments": segments, "language": result.get("language", "")}))
