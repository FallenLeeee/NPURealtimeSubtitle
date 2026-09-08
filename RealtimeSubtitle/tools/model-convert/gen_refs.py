#!/usr/bin/env python3
"""Generate reference artifacts for the C# SenseVoice backend tests.

For each 16 kHz test wav: dumps the kaldi-native-fbank log-mel (T,80), the LFR-stacked +
CMVN-normalised features (T_lfr,560), and (when the ONNX model is present) the expected
decoded text. Also prints every knf option value used so the C# implementation can mirror
them exactly.

Usage: python gen_refs.py [model_dir]
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

model_dir = sys.argv[1] if len(sys.argv) > 1 else os.path.join(ROOT, "out", "sensevoice-small-int8")
ref_dir = os.path.join(ROOT, "testrefs")
os.makedirs(ref_dir, exist_ok=True)

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
    opts.use_log_fbank = True
except Exception:
    pass

print("=== knf options in effect ===")
print("frame_opts:", {k: getattr(opts.frame_opts, k) for k in dir(opts.frame_opts) if not k.startswith("_")})
print("mel_opts :", {k: getattr(opts.mel_opts, k) for k in dir(opts.mel_opts) if not k.startswith("_")})

# CMVN components from the kaldi nnet text
am = open(os.path.join(model_dir, "am.mvn"), encoding="utf-8").read()


def comp(txt, c):
    m = re.search(rf"<{c}>\s+\d+\s+\d+\s*(?:<[^>]+>\s+[^\s]+\s*)*\[(.*?)\]", txt, re.S)
    assert m, f"component {c} not found"
    return np.array([float(x) for x in re.findall(r"[-+]?[0-9]*\.?[0-9]+[eE][-+]?[0-9]+|[-+]?\d+\.\d+", m.group(1))], dtype=np.float32)


shift = comp(am, "AddShift")
rescale = comp(am, "Rescale")

tokens = json.load(open(os.path.join(model_dir, "tokens.json"), encoding="utf-8"))

wavs = []
for name in ("speech_ja", "speech_zh", "test_speech"):
    p = os.path.join(ROOT, "..", "..", "testdata", f"{name}.wav")
    if os.path.exists(p):
        wavs.append((name, p))

for name, wav_path in wavs:
    with wave.open(wav_path, "rb") as w:
        pcm = np.frombuffer(w.readframes(w.getnframes()), dtype=np.int16).astype(np.float32) / 32768.0

    fb = knf.OnlineFbank(opts)
    fb.accept_waveform(16000, pcm)
    fb.input_finished()
    T = fb.num_frames_ready
    feats = np.array([fb.get_frame(i) for i in range(T)], dtype=np.float32)

    m, n = 7, 6
    idx = np.arange(0, T - m + 1, n)
    lfr = np.stack([feats[i:i + m].reshape(-1) for i in idx])
    cmvn = (lfr + shift) * rescale

    np.save(os.path.join(ref_dir, f"kaldi_{name}.npy"), feats)
    np.save(os.path.join(ref_dir, f"kaldi_{name}_lfr_cmvn.npy"), cmvn)
    print(f"{name}: audio {len(pcm)/16000:.2f}s fbank {feats.shape} lfr_cmvn {cmvn.shape}")


def dump_bin(arr, name):
    p = os.path.join(ref_dir, name)
    arr = np.ascontiguousarray(arr, dtype=np.float32)
    with open(p, "wb") as f:
        f.write(arr.tobytes())
        f.write(np.array(arr.shape, dtype=np.int32).tobytes())  # trailing dims header
    print(f"  wrote {p} ({arr.shape})")


for name in wavs:
    base = name[0]
    feats = np.load(os.path.join(ref_dir, f"kaldi_{base}.npy"))
    cmvn = np.load(os.path.join(ref_dir, f"kaldi_{base}_lfr_cmvn.npy"))
    dump_bin(feats, f"kaldi_{base}.bin")
    dump_bin(cmvn, f"kaldi_{base}_lfr_cmvn.bin")

    # Expected text via the ONNX (best-effort; requires onnxruntime + the downloaded model)
    try:
        import onnxruntime as ort  # noqa: E402

        sess = ort.InferenceSession(os.path.join(model_dir, "model_quant.onnx"), providers=["CPUExecutionProvider"])
        out = sess.run(None, {
            "speech": cmvn[np.newaxis, :, :].astype(np.float32),
            "speech_lengths": np.array([cmvn.shape[0]], dtype=np.int32),
            "language": np.array([0], dtype=np.int32),
            "textnorm": np.array([0], dtype=np.int32),
        })
        logits, lens = out
        ids = np.argmax(logits[0, :lens[0]], axis=-1)
        collapsed, prev = [], -1
        for i in ids:
            if i != prev and i != 0:
                collapsed.append(int(i))
                prev = i
        text = "".join(tokens[i] for i in collapsed)
        for ctl in ("<|zh|>", "<|ja|>", "<|en|>", "<|yue|>", "<|ko|>", "<|NEUTRAL|>", "<|Speech|>",
                    "<|woitn|>", "<|withitn|>", "<|EMO_UNKNOWN|>", "<unk>"):
            text = text.replace(ctl, "")
        text = text.replace("▁", " ").strip()
        print(f"  expected: {text!r}")
        exp_path = os.path.join(ref_dir, "sensevoice_expected.json")
        exp = json.load(open(exp_path, encoding="utf-8")) if os.path.exists(exp_path) else {}
        exp[base] = text
        with open(exp_path, "w", encoding="utf-8") as f:
            json.dump(exp, f, ensure_ascii=False, indent=1)
    except Exception as e:  # noqa: BLE001
        print(f"  expected-text skipped: {e}")

print("DONE")
