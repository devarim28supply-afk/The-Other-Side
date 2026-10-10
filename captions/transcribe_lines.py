"""Transcribes every spoken clip in gameclips/ so live captions can snap to the game's real lines.
Usage: python transcribe_lines.py <gameclips dir> <out ghost_lines.json> [model]
"""
import json, os, re, sys
import numpy as np

for pkg in ("nvidia/cublas/bin", "nvidia/cudnn/bin"):
    p = os.path.abspath(os.path.join(os.path.dirname(np.__file__), "..", pkg))
    if os.path.isdir(p):
        os.add_dll_directory(p); os.environ["PATH"] = p + os.pathsep + os.environ.get("PATH", "")
from faster_whisper import WhisperModel  # noqa: E402

root, out = sys.argv[1], sys.argv[2]
name = sys.argv[3] if len(sys.argv) > 3 else "medium.en"

# folders/files that hold words (who speaks them is shown on the caption)
SPEAKERS = [
    (r"Ghosts/Hans/", "Hans"), (r"Ghosts/McGavin/", "McGavin"), (r"Ghosts/Orphan/", "Orphan"),
    (r"Responses/Skia/", "Skia"), (r"Ghosts/Responses/", "Ghost"), (r"SOH_Voice_FX", "Ghost"),
    (r"SFX_AreYouScared|SFX_IWillEndYou|SFX_WeAreComingForYou", "Ghost"),
    (r"RespawnSystem/Audio/Audio_Respawn_FemalePsst|BrideHumming", "Bride"),
    (r"VictoriaMemory_Crying|Cemetery_Praying|Hans_EatingFlesh", "Ghost event"),
    (r"Core/Training/", "Training"), (r"HQWelcomeWidget", "HQ"), (r"Z_Test/SFX_Test_Talking", "Test"),
]

try:
    model = WhisperModel(name, device="cuda", compute_type="float16")
except Exception as e:
    print("cuda failed, cpu:", str(e)[:120]); model = WhisperModel(name, device="cpu", compute_type="int8")

lines = []
for dirpath, _, files in os.walk(root):
    for f in sorted(files):
        if not f.endswith(".wav"): continue
        path = os.path.join(dirpath, f); rel = os.path.relpath(path, root).replace(os.sep, "/")
        who = next((s for pat, s in SPEAKERS if re.search(pat, rel)), None)
        if who is None or "Hunt_Orphan_Looping" in rel: continue
        hint = re.sub(r"^(SFX_)?(Spirit[Bb]ox_)?(Hans_|McGavin_?|Orphan_|Skia_)?", "", os.path.splitext(f)[0])
        hint = re.sub(r"_AlesiaDavina|_/d+$", "", hint).replace("_", " ")
        segs, _ = model.transcribe(path, language="en", beam_size=5, condition_on_previous_text=False,
                                   initial_prompt=f"{hint}.", vad_filter=False)
        text = " ".join(s.text.strip() for s in segs).strip()
        lines.append({"file": rel, "speaker": who, "name": hint, "text": text})
        print(f"{who:<12} {hint[:40]:<40} | {text}", flush=True)

json.dump(lines, open(out, "w", encoding="utf-8"), indent=1, ensure_ascii=False)
print(f"wrote {len(lines)} lines")
