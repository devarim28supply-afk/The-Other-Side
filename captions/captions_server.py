"""Local live captioner for the overlay, The Other Side edition.

Same as the PhasmoSound captioner, plus: every caption is compared with the ghost lines the game ships
(ghost_lines.json next to this script, made by transcribe_lines.py). A close match is replaced by the
exact line and tagged with who says it, e.g. "GHOST (Orphan): You are never gonna leave here".

The overlay streams 16 kHz mono PCM16 to 127.0.0.1:8766 over TCP. This process cuts it into utterances
with a simple energy gate, transcribes each one with faster-whisper (GPU if available, else CPU) and
sends JSON lines back on the same socket: {"text": "...", "t0": 12.3, "dur": 1.8, "ms": 640}

    python captions_server.py [port] [model] [device]     e.g.  8766 small.en cuda
"""
import json
import os
import socket
import sys
import threading
import time

import numpy as np

PORT = int(sys.argv[1]) if len(sys.argv) > 1 else 8766
MODEL = sys.argv[2] if len(sys.argv) > 2 else "small.en"
DEVICE = sys.argv[3] if len(sys.argv) > 3 else "auto"
SR = 16000

# make the pip-installed CUDA libraries visible to CTranslate2
for pkg in ("nvidia/cublas/bin", "nvidia/cudnn/bin"):
    p = os.path.join(os.path.dirname(np.__file__), "..", pkg)
    p = os.path.abspath(p)
    if os.path.isdir(p):
        os.add_dll_directory(p)
        os.environ["PATH"] = p + os.pathsep + os.environ.get("PATH", "")

from faster_whisper import WhisperModel  # noqa: E402

t0 = time.time()
model = None
used = None
for dev, ctype in ([("cuda", "float16"), ("cpu", "int8")] if DEVICE == "auto" else [(DEVICE, "float16" if DEVICE == "cuda" else "int8")]):
    try:
        model = WhisperModel(MODEL, device=dev, compute_type=ctype)
        # smoke test so a broken CUDA install fails here, not on the first caption
        list(model.transcribe(np.zeros(SR, dtype=np.float32), language="en", beam_size=1)[0])
        used = f"{dev}/{ctype}"
        break
    except Exception as e:
        print(f"{dev} failed: {str(e)[:160]}", flush=True)
        model = None
if model is None:
    print("no usable device", flush=True)
    sys.exit(1)
print(f"whisper {MODEL} ready on {used} in {time.time() - t0:.1f}s", flush=True)


# ghost lines from the game files: normalized words -> (speaker, exact text)
import difflib, re  # noqa: E402
_here = os.path.dirname(os.path.abspath(__file__))
_lines_path = os.path.join(_here, "ghost_lines.json")
# "You're never gonna leave here" == "You are never going to leave here"
_LONG = {"youre": "you are", "im": "i am", "dont": "do not", "wont": "will not", "cant": "can not", "cannot": "can not",
         "gonna": "going to", "wanna": "want to", "its": "it is", "wasnt": "was not", "shouldnt": "should not",
         "youll": "you will", "lets": "let us", "whats": "what is", "wouldnt": "would not", "ill": "i will",
         "isnt": "is not", "didnt": "did not", "theyre": "they are", "thats": "that is"}


def norm(text: str):
    words = re.sub(r"[^a-z0-9 ]", "", text.lower().replace("'", "").replace("’", "")).split()
    return " ".join(_LONG.get(w, w) for w in words).split()


GHOST = []
if os.path.exists(_lines_path):
    for ln in json.load(open(_lines_path, encoding="utf-8")):
        GHOST.append((ln["speaker"], ln["text"], norm(ln["text"])))
print(f"{len(GHOST)} ghost lines loaded", flush=True)
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


MULTI = not MODEL.endswith(".en")   # multilingual model: other languages are translated to English (Kaalob 2026-10-10, Russian / Ukrainian player)
LANG_NAMES = {"ru": "Russian", "uk": "Ukrainian", "es": "Spanish", "de": "German", "fr": "French", "pt": "Portuguese", "pl": "Polish",
              "it": "Italian", "tr": "Turkish", "nl": "Dutch", "sv": "Swedish", "ja": "Japanese", "ko": "Korean", "zh": "Chinese",
              "ar": "Arabic", "cs": "Czech", "ro": "Romanian", "hu": "Hungarian", "fi": "Finnish", "no": "Norwegian", "da": "Danish",
              "be": "Belarusian", "kk": "Kazakh", "bg": "Bulgarian", "sr": "Serbian", "hr": "Croatian", "el": "Greek", "he": "Hebrew",
              "hi": "Hindi", "id": "Indonesian", "vi": "Vietnamese", "th": "Thai"}
LANG_MIN_PROB = 0.7   # short noisy clips can look like a random language; below this they are treated as English


def _run(audio, language, task, prompt):
    return model.transcribe(audio, language=language, task=task, beam_size=1, best_of=1, temperature=0.0,
                            condition_on_previous_text=False, without_timestamps=True, initial_prompt=prompt,
                            vad_filter=True, vad_parameters={"min_silence_duration_ms": 250, "speech_pad_ms": 150, "threshold": 0.35},
                            no_speech_threshold=0.5, log_prob_threshold=-1.0)


def transcribe(audio: np.ndarray) -> str:
    if not MULTI:
        segs, _ = _run(audio, "en", "transcribe", NAMES)
        return " ".join(s.text.strip() for s in segs).strip()
    # auto-detect the language; anything not English is translated to English in the same pass
    segs, info = _run(audio, None, "translate", None)
    segs = list(segs)
    lang, prob = info.language, info.language_probability
    if lang != "en" and prob >= LANG_MIN_PROB:
        text = " ".join(s.text.strip() for s in segs).strip()
        return f"[{LANG_NAMES.get(lang, lang)}] {text}" if text else ""
    if lang == "en":
        return " ".join(s.text.strip() for s in segs).strip()
    segs, _ = _run(audio, "en", "transcribe", NAMES)   # unsure about the language: plain English captioning
    return " ".join(s.text.strip() for s in segs).strip()


BANNED = {"thank you.", "thanks for watching.", "you", "bye.", "thank you for watching."}   # whisper's silence hallucinations
# Whisper invents these YouTube endings from noise; removed anywhere in a line, whatever the punctuation
# (it always came as "Thanks for watching!" with "!", which the exact-match list above missed - Kaalob 2026-10-10)
FAKE = re.compile(r"\b(thanks?( you)? (so much )?for watching|please (like and )?subscribe|see you (in the )?next (time|video)|"
                  r"don'?t forget to (like and )?subscribe|subtitles? by [^.!?]*|amara\.org[^.!?]*)\b[.!?]*", re.I)


def clean(text: str) -> str:
    text = FAKE.sub("", text).strip(" ,.-")
    norm = re.sub(r"[^a-z ]", "", text.lower()).strip()
    if norm in {"thank you", "thanks", "you", "bye", ""}:
        return ""
    return text


def handle(conn: socket.socket):
    print("overlay connected", flush=True)
    conn.settimeout(5)
    buf = np.zeros(0, dtype=np.float32)
    utter = []            # (hop audio, rms) of the utterance being collected
    in_speech = False
    silence = 0.0         # seconds below the gate since the last speech hop
    spoken = 0.0          # seconds above the gate in the current utterance
    stream_t = 0.0        # stream time in seconds
    HOP = 0.1
    hop_n = int(SR * HOP)
    pending = b""
    lock = threading.Lock()
    recent = []           # last few seconds of hop RMS, for the adaptive gate
    PAUSE = 0.45          # a gap this long ends an utterance
    MAX = 4.5             # longer than this: cut at the quietest recent spot and caption what we have

    def gate():
        # game ambience sets the floor; speech must clear it by a margin
        if len(recent) < 10:
            return 0.008
        floor = float(np.percentile(recent, 20))
        return max(0.006, floor * 2.2)

    def emit(audio, reason):
        start = stream_t - len(audio) / SR
        t = time.time()
        try:
            text = transcribe(audio)
        except Exception as e:
            print("transcribe error:", e, flush=True)
            return
        ms = int((time.time() - t) * 1000)
        text = clean(text)
        if not text or text.lower() in BANNED:
            return
        g = match_ghost(text)
        if g:
            text = f"GHOST ({g[0]}): {g[1]}" if g[0] not in ("Ghost", "Ghost event") else f"GHOST: {g[1]}"
        msg = json.dumps({"text": text, "t0": round(start, 2), "dur": round(len(audio) / SR, 2), "ms": ms}) + "\n"
        print(f"[{ms} ms, {len(audio)/SR:.1f}s, {reason}] {text}", flush=True)
        try:
            with lock:
                conn.sendall(msg.encode("utf-8"))
        except OSError:
            pass

    try:
        while True:
            try:
                data = conn.recv(8192)
            except socket.timeout:
                continue
            if not data:
                break
            pending += data
            n = (len(pending) // 2) * 2
            if n == 0:
                continue
            samples = np.frombuffer(pending[:n], dtype=np.int16).astype(np.float32) / 32768.0
            pending = pending[n:]
            buf = np.concatenate([buf, samples])
            while len(buf) >= hop_n:
                hop, buf = buf[:hop_n], buf[hop_n:]
                stream_t += HOP
                rms = float(np.sqrt(np.mean(hop * hop)))
                recent.append(rms)
                if len(recent) > 50:
                    recent.pop(0)
                g = gate()
                if rms > g:
                    if not in_speech:
                        in_speech = True
                    utter.append((hop, rms))
                    spoken += HOP
                    silence = 0.0
                else:
                    if in_speech:
                        utter.append((hop, rms))          # keep a little tail
                        silence += HOP
                        if silence >= PAUSE:
                            in_speech = False
                            silence = 0.0
                            snapshot = list(utter); utter = []; sp = spoken; spoken = 0.0
                            def run(snap=snapshot, sp=sp):
                                nonlocal utter
                                if sp < 0.35:
                                    return
                                audio = np.concatenate([a for a, _ in snap])
                                emit(audio, "pause")
                            threading.Thread(target=run, daemon=True).start()
                if in_speech and spoken >= MAX:
                    # cut at the quietest hop within the last 1.5 s so words are not chopped
                    tail = utter[-15:]
                    k = len(utter) - 15 + int(np.argmin([r for _, r in tail]))
                    part, utter = utter[:k], utter[k:]
                    spoken = sum(HOP for _, r in utter if r > g)
                    audio = np.concatenate([a for a, _ in part])
                    threading.Thread(target=emit, args=(audio, "long"), daemon=True).start()
    finally:
        conn.close()
        print("overlay disconnected", flush=True)


srv = socket.socket(socket.AF_INET, socket.SOCK_STREAM)
# No SO_REUSEADDR: on Windows it lets a second server silently bind the same port, and the
# overlay can then end up talking to a stale process. Fail loudly instead.
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
