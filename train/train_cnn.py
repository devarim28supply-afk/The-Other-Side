"""Trains PhasmoSound's own sound recognizer straight from audio (log-mel CNN), on the game's files.

Every batch is mixed fresh: a game sound placed at the end of the window the way the overlay hears
it live (often cut short), random loudness, wall muffling, room echo and the game's own ambience
underneath. Exports app-ready head.onnx (16 kHz mono waveform in, one probability per label out)
and head_labels.json.
Usage: python train_cnn.py <data dir> <out dir> [steps]
"""
import json, sys, time
import numpy as np, torch, torch.nn as nn, torch.nn.functional as F

data, out_dir = sys.argv[1], sys.argv[2]
STEPS = int(sys.argv[3]) if len(sys.argv) > 3 else 4000
torch.set_num_threads(8)
SR = 16000

clips = json.load(open(f"{data}/clips.json"))
raw = np.memmap(f"{data}/clips.bin", dtype=np.int16, mode="r")
waves = [raw[c["Offset"]: c["Offset"] + c["Length"]].astype(np.float32) / 32768 for c in clips]
labels = sorted({c["Label"] for c in clips}); li = {l: i for i, l in enumerate(labels)}
by_label = {l: [i for i, c in enumerate(clips) if c["Label"] == l] for l in labels}
beds = [w for c, w in zip(clips, waves) if c["Label"] == "_background" and len(w) > SR * 2]
print(f"{len(clips)} clips, {len(labels)} labels, {len(beds)} noise beds")


def peak(w): return np.abs(w).max() + 1e-9
def rms(w): return np.sqrt(np.mean(w * w)) + 1e-9


def lowpass(w, rng):
    fc = 700 + rng.random() * 5000
    S = np.fft.rfft(w); f = np.fft.rfftfreq(len(w), 1 / SR)
    return np.fft.irfft(S / (1 + (f / fc) ** 4), len(w)).astype(np.float32)


def reverb(w, rng):
    rt = 0.2 + rng.random() * 0.9; n = int(min(rt, 0.8) * SR)
    ir = rng.standard_normal(n).astype(np.float32) * np.exp(-6.9 * np.arange(n) / (rt * SR)).astype(np.float32)
    ir[0] = 1.0; ir /= np.sqrt(np.sum(ir ** 2))
    m = len(w) + n - 1; size = 1 << (m - 1).bit_length()
    wet = np.fft.irfft(np.fft.rfft(w, size) * np.fft.rfft(ir, size), size)[: len(w)].astype(np.float32)
    mix = 0.15 + rng.random() * 0.45
    return (1 - mix) * w + mix * wet * (rms(w) / rms(wet))


def bed(L, rng):
    b = beds[rng.integers(len(beds))]
    s = rng.integers(0, len(b) - L) if len(b) > L else 0
    out = np.zeros(L, np.float32); seg = b[s: s + L]; out[: len(seg)] = seg
    return out


def make(idx, L, rng):
    lab = clips[idx]["Label"]; w = waves[idx]
    if lab == "_background":
        x = bed(L, rng) if rng.random() < 0.5 or len(w) < L else w[(s := rng.integers(0, len(w) - L + 1)): s + L].copy()
        return x * (10 ** ((-15 - rng.random() * 40) / 20) / peak(x))
    e = w.copy()
    if rng.random() < 0.4: e = lowpass(e, rng)
    if rng.random() < 0.6: e = reverb(np.concatenate([e, np.zeros(SR // 2, np.float32)]), rng)
    visible = int(4000 + rng.random() * (L + 8000))
    x = np.zeros(L, np.float32); start = L - visible
    a, b = max(0, start), min(L, start + len(e))
    if b > a: x[a:b] = e[a - start: b - start]
    if peak(x) < 1e-4: x[:] = e[:L] if len(e) >= L else np.pad(e, (0, L - len(e)))
    x *= 10 ** ((-3 - rng.random() * 32) / 20) / peak(x)
    if rng.random() < 0.85:
        n = bed(L, rng); snr = -5 + rng.random() * 30
        x += n * (rms(x) / rms(n)) * 10 ** (-snr / 20)
    return np.clip(x, -1, 1)


def batch(rng, B=64):
    L = 15600 if rng.random() < 0.5 else 24000
    ys = rng.integers(0, len(labels), B)                       # every label equally often
    idx = [by_label[labels[y]][rng.integers(len(by_label[labels[y]]))] for y in ys]
    return torch.tensor(np.stack([make(i, L, rng) for i in idx])), torch.tensor(ys)


class LogMel(nn.Module):
    """STFT as a fixed convolution so the whole thing exports to plain ONNX."""
    def __init__(self, n_fft=400, hop=160, n_mels=64):
        super().__init__()
        win = np.hanning(n_fft + 1)[:-1]; k = np.arange(n_fft // 2 + 1)[:, None]; t = np.arange(n_fft)[None]
        basis = np.concatenate([np.cos(2 * np.pi * k * t / n_fft) * win, -np.sin(2 * np.pi * k * t / n_fft) * win])
        self.register_buffer("basis", torch.tensor(basis[:, None, :], dtype=torch.float32))
        hz = lambda m: 700 * (10 ** (m / 2595) - 1); mel = lambda f: 2595 * np.log10(1 + f / 700)
        pts = hz(np.linspace(mel(60), mel(7800), n_mels + 2)); bins = np.fft.rfftfreq(n_fft, 1 / SR)
        fb = np.zeros((n_mels, len(bins)))
        for i in range(n_mels):
            l, c, r = pts[i: i + 3]
            fb[i] = np.clip(np.minimum((bins - l) / (c - l), (r - bins) / (r - c)), 0, None)
        self.register_buffer("fb", torch.tensor(fb, dtype=torch.float32))
        self.hop, self.half = hop, n_fft // 2 + 1

    def forward(self, w):                                       # [B, N] -> [B, 1, mels, frames]
        s = F.conv1d(w.unsqueeze(1), self.basis, stride=self.hop)
        p = s[:, : self.half] ** 2 + s[:, self.half:] ** 2
        m = torch.log(torch.matmul(self.fb, p) + 1e-6)
        return (m - m.mean(dim=(1, 2), keepdim=True)).unsqueeze(1)   # loudness-independent


def block(i, o): return nn.Sequential(nn.Conv2d(i, o, 3, padding=1), nn.BatchNorm2d(o), nn.ReLU(), nn.Conv2d(o, o, 3, padding=1), nn.BatchNorm2d(o), nn.ReLU(), nn.MaxPool2d(2))


class Net(nn.Module):
    def __init__(self, c):
        super().__init__()
        self.mel = LogMel(); self.cnn = nn.Sequential(block(1, 32), block(32, 64), block(64, 128), block(128, 160))
        self.head = nn.Sequential(nn.Dropout(0.3), nn.Linear(320, c))

    def forward(self, w):
        h = self.cnn(self.mel(w)).mean(2)                        # [B, C, frames]
        return self.head(torch.cat([h.mean(2), h.amax(2)], 1))


# fixed test set: the same game sounds with noise/echo/timing the net has not seen in this mix
trng = np.random.default_rng(999)
test = [batch(trng) for _ in range(40)]
net = Net(len(labels))
opt = torch.optim.AdamW(net.parameters(), lr=2e-3, weight_decay=1e-3)
sched = torch.optim.lr_scheduler.OneCycleLR(opt, 3e-3, total_steps=STEPS)
rng = np.random.default_rng(1); t0 = time.time()


def evaluate():
    net.eval(); P, Y = [], []
    with torch.no_grad():
        for x, y in test: P.append(torch.softmax(net(x), 1)); Y.append(y)
    net.train(); return torch.cat(P).numpy(), torch.cat(Y).numpy()


for step in range(1, STEPS + 1):
    x, y = batch(rng)
    loss = F.cross_entropy(net(x), y, label_smoothing=0.05)
    opt.zero_grad(); loss.backward(); opt.step(); sched.step()
    if step % 500 == 0 or step == STEPS:
        P, Y = evaluate()
        print(f"step {step}: loss {loss.item():.2f}, test acc {np.mean(P.argmax(1) == Y):.0%}, {time.time() - t0:.0f}s", flush=True)

P, Y = evaluate(); pred, conf = P.argmax(1), P.max(1)
hidden = [li[l] for l in labels if l.startswith("_")]
ev = ~np.isin(Y, hidden)
for t in (0.5, 0.6, 0.7, 0.8):
    shown = ev & (conf >= t) & ~np.isin(pred, hidden)
    fb = (~ev) & (conf >= t) & ~np.isin(pred, hidden)
    print(f"  conf>={t}: game sounds named {shown.sum() / ev.sum():.0%}, of those correct {(shown & (pred == Y)).sum() / max(1, shown.sum()):.0%}; background/equipment wrongly shown {fb.sum() / max(1, (~ev).sum()):.0%}")
print("  per label (recall, most confused with):")
for c, l in enumerate(labels):
    sel = Y == c
    if sel.sum():
        wr = np.bincount(pred[sel], minlength=len(labels)); wr[c] = 0
        print(f"    {l:<24} {np.mean(pred[sel] == c):4.0%}  {labels[wr.argmax()] if wr.sum() else '-'}")

net.eval()
real = json.load(open(f"{data}/real_waves.json"))
if real:
    print("\nYour own in-game recordings:")
    with torch.no_grad():
        for r in real:
            w = np.array(r["Wave"], np.float32)
            p = torch.softmax(net(torch.tensor(w[-24000:])[None]), 1)[0].numpy(); top = p.argsort()[::-1][:2]
            print(f"  {r['File']:<34} ({r['Label']:<10}) -> {labels[top[0]]} {p[top[0]]:.2f}, {labels[top[1]]} {p[top[1]]:.2f}")


class Export(nn.Module):
    def __init__(self, n): super().__init__(); self.n = n
    def forward(self, w): return torch.softmax(self.n(w), 1)


torch.onnx.export(Export(net), (torch.zeros(1, 24000),), f"{out_dir}/head.onnx", input_names=["waveform"], output_names=["probs"],
                  dynamic_axes={"waveform": {1: "n"}}, opset_version=17, dynamo=False)
json.dump(labels, open(f"{out_dir}/head_labels.json", "w"))
print(f"wrote {out_dir}/head.onnx")
