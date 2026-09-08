#!/usr/bin/env python3
"""Probe SenseVoiceSmall ONNX: parse am.mvn (kaldi nnet text), tokens.json, and run a real
forward pass with kaldi-native-fbank features to determine language id / textnorm / CTC
decode behaviour and generate a reference feature file for the C# tests.

Usage: python probe_sensevoice.py <model_dir> <wav>
"""
import json
import os
import re
import sys
import wave

import numpy as np

ROOT = os.path.dirname(os.path.abspath(__file__))
os.environ.pop("SSL_CERT_FILE", None)
os.environ.pop("SSL_CERT_DIR", None)
_tmp = os.path.join(ROOT, ".tmp")
os.makedirs(_tmp, exist_ok=True)
for _k in ("TMP", "TEMP", "TMPDIR"):
    os.environ[_k] = _tmp

d = sys.argv[1] if len(sys.argv) > 1 else os.path.join(ROOT, "out", "sensevoice-small-int8")
wav_path = sys.argv[2] if len(sys.argv) > 2 else os.path.join(ROOT, "..", "..", "testdata", "speech_ja.wav")

with wave.open(wav_path, "rb") as w:
    assert w.getframerate() == 16000 and w.getnchannels() == 1
    pcm = np.frombuffer(w.readframes(w.getnframes()), dtype=np.int16).astype(np.float32) / 32768.0
print(f"audio {len(pcm)/16000:.2f}s")

# ---- parse the kaldi nnet text (am.mvn) -----------------------------------
am = open(os.path.join(d, "am.mvn"), encoding="utf-8").read()
print("--- am.mvn head ---")
print("\n".join(am.splitlines()[:8]))


def parse_component(text, comp):
    m = re.search(rf"<{comp}>\s+\d+\s+\d+\s*(?:<[^>]+>\s+[^\s]+\s*)*\[(.*?)\]", text, re.S)
    if not m:
        return None
    nums = [float(x) for x in re.findall(r"[-+]?[0-9]*\.?[0-9]+[eE][-+]?[0-9]+|[-+]?\d+\.\d+", m.group(1))]
    return np.array(nums, dtype=np.float32)


shift = parse_component(am, "AddShift")
rescale = parse_component(am, "Rescale")
print(f"AddShift: {None if shift is None else shift.shape} mean={None if shift is None else shift.mean():.4g}")
print(f"Rescale: {None if rescale is None else rescale.shape} mean={None if rescale is None else rescale.mean():.4g}")
assert shift is not None and rescale is not None and shift.size == 560
# kaldi AddShift: y = x + shift ; Rescale: y = x * scale. funasr cmvn = (x - mean)*istd
mean = -shift
istd = rescale
with open(os.path.join(d, "sensevoice_meta.json"), "w", encoding="utf-8") as f:
    json.dump({
        "fs": 16000, "n_mels": 80, "window": "hamming",
        "frame_length_ms": 25, "frame_shift_ms": 10,
        "lfr_m": 7, "lfr_n": 6,
        "cmvn_mean": mean.tolist(), "cmvn_istd": istd.tolist(),
        "cmvn_dim": 560,
    }, f, ensure_ascii=False)
print("sensevoice_meta.json rewritten (560-dim mean/istd)")

# ---- tokens.json -----------------------------------------------------------
tokens = json.load(open(os.path.join(d, "tokens.json"), encoding="utf-8"))
if isinstance(tokens, dict):
    tokens_list = [tokens.get(str(i), f"<{i}>") for i in range(max(int(k) for k in tokens) + 1)]
else:
    tokens_list = list(tokens)
print(f"tokens.json entries: {len(tokens_list)}  head: {tokens_list[:5]}  tail: {tokens_list[-3:]}")
TOK = lambda i: tokens_list[i] if 0 <= i < len(tokens_list) else f"<{i}>"

# ---- kaldi-native-fbank features -------------------------------------------
import kaldi_native_fbank as knf  # noqa: E402

opts = knf.FbankOptions()
opts.frame_opts.dither = 0.0
opts.frame_opts.snip_edges = True
opts.frame_opts.window_type = "hamming"
opts.frame_opts.samp_freq = 16000
opts.frame_opts.frame_shift_ms = 10
opts.frame_opts.frame_length_ms = 25
opts.mel_opts.num_bins = 80
opts.mel_opts.low_freq = 20
opts.mel_opts.high_freq = 0
try:
    opts.use_log_fbank = True  # newer API surface
except Exception:
    pass  # knf OnlineFbank already emits log-mel by default
# print control tokens (rich transcription markers) and their ids
ctl = [(i, t) for i, t in enumerate(tokens_list) if t.startswith("<|")]
print(f"control tokens ({len(ctl)}):", ctl[:12], ctl[-6:])
fbank = knf.OnlineFbank(opts)
fbank.accept_waveform(16000, pcm)
fbank.input_finished()
T = fbank.num_frames_ready
feats = np.array([fbank.get_frame(i) for i in range(T)], dtype=np.float32)
print(f"fbank: ({T}, {feats.shape[1]})  range [{feats.min():.3f}, {feats.max():.3f}]")

# LFR 7/6
m, n = 7, 6
idx = np.arange(0, T - m + 1, n)
lfr = np.stack([feats[i:i + m].reshape(-1) for i in idx])  # (T_lfr, 560)
print(f"LFR: {lfr.shape}")
# CMVN on 560-dim
cmvn = (lfr + shift) * rescale  # kaldi components applied in order
print(f"cmvn: range [{cmvn.min():.3f}, {cmvn.max():.3f}]")

# save reference features for the C# test
ref_dir = os.path.join(ROOT, "testrefs")
os.makedirs(ref_dir, exist_ok=True)
base = os.path.splitext(os.path.basename(wav_path))[0]
np.save(os.path.join(ref_dir, f"kaldi_{base}.npy"), feats)
np.save(os.path.join(ref_dir, f"kaldi_{base}_lfr_cmvn.npy"), cmvn)
print(f"reference features saved to {ref_dir}")

# ---- run the ONNX ----------------------------------------------------------
import onnxruntime as ort  # noqa: E402

sess = ort.InferenceSession(os.path.join(d, "model_quant.onnx"), providers=["CPUExecutionProvider"])
for lang in range(5):
    for textnorm in (0, 1):
        out = sess.run(None, {
            "speech": cmvn[np.newaxis, :, :].astype(np.float32),
            "speech_lengths": np.array([cmvn.shape[0]], dtype=np.int32),
            "language": np.array([lang], dtype=np.int32),
            "textnorm": np.array([textnorm], dtype=np.int32),
        })
        logits, lens = out
        L = lens[0]
        ids = np.argmax(logits[0, :L], axis=-1)
        # CTC greedy: collapse repeats + drop blank (blank id?)
        collapsed = []
        prev = -1
        for i in ids:
            if i != prev:
                collapsed.append(int(i))
                prev = i
        text = "".join(TOK(t) for t in collapsed)
        print(f"lang={lang} tn={textnorm} lens={lens} len={L} ids[:40]={collapsed[:40]}")
        print(f"  text: {text[:120]!r}")
