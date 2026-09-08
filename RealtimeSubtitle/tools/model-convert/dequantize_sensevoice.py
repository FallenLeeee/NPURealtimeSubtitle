#!/usr/bin/env python3
"""Dequantize the dynamic-quantized SenseVoice ONNX to pure FP32 (P6-9 NPU attempt).

The iic `model_quant.onnx` uses DynamicQuantizeLinear → MatMulInteger → scale chains. The NPU
mis-computes those (plausible-but-wrong logits). This rewrite replaces every quantized matmul
with an exact FP32 MatMul (weights dequantized), producing a pure-FP32 model that the NPU
should compute faithfully.

Usage: python dequantize_sensevoice.py [model_dir]
Outputs model_fp32_static.onnx. Verifies CPU output == original, then tests the NPU.
"""
import json
import os
import sys

import numpy as np
import onnx
from onnx import helper, numpy_helper

ROOT = os.path.dirname(os.path.abspath(__file__))
os.environ.pop("SSL_CERT_FILE", None)
os.environ.pop("SSL_CERT_DIR", None)
_tmp = os.path.join(ROOT, ".tmp")
os.makedirs(_tmp, exist_ok=True)
for _k in ("TMP", "TEMP", "TMPDIR"):
    os.environ[_k] = _tmp

model_dir = sys.argv[1] if len(sys.argv) > 1 else os.path.join(ROOT, "out", "sensevoice-small-int8")
src = os.path.join(model_dir, "model_static.onnx")
dst = os.path.join(model_dir, "model_fp32_static.onnx")

model = onnx.load(src)
g = model.graph
inits = {i.name: numpy_helper.to_array(i) for i in g.initializer}
consumers = {t: [] for t in inits}
for n in g.node:
    for i in n.input:
        consumers.setdefault(i, []).append(n)

nodes = list(g.node)


def producer_of(t):
    for n in nodes:
        if t in n.output:
            return n
    return None


dql_map = {}          # q_name -> node
for n in nodes:
    if n.op_type == "DynamicQuantizeLinear":
        dql_map[n.output[0]] = n

new_inits = {}
added_nodes = []
remove_names = set()
log = []

for m in [n for n in nodes if n.op_type == "MatMulInteger"]:
    x_q, w_q, x_zp, w_zp = m.input
    dql = dql_map.get(x_q)
    if dql is None or w_q not in inits or w_zp not in inits:
        log.append(f"skip {m.name}: pattern not matched")
        continue

    # y_q -> Cast -> Mul(combined scales) -> y_out
    y_q = m.output[0]
    cast = next((n for n in nodes if n.op_type == "Cast" and y_q in n.input), None)
    if cast is None:
        log.append(f"skip {m.name}: no Cast")
        continue
    y_f = cast.output[0]
    mul = next((n for n in nodes if n.op_type == "Mul" and y_f in n.input), None)
    if mul is None:
        log.append(f"skip {m.name}: no scale Mul")
        continue
    y_out = mul.output[0]
    scale_other = mul.input[1] if mul.input[0] == y_f else mul.input[0]

    # find the w_scale initializer inside the combined-scale chain (scale_other -> ... -> x_s)
    # walk back from scale_other through Mul(Reshape/Unsqueeze*(x_s,...)) to find an init input
    w_scale_name = None
    cur = scale_other
    seen = set()
    while cur not in inits and cur not in seen:
        seen.add(cur)
        p = producer_of(cur)
        if p is None:
            break
        if p.op_type in ("Mul", "Reshape", "Unsqueeze", "Cast", "Transpose"):
            cands = [i for i in p.input if i != "" and i not in (x_s := dql.output[1])]
            cur = cands[0] if cands else None
            if cur is None:
                break
        else:
            log.append(f"skip {m.name}: scale chain op {p.op_type}")
            break
    if cur in inits:
        w_scale_name = cur
    if w_scale_name is None:
        log.append(f"skip {m.name}: w_scale not found (chain {scale_other})")
        continue

    w_q_arr = inits[w_q].astype(np.float32)
    w_zp_arr = inits[w_zp].astype(np.float32)
    w_s_arr = inits[w_scale_name].astype(np.float32)
    # quantized weights are stored transposed [K, N]; per-output-channel scale = last dim
    if w_s_arr.size == w_q_arr.shape[-1]:
        w_s_arr = w_s_arr.reshape([1] * (w_q_arr.ndim - 1) + [w_q_arr.shape[-1]])
    elif w_s_arr.size != 1:
        log.append(f"skip {m.name}: scale size {w_s_arr.size} vs W {w_q_arr.shape}")
        continue
    w_fp32 = (w_q_arr - w_zp_arr) * w_s_arr

    new_name = f"{w_q}_fp32"
    if new_name not in new_inits:
        new_inits[new_name] = w_fp32

    # replace: MatMul(dql.input[0], new weight) -> y_out
    mm = helper.make_node("MatMul", [dql.input[0], new_name], [y_out], name=f"{m.name}_fp32")
    added_nodes.append(mm)
    remove_names.add(m.name)
    remove_names.add(cast.name)
    remove_names.add(mul.name)
    # remove the scale-chain intermediate nodes if they only feed the removed Mul
    cur = scale_other
    seen = set()
    while cur not in inits and cur not in seen:
        seen.add(cur)
        p = producer_of(cur)
        if p is None:
            break
        other_users = [u for u in consumers.get(cur, []) if u.name not in remove_names]
        if p.op_type in ("Mul", "Reshape", "Unsqueeze", "Cast", "Transpose") and not other_users:
            remove_names.add(p.name)
            # keep walking only the non-init input
            nxt = [i for i in p.input if i != "" and i != dql.output[1] and i not in inits]
            cur = nxt[0] if len(nxt) == 1 else None
            if nxt != [cur]:
                break
        else:
            break
    # mark the DQL dead when all its consumers are removed
    dql_users = consumers.get(x_q, [])
    if all(u.name in remove_names for u in dql_users) and dql_users:
        remove_names.add(dql.name)

print("\n".join(log[:20]) if log else "all quantized matmuls rewrote")
print(f"removed count so far: {len(remove_names)}; added: {len(added_nodes)}")

# add dequantized weight initializers
for name, arr in new_inits.items():
    g.initializer.append(numpy_helper.from_array(np.ascontiguousarray(arr, dtype=np.float32), name))

# rebuild node list
keep = [n for n in nodes if n.name not in remove_names]
keep += added_nodes
del g.node[:]
g.node.extend(keep)

# drop initializers no longer referenced (optional; keep for safety if referenced)
used = set()
for n in g.node:
    used.update(i for i in n.input if i != "")
used.update(o for n in g.node for o in n.output)
ref_inits = [i for i in g.initializer if i.name in used or i.name in inits and False]
del g.initializer[:]
g.initializer.extend([i for i in onnx.load(src).graph.initializer if i.name in used])
for name, arr in new_inits.items():
    if name not in {x.name for x in g.initializer}:
        g.initializer.append(numpy_helper.from_array(np.ascontiguousarray(arr, dtype=np.float32), name))

try:
    onnx.checker.check_model(model, full_check=False)
    print("onnx.checker: OK")
except Exception as e:  # noqa: BLE001
    print(f"onnx.checker: warning {e}")

onnx.save(model, dst)
print(f"saved {dst} ({os.path.getsize(dst) / 1e6:.1f} MB, nodes={len(g.node)})")

# ---- verify identical output on CPU ----
import onnxruntime as ort  # noqa: E402

tokens = json.load(open(os.path.join(model_dir, "tokens.json"), encoding="utf-8"))


def run_and_decode(path):
    sess = ort.InferenceSession(path, providers=["CPUExecutionProvider"])
    feats = np.load(os.path.join(ROOT, "testrefs", "kaldi_speech_ja_lfr_cmvn.npy"))
    x = np.zeros((1, 200, 560), np.float32)
    x[0, : feats.shape[0]] = feats
    out = sess.run(None, {"speech": x.astype(np.float32)})
    ids = np.argmax(out[0][0, : int(out[1][0])], axis=-1)
    collapsed, prev = [], -1
    for i in ids:
        if i != prev and i != 0:
            collapsed.append(int(i))
        prev = i
    return "".join(tokens[i] for i in collapsed)


a, b = run_and_decode(src), run_and_decode(dst)
print("static :", a[:80])
print("fp32   :", b[:80])
print("IDENTICAL" if a == b else "*** DIFFERENT ***")

# ---- NPU test ----
import time  # noqa: E402
import openvino as ov  # noqa: E402

core = ov.Core()
feats = np.load(os.path.join(ROOT, "testrefs", "kaldi_speech_ja_lfr_cmvn.npy"))
x = np.zeros((1, 200, 560), np.float32)
x[0, : feats.shape[0]] = feats
for path, label in ((src, "int8-static"), (dst, "fp32-static")):
    try:
        t0 = time.time()
        c = core.compile_model(path, "NPU")
        tc = time.time() - t0
        r = c.create_infer_request()
        r.infer({"speech": x.astype(np.float32)})
        logits = r.get_output_tensor(0).data
        lens = r.get_output_tensor(1).data
        ids = np.argmax(logits[0, : int(lens[0])], axis=-1)
        collapsed, prev = [], -1
        for i in ids:
            if i != prev and i != 0:
                collapsed.append(int(i))
            prev = i
        text = "".join(tokens[i] for i in collapsed)
        for ctl in ("<|ja|>", "<|zh|>", "<|NEUTRAL|>", "<|Speech|>", "<|woitn|>", "<|withitn|>", "<|EMO_UNKNOWN|>", "<unk>"):
            text = text.replace(ctl, "")
        ts = []
        for _ in range(3):
            t0 = time.time()
            r.infer({"speech": x.astype(np.float32)})
            ts.append(1000 * (time.time() - t0))
        print(f"NPU [{label}]: compile={1000*tc:.0f}ms infer_avg={np.mean(ts):.0f}ms nonblank={(ids != 0).sum()} text={text[:40]!r}")
    except Exception as e:  # noqa: BLE001
        print(f"NPU [{label}]: FAIL {str(e).splitlines()[0][:130]}")