#!/usr/bin/env python3
"""Export a fully STATIC SenseVoiceSmall ONNX (P6-6 follow-up).

The community export keeps `speech`/`speech_lengths`/`language`/`textnorm` dynamic and builds
the encoder attention mask from the runtime `speech_lengths` value, which the NPU compiler
rejects and OpenVINO's dynamic shapes mishandle for some lengths. Verified empirically that
zero-padding the input to a fixed length and treating ALL frames as real (no masking) yields
the same transcript — so we fold the three scalar inputs into constants and fix `speech` to
[1, N, 560].

Usage: python export_sensevoice_static.py [model_dir] [N]
"""
import json
import os
import sys

import numpy as np

ROOT = os.path.dirname(os.path.abspath(__file__))
os.environ.pop("SSL_CERT_FILE", None)
os.environ.pop("SSL_CERT_DIR", None)
_tmp = os.path.join(ROOT, ".tmp")
os.makedirs(_tmp, exist_ok=True)
for _k in ("TMP", "TEMP", "TMPDIR"):
    os.environ[_k] = _tmp

model_dir = sys.argv[1] if len(sys.argv) > 1 else os.path.join(ROOT, "out", "sensevoice-small-int8")
N = int(sys.argv[2]) if len(sys.argv) > 2 else 300

import onnx  # noqa: E402
from onnx import numpy_helper  # noqa: E402

src = os.path.join(model_dir, "model_quant.onnx")
dst = os.path.join(model_dir, "model_static.onnx")
m = onnx.load(src)

# 1) remove the scalar inputs that become constants
keep = [i for i in m.graph.input if i.name not in ("speech_lengths", "language", "textnorm")]
del m.graph.input[:]
m.graph.input.extend(keep)

# 2) provide them as initializers (constant values)
for name, val in (("speech_lengths", np.array([N], np.int32)),
                  ("language", np.array([0], np.int32)),
                  ("textnorm", np.array([0], np.int32))):
    m.graph.initializer.append(numpy_helper.from_array(val, name))

# 3) fix the speech input shape to [1, N, 560]
for i in m.graph.input:
    if i.name == "speech":
        d = i.type.tensor_type.shape.dim
        d[0].dim_value = 1
        d[1].dim_value = N
        d[2].dim_value = 560
        print(f"speech -> static [1, {N}, 560]")

try:
    onnx.checker.check_model(m, full_check=False)
    print("onnx.checker: OK")
except Exception as e:  # noqa: BLE001
    print(f"onnx.checker: warning {e}")

onnx.save(m, dst)
print(f"saved {dst} ({os.path.getsize(dst) / 1e6:.1f} MB, inputs={[i.name for i in m.graph.input]})")

# ---- verify: fixed-length padded input decodes identically to the dynamic model ----
import onnxruntime as ort  # noqa: E402

for wav in ("kaldi_speech_ja_lfr_cmvn", "kaldi_speech_zh_lfr_cmvn"):
    feats = np.load(os.path.join(ROOT, "testrefs", f"{wav}.npy"))
    tokens = json.load(open(os.path.join(model_dir, "tokens.json"), encoding="utf-8"))
    L = feats.shape[0]
    x = np.zeros((1, N, 560), np.float32)
    x[0, :L] = feats
    sess = ort.InferenceSession(dst, providers=["CPUExecutionProvider"])
    out = sess.run(None, {"speech": x.astype(np.float32)})
    logits, lens = out
    ids = np.argmax(logits[0, : int(lens[0])], axis=-1)
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
    print(f"[static N={N}] {wav}: lens={lens} rows={int(lens[0])} real={L} text={text[:60]!r}")

# ---- NPU / CPU timing via OpenVINO ----
import time  # noqa: E402
import openvino as ov  # noqa: E402

core = ov.Core()
for dev in ("CPU", "NPU"):
    if dev not in core.available_devices:
        print(f"{dev}: unavailable")
        continue
    try:
        t0 = time.time()
        c = core.compile_model(dst, dev)
        tc = time.time() - t0
        r = c.create_infer_request()
        x = np.zeros((1, N, 560), np.float32)
        r.infer({"speech": x.astype(np.float32)})
        ts = []
        for _ in range(3):
            t0 = time.time()
            r.infer({"speech": x.astype(np.float32)})
            ts.append(1000 * (time.time() - t0))
        print(f"[static N={N}] {dev}: compile={1000*tc:.0f}ms infer_avg={np.mean(ts):.0f}ms")
    except Exception as e:  # noqa: BLE001
        print(f"[static N={N}] {dev}: FAIL {str(e).splitlines()[0][:140]}")