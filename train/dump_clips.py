"""Labels The Other Side's extracted sounds with labels.json and writes clean 16 kHz clips for train_cnn.py
(clips.bin = int16 samples, clips.json = label/clip/offset/length per clip). Same format as PhasmoTrain.
Usage: python dump_clips.py <gameclips dir> <out data dir>
"""
import json, os, re, subprocess, sys
import numpy as np

root, out = sys.argv[1], sys.argv[2]
here = os.path.dirname(os.path.abspath(__file__))
rules = [(re.compile(r["Pattern"]), r["Label"]) for r in json.load(open(os.path.join(here, "labels.json")))]
SR = 16000
os.makedirs(out, exist_ok=True)

items, off = [], 0
counts = {}
with open(os.path.join(out, "clips.bin"), "wb") as bin_:
    for dirpath, _, files in os.walk(root):
        for f in sorted(files):
            if not f.endswith(".wav"): continue
            rel = os.path.relpath(os.path.join(dirpath, f), root).replace(os.sep, "/")
            label = next((l for rx, l in rules if rx.search(rel[:-4])), None)
            if label in (None, "skip"): continue
            pcm = subprocess.run(["ffmpeg", "-v", "error", "-i", os.path.join(dirpath, f), "-ac", "1", "-ar", str(SR), "-f", "s16le", "-"],
                                 capture_output=True, check=True).stdout
            w = np.frombuffer(pcm, np.int16)
            # the overlay never hears more than 1.5 s of one sound; long normal talking is cut into 6 s pieces instead
            pieces = [w[i: i + SR * 6] for i in range(0, len(w), SR * 6)] if label == "Voice" else \
                     [w if label == "_background" else w[: SR * 6]]
            for k, piece in enumerate(pieces):
                if len(piece) < 400 or np.abs(piece).max() < 4: continue
                bin_.write(piece.tobytes())
                items.append({"Label": label, "Clip": rel + (f"#{k}" if k else ""),
                              "Group": re.sub(r"[\s_\-.]*(\d+|[A-Za-z])$", "", f[:-4]).lower(), "Offset": off, "Length": len(piece)})
                off += len(piece); counts[label] = counts.get(label, 0) + 1

json.dump(items, open(os.path.join(out, "clips.json"), "w"))
json.dump([], open(os.path.join(out, "real_waves.json"), "w"))   # no in-game recordings yet
for k in sorted(counts): print(f"{k:<26} {counts[k]}")
print(f"dumped {len(items)} clips, {off / SR / 60:.1f} min of audio")
