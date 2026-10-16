"""Audio evidence finder for The Other Side.

The ghost answers on the Spirit Box / Infrasound Receiver with one of ~146 recorded clips (list read from the
game's DA_Evidence_SpiritBox / BP_Evidence_* assets, see evidence_clips.txt). The game plays them through a
MetaSound with a random pitch shift (Wave Player "Pitch Shift" = playback speed), bitcrusher, high/low pass
and Spirit Box static, so Whisper often misses them and never tags one-word answers ("Yes", "Run", a grunt).

This matches the game audio against those clips directly: log-frequency spectrogram, static floor removed,
cosine similarity at every time lag against each clip resampled to -4..+4 semitones.

    ev = EvidenceMatcher(clips_dir)        # loads templates
    ev.feed(samples_16k_float32)           # call with every new block of game audio
    for hit in ev.poll(): ...              # dicts {speaker, text, file, score, t_end}
"""
import os
import re
import json
import threading

import numpy as np
import soundfile as sf

SR = 16000
HOP = 320                 # 20 ms frames
WIN = int(os.environ.get("EV_WIN", 640))
F_LO, F_HI, NB = 250.0, 3800.0, int(os.environ.get("EV_NB", 40))   # log-spaced bands
MAX_T = 2.4               # seconds of each clip used as its template (from where it gets loud)
MIN_T = 0.35
SEMIS = [float(s) for s in os.environ.get("EV_SEMIS", "-3.5,-3.25,-3,-2.75,-2.5,-2.25,-2").split(",")]   # real game audio (wiki recordings): ghost answers always play 2-3 semitones LOW
BUF_S = 7.0
THRESH = 0.62             # tuned by test_evidence.py

_here = os.path.dirname(os.path.abspath(__file__))

_win = np.hanning(WIN).astype(np.float32)
_freqs = np.fft.rfftfreq(WIN, 1 / SR)
_edges = np.geomspace(F_LO, F_HI, NB + 2)
_fb = np.zeros((NB, len(_freqs)), np.float32)
for i in range(NB):
    lo, c, hi = _edges[i], _edges[i + 1], _edges[i + 2]
    up = (_freqs - lo) / (c - lo)
    dn = (hi - _freqs) / (hi - c)
    _fb[i] = np.clip(np.minimum(up, dn), 0, None)
_fb /= _fb.sum(1, keepdims=True) + 1e-9


def logspec(x):
    """(frames, NB) log band energies."""
    if len(x) < WIN:
        x = np.pad(x, (0, WIN - len(x)))
    n = 1 + (len(x) - WIN) // HOP
    idx = np.arange(WIN)[None, :] + HOP * np.arange(n)[:, None]
    fr = x[idx] * _win
    p = np.abs(np.fft.rfft(fr, axis=1)) ** 2
    return np.log10(p @ _fb.T + 1e-10).astype(np.float32)


def clean(S, floor=None):
    """Per frame: which bands are within 20 dB of that frame's loudest band (the spectral shape, independent of
    level, filters and how loud the static is), mean-removed so it is signed, weighted by how far the frame rises
    above the steady background (floor = a quiet frame's level; static and room hum sit there)."""
    top = S.max(1, keepdims=True)
    if floor is None:
        floor = np.percentile(top, 15)
    X = np.clip(S - top + 2.0, 0, None)
    k = np.array([0.25, 0.5, 0.25], np.float32)
    X = np.apply_along_axis(lambda c: np.convolve(c, k, mode="same"), 0, X)
    Z = X - X.mean(1, keepdims=True)
    Z = Z / (np.sqrt((Z * Z).mean(1, keepdims=True)) + 1e-3)
    w = np.clip((top - floor - 0.3) / 0.7, 0, 1)
    return (Z * w).astype(np.float32)


def template_floor(S):
    """A clean clip has ~no floor; pretend one 25 dB under its loudest moment."""
    return S.max() - 2.5


def resample(x, factor):
    """Play x 'factor' times faster (pitch up when > 1)."""
    n = int(len(x) / factor)
    return np.interp(np.arange(n) * factor, np.arange(len(x)), x).astype(np.float32)


def load(path):
    a, sr = sf.read(path, dtype="float32", always_2d=True)
    a = a.mean(1)
    if sr != SR:
        a = resample(a, sr / SR)
    return a


def describe(asset):
    """(speaker, text) for a response asset path like /Game/Audio/Ghosts/Orphan/SFX_SpiritBox_Orphan_IAmHere_03."""
    name = asset.rsplit("/", 1)[-1]
    if "BansheeScream" in name:
        return "Banshee", "(Banshee scream)"
    if "Skia_Hunting" in name:
        return "Skia", "(Skia crying)"
    if "/Responses/Skia/" in asset:
        who = "Skia"
    elif "/Responses/" in asset:
        who = "Ghost"
    else:
        who = asset.split("/Ghosts/")[1].split("/")[0]
    n = re.sub(r"^SFX_Spirit[Bb]ox_(Hans|McGavin|Orphan)_?|^SFX_Skia_|^GhostAudio_|_AlesiaDavina$|_\d+$", "", name)
    n = re.sub(r"_\d+$", "", n)
    if re.match(r"(Action_)?Laugh", n):
        return who, "(laughing)"
    if n.startswith("Grunt"):
        return who, "(grunt)"
    m = re.match(r"Action_([A-Za-z]+)", n)
    if m:
        # wiki recordings: Hungry = breathing, the others = whispery gibberish (Cleanse audio level 1)
        return who, "(breathing)" if m.group(1) == "Hungry" else "(gibberish)"
    n = re.sub(r"\d+$", "", n)
    n = re.sub(r"(?<=[a-z])(?=[A-Z])|(?<=[A-Z])(?=[A-Z][a-z])", " ", n).replace("_", " ").strip()
    return who, n[:1].upper() + n[1:]


class EvidenceMatcher:
    def __init__(self, clips_dir, list_path=os.path.join(_here, "evidence_clips.txt"), thresh=THRESH):
        self.thresh = thresh
        known = {}
        lp = os.path.join(_here, "ghost_lines.json")
        if os.path.exists(lp):
            for ln in json.load(open(lp, encoding="utf-8")):
                known[ln["file"].replace("\\", "/").lower()] = ln["text"]
        self.meta, temps, tE, act = [], [], [], []
        for asset in open(list_path, encoding="utf-8").read().split():
            rel = asset.replace("/Game/", "") + ".wav"
            path = os.path.join(clips_dir, rel)
            if not os.path.exists(path):
                continue
            who, text = describe(asset)
            text = known.get(rel.lower(), text)
            a = load(path)
            e = np.convolve(a * a, np.ones(320) / 320, mode="same")
            loud = np.where(e > e.max() * 0.01)[0]
            if len(loud) == 0:
                continue
            a = a[loud[0]: loud[-1] + 1]
            for s in SEMIS:
                b = resample(a, 2 ** (s / 12))[: int(MAX_T * SR)]
                if len(b) < MIN_T * SR:
                    continue
                Sb = logspec(b)
                T = clean(Sb, template_floor(Sb))
                nrm = np.linalg.norm(T)
                if nrm < 1e-3:
                    continue
                act.append(np.abs(T).sum(1) > 0.5 * np.abs(T).sum(1).max())
                temps.append(T / nrm)
                tE.append(nrm * nrm)
                self.meta.append((who, text, rel, s))
        self.L = max(t.shape[0] for t in temps)
        self.nfr = int(BUF_S * SR / HOP)
        self.nfft = 1 << int(np.ceil(np.log2(self.nfr + self.L)))
        # templates zero-padded at the END to L frames; matching uses only lags where the whole real template fits
        self.lens = np.array([t.shape[0] for t in temps])
        self.tE = np.array(tE)
        self.act = act
        P = np.zeros((len(temps), self.L, NB), np.float32)
        for i, t in enumerate(temps):
            P[i, : t.shape[0]] = t
        # correlation via FFT along time, summed over bands
        self.TF = np.conj(np.fft.rfft(P, n=self.nfft, axis=1))     # (K, F, NB)
        self.audio = np.zeros(0, np.float32)
        self.t = 0.0           # stream time (s) of the end of self.audio
        self.reported = []     # (t_start, rel)
        self.lock = threading.Lock()
        self.files = len({m[2] for m in self.meta})

        self.mu = np.zeros(len(self.meta))
        self.sd = np.ones(len(self.meta))
        cal = os.path.join(_here, "evidence_calib.npz")
        if os.path.exists(cal):
            z = np.load(cal)
            if len(z["mu"]) == len(self.meta):
                self.mu, self.sd = z["mu"], z["sd"]

    def calibrate(self, buffers, save=True):
        """Each template's score on audio that is NOT a ghost answer (other game sounds, static, voices):
        short templates like "No" match random sounds better, so every score is judged against its own
        normal range (z = how many spreads above its usual best)."""
        R = np.array([[s for s, _, _ in sorted(self.scores(b, raw=True), key=lambda r: r[1])] for b in buffers])
        self.mu = R.mean(0)
        self.sd = np.maximum(R.std(0), 0.02)
        if save:
            np.savez(os.path.join(_here, "evidence_calib.npz"), mu=self.mu, sd=self.sd)

    def feed(self, x):
        with self.lock:
            self.audio = np.concatenate([self.audio, x])[-int(BUF_S * SR):]
            self.t += len(x) / SR

    def scores(self, audio, raw=False):
        """Best (score, template index, start frame) over all templates for this audio."""
        S = logspec(audio)
        X = clean(S)
        n = X.shape[0]
        XF = np.fft.rfft(X, n=self.nfft, axis=0)                    # (F, NB)
        C = np.fft.irfft(np.einsum("kfb,fb->kf", self.TF, XF, optimize=True), n=self.nfft, axis=1)[:, :n]
        # buffer energy under each template window, for cosine normalization
        e = np.concatenate([[0], np.cumsum((X.astype(np.float64) ** 2).sum(1))])
        live = np.abs(X).sum(1)
        live = live > 0.25 * max(live.max(), 1e-6)
        out = []
        for k in range(len(self.meta)):
            m = self.lens[k]
            if m > n:
                continue
            c = C[k, : n - m + 1]
            # a window needs real sound in it (not FFT noise over silence): at least ~1/4 of the template's
            w = np.sqrt(np.maximum(e[m:] - e[: n - m + 1], 0.25 * self.tE[k]))
            s = c / w
            j = int(np.argmax(s))
            # the sound has to last like the line does: a footstep or click can match one moment of a line,
            # but most of the line's loud frames must have sound under them
            a = self.act[k]
            if not raw and live[j:j + m][a].mean() < 0.75:
                continue
            out.append((float(s[j]) if raw else float((s[j] - self.mu[k]) / self.sd[k]), k, j))
        return out

    def poll(self):
        with self.lock:
            audio = self.audio.copy()
            t_end = self.t
        if len(audio) < SR:
            return []
        res = self.scores(audio)
        if not res:
            return []
        best = max(res)
        score, k, j = best
        if score < self.thresh:
            return []
        who, text, rel, semis = self.meta[k]
        t0 = t_end - len(audio) / SR + j * HOP / SR
        self.reported = [r for r in self.reported if t_end - r[0] < 15]
        if any(abs(r[0] - t0) < 1.5 for r in self.reported):
            return []
        self.reported.append((t0, rel))
        return [{"speaker": who, "text": text, "file": rel, "score": round(score, 3), "semis": semis,
                 "t0": round(t0, 2)}]


def level(text):
    """Cleanse audio level: 1 = sounds (breathing, gibberish, laughing), 2 = single words, 3 = phrases."""
    if text.startswith("("):
        return 1
    return 2 if len(re.findall(r"[A-Za-z']+", text)) <= 2 or text.lower().startswith("run") and "away" not in text.lower() else 3
