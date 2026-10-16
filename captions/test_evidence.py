"""Score check for evidence.py: ghost answers through a fake Spirit Box chain (random pitch, bitcrush, band-pass,
static, game ambience) must score high; other game sounds, static alone and normal voices saying the SAME words
must score low.  python test_evidence.py <gameclips dir> [tts dir]"""
import glob
import os
import sys

import numpy as np

from evidence import SR, BUF_S, EvidenceMatcher, load, resample

clips = sys.argv[1]
tts_dir = sys.argv[2] if len(sys.argv) > 2 else None
rng = np.random.default_rng(1)
ev = EvidenceMatcher(clips, thresh=0)
resp = sorted({m[2] for m in ev.meta})
noise = load(os.path.join(clips, "Core/Equipment/Audio/SpirtBox/Audio/SpiritBoxNoise.wav"))
others = [f for f in glob.glob(os.path.join(clips, "**/*.wav"), recursive=True)
          if f.replace("\\", "/").split(clips.rstrip("/") + "/")[-1] not in resp and "SpiritBoxNoise" not in f]
N = int(BUF_S * SR)


def rms(x):
    return float(np.sqrt(np.mean(x * x)) + 1e-9)


def one_pole(x, fc, high):
    a = np.exp(-2 * np.pi * fc / SR)
    y = np.zeros_like(x)
    p = 0.0
    for i in range(len(x)):          # small clips, fine
        p = (1 - a) * x[i] + a * p
        y[i] = p
    return x - y if high else y


def spirit_box(x):
    x = resample(x, 2 ** (rng.uniform(-3.5, 3.5) / 12))
    bits = rng.integers(5, 9)
    q = 2 ** (bits - 1)
    x = np.round(x / (np.abs(x).max() + 1e-9) * q) / q
    hold = int(rng.integers(1, 4))                    # sample-rate reduction
    x = np.repeat(x[::hold], hold)[: len(x)]
    x = one_pole(one_pole(x, 300, True), 3200, False)
    return x.astype(np.float32)


def bed(n):
    """game ambience: a random other clip, quiet"""
    a = load(rng.choice(others))
    a = np.tile(a, n // max(len(a), 1) + 1)[:n] if len(a) else np.zeros(n, np.float32)
    return a / rms(a) * 0.01


def place(sig, snr_static):
    out = bed(N)
    st = np.tile(noise, N // len(noise) + 1)[:N]
    out += st / rms(st) * rms(sig) / (10 ** (snr_static / 20))
    sig = sig[: N - SR]
    p = int(rng.integers(0, N - len(sig)))
    out[p:p + len(sig)] += sig
    return out


def best(audio):
    s = ev.scores(audio)
    return max(s) if s else (-9.0, 0, 0)


# calibration on half of the other clips (+ static), test on the other half
rng.shuffle(others)
cal_clips, others = others[: len(others) // 2], others[len(others) // 2:]
def cal_buf(i):
    if i % 5 == 0:
        st = np.tile(noise, N // len(noise) + 1)[:N]
        return st * rng.uniform(0.02, 0.3)
    a = load(cal_clips[i % len(cal_clips)])
    out = np.zeros(N, np.float32)
    a = a[: N - SR]
    if len(a):
        p = int(rng.integers(0, N - len(a)))
        out[p:p + len(a)] += a / rms(a) * 0.1
    st = np.tile(noise, N // len(noise) + 1)[:N]
    return out + st / rms(st) * 0.1 * rng.uniform(0, 0.5)
ev.calibrate([cal_buf(i) for i in range(int(os.environ.get("NCAL", 200)))])
print("calibrated")
pos, neg, wrong = [], [], 0
for rel in resp:
    for snr in (6, 0):
        a = spirit_box(load(os.path.join(clips, rel)))
        sc, k, _ = best(place(a, snr))
        pos.append(sc)
        if ev.meta[k][2] != rel and ev.meta[k][1] != dict((m[2], m[1]) for m in ev.meta)[rel]:
            wrong += 1
print(f"positives {len(pos)}: min {min(pos):.3f}  p5 {np.percentile(pos, 5):.3f}  median {np.median(pos):.3f}  wrong clip {wrong}")

for f in rng.choice(others, min(250, len(others)), replace=False):
    a = load(f)
    if len(a) < 0.3 * SR:
        continue
    b = best(place(a[: N - SR], 6))
    neg.append((b[0], os.path.basename(f) + " -> " + ev.meta[b[1]][1]))
for _ in range(20):
    st = np.tile(noise, N // len(noise) + 1)[:N]
    neg.append((best(bed(N) + st * rng.uniform(0.02, 0.3))[0], "static only"))
if tts_dir:
    for f in glob.glob(os.path.join(tts_dir, "*.wav")):
        a = load(f)
        neg.append((best(place(a, 30))[0], "tts " + os.path.basename(f)))
        neg.append((best(place(spirit_box(a), 3))[0], "tts+box " + os.path.basename(f)))
neg.sort(reverse=True)
print(f"negatives {len(neg)}: max {neg[0][0]:.3f}  p95 {np.percentile([n for n, _ in neg], 95):.3f}")
for s, n in neg[:12]:
    print(f"  {s:.3f} {n}")
R = [ev.scores(cal_buf(i), raw=True) for i in range(0)]
for t in (2, 2.5, 3, 3.5, 4, 4.5, 5, 6):
    print(f"thresh {t}: catch {np.mean(np.array(pos) >= t):.0%}  false {sum(1 for s, _ in neg if s >= t)}/{len(neg)}")
