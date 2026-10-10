"""Live captioner for the overlay, The Other Side edition, on WhisperLive (github.com/collabora/WhisperLive).

The overlay streams 16 kHz mono PCM16 to 127.0.0.1:<port> over TCP (same as captions_server.py). This bridge runs
a WhisperLive server (faster_whisper backend, GPU) next to it, forwards the audio over WhisperLive's websocket and
turns its segments into JSON lines for the overlay:
    {"text": "...", "partial": true}    the sentence still being spoken (updates word by word)
    {"text": "..."}                     a finished sentence
Finished sentences that match a ghost line from the game (ghost_lines.json) become "GHOST (Orphan): ...".

    python captions_whisperlive.py [port] [model] [device]     e.g.  8766 small.en auto
"""
import json
import os
import re
import socket
import sys
import threading
import time
import difflib

import numpy as np

PORT = int(sys.argv[1]) if len(sys.argv) > 1 else 8766
MODEL = sys.argv[2] if len(sys.argv) > 2 else "small.en"
DEVICE = sys.argv[3] if len(sys.argv) > 3 else "auto"
WL_PORT = PORT + 1000          # WhisperLive's own websocket, local only

# make the pip-installed CUDA libraries visible to CTranslate2
for pkg in ("nvidia/cublas/bin", "nvidia/cudnn/bin"):
    p = os.path.abspath(os.path.join(os.path.dirname(np.__file__), "..", pkg))
    if os.path.isdir(p):
        os.add_dll_directory(p)
        os.environ["PATH"] = p + os.pathsep + os.environ.get("PATH", "")

# WhisperLive asks PyTorch whether there is a GPU; the PyTorch pip gave us is CPU-only, but the engine that
# really runs Whisper (CTranslate2) has its own CUDA. So answer with CTranslate2's view.
import ctranslate2  # noqa: E402
import torch  # noqa: E402
_gpu = DEVICE != "cpu" and ctranslate2.get_cuda_device_count() > 0
torch.cuda.is_available = lambda: _gpu
torch.cuda.get_device_capability = lambda *a, **k: (8, 6)

from whisper_live.server import TranscriptionServer  # noqa: E402
from websockets.sync.client import connect  # noqa: E402

# ---- ghost lines (same rules as captions_server.py)
_here = os.path.dirname(os.path.abspath(__file__))
GHOST = []
_lines_path = os.path.join(_here, "ghost_lines.json")
if os.path.exists(_lines_path):
    for ln in json.load(open(_lines_path, encoding="utf-8")):
        GHOST.append((ln["speaker"], ln["text"], None))
print(f"{len(GHOST)} ghost lines loaded", flush=True)

# "You're never gonna leave here" == "You are never going to leave here"
_LONG = {"youre": "you are", "im": "i am", "dont": "do not", "wont": "will not", "cant": "can not", "cannot": "can not",
         "gonna": "going to", "wanna": "want to", "its": "it is", "wasnt": "was not", "shouldnt": "should not",
         "youll": "you will", "lets": "let us", "whats": "what is", "wouldnt": "would not", "ill": "i will",
         "isnt": "is not", "didnt": "did not", "theyre": "they are", "thats": "that is"}


def norm(text: str):
    words = re.sub(r"[^a-z0-9 ]", "", text.lower().replace("'", "").replace("’", "")).split()
    return " ".join(_LONG.get(w, w) for w in words).split()


GHOST = [(who, exact, norm(exact)) for who, exact, _ in GHOST]
NAMES = "Hans, McGavin, Victoria, Skia, Orphan."


def match_ghost(text: str):
    """Best ghost line for a caption, or None. One- and two-word lines (yes, no, run) are never tagged:
    players say those too."""
    words = norm(text)
    if len(words) < 3:
        return None
    best, score = None, 0.0
    for who, exact, w in GHOST:
        if len(w) < 3:
            continue
        sm = difflib.SequenceMatcher(None, words, w)
        r = sm.ratio()
        # Players say things like "I'm going to get the EMF" (vs "I'm going to get you"): a loose match only
        # counts when the caption ends the way the ghost line ends. A near-exact match always counts.
        tail_ok = words[-2:] == w[-2:]
        if r < 0.9 and not tail_ok:
            r = 0.0
        # a loud sound right before a ghost line can garble its start ("Crash or not help you here"):
        # 4+ words in a row up to the line's end, covering most of it, also counts
        m = sm.find_longest_match(0, len(words), 0, len(w))
        if tail_ok and m.size >= 4 and m.size >= 0.55 * len(w) and m.b + m.size == len(w):
            r = max(r, 0.8)
        if r > score:
            best, score = (who, exact), r
    return best if score >= 0.72 else None


def label(text: str) -> str:
    g = match_ghost(text)
    if not g:
        return text
    return f"GHOST ({g[0]}): {g[1]}" if g[0] not in ("Ghost", "Ghost event") else f"GHOST: {g[1]}"


BANNED = {"thank you.", "thanks for watching.", "you", "you.", "bye.", "thank you for watching.", "thank you for watching!"}


# ---- WhisperLive server, in this process
def run_server():
    TranscriptionServer().run("127.0.0.1", port=WL_PORT, backend="faster_whisper", single_model=True,
                              max_clients=2, max_connection_time=24 * 3600)


t0 = time.time()
threading.Thread(target=run_server, daemon=True).start()
for _ in range(100):
    try:
        socket.create_connection(("127.0.0.1", WL_PORT), timeout=0.5).close()
        break
    except OSError:
        time.sleep(0.2)
print(f"whisperlive server up on {WL_PORT} ({'cuda' if _gpu else 'cpu'}) in {time.time() - t0:.1f}s", flush=True)


def handle(conn: socket.socket):
    print("overlay connected", flush=True)
    ws = connect(f"ws://127.0.0.1:{WL_PORT}", max_size=None)
    ws.send(json.dumps({
        "uid": f"overlay-{time.time():.0f}", "language": "en", "task": "transcribe", "model": MODEL,
        "use_vad": True, "audio_format": "int16", "send_last_n_segments": 4, "no_speech_thresh": 0.45,
        "initial_prompt": NAMES,
    }))
    lock = threading.Lock()
    alive = [True]

    def send(obj):
        try:
            with lock:
                conn.sendall((json.dumps(obj) + "\n").encode("utf-8"))
        except OSError:
            alive[0] = False

    def receive():
        done = set()        # start times of segments already sent as finished
        last_partial = ""
        while alive[0]:
            try:
                msg = json.loads(ws.recv())
            except Exception:
                break
            if "segments" not in msg:
                if msg.get("message") == "SERVER_READY":
                    print("whisperlive ready", flush=True)
                continue
            for seg in msg["segments"]:
                text = seg.get("text", "").strip()
                if not seg.get("completed") or seg["start"] in done:
                    continue
                done.add(seg["start"])
                if len(done) > 200:
                    done.clear()
                if not text or text.lower() in BANNED:
                    continue
                out = label(text)
                print(f"[final] {out}", flush=True)
                send({"text": out})
                last_partial = ""
            tail = msg["segments"][-1] if msg["segments"] else None
            if tail and not tail.get("completed"):
                text = tail.get("text", "").strip()
                if text and text != last_partial and text.lower() not in BANNED:
                    last_partial = text
                    send({"text": text, "partial": True})

    threading.Thread(target=receive, daemon=True).start()
    try:
        conn.settimeout(5)
        pending = b""
        while alive[0]:
            try:
                data = conn.recv(8192)
            except socket.timeout:
                continue
            if not data:
                break
            pending += data
            n = (len(pending) // 2) * 2
            if n:
                ws.send(pending[:n])
                pending = pending[n:]
    except OSError:
        pass
    finally:
        alive[0] = False
        try:
            ws.send(b"END_OF_AUDIO"); ws.close()
        except Exception:
            pass
        conn.close()
        print("overlay disconnected", flush=True)


srv = socket.socket(socket.AF_INET, socket.SOCK_STREAM)
# No SO_REUSEADDR: on Windows it lets a second server silently bind the same port. Fail loudly instead.
try:
    srv.bind(("127.0.0.1", PORT))
except OSError as e:
    print(f"port {PORT} is already in use by another captioner: {e}", flush=True)
    sys.exit(2)
srv.listen(1)
print(f"listening on 127.0.0.1:{PORT}", flush=True)
while True:
    c, _ = srv.accept()
    threading.Thread(target=handle, args=(c,), daemon=True).start()
