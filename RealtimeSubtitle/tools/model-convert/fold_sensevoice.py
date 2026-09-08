#!/usr/bin/env python3
"""Constant-fold the static SenseVoice ONNX so no runtime control-flow (Equal/Cast/ReduceSum/
Gather) remains — the NPU mis-computes those even when folded to constants at graph level.

Usage: python fold_sensevoice.py [model_dir]
Outputs model_folded.onnx next to model_static.onnx.
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

import onnx  # noqa: E402
from onnx import helper, numpy_helper  # noqa: E402
from onnx.reference import ReferenceEvaluator  # noqa: E402

src = os.path.join(model_dir, "model_static.onnx")
dst = os.path.join(model_dir, "model_folded.onnx")
model = onnx.load(src)
g = model.graph

# name -> np array for every constant we can evaluate
values = {}
for init in g.initializer:
    values[init.name] = numpy_helper.to_array(init).astype(np.float64, copy=False)
# also register constant tensors embedded in node attributes (rare; guard against API drift)
try:
    for node in g.node:
        for attr in node.attribute:
            if attr.type == onnx.AttributeProto.TENSOR and attr.t.name:
                values[attr.t.name] = numpy_helper.to_array(attr.t)
except Exception:
    pass

# Topologically sort by explicit deps
order = []
done = set()
for init in g.initializer:
    done.add(init.name)


def visit(n):
    if n in done:
        return
    done.add(n)
    order.append(n)


# simple topological order (nodes appear after their producers among graph.input/initializer is
# guaranteed by ONNX canonical order for this model; use a worklist approach instead)
remaining = {id(n): n for n in g.node}
folds = []
while remaining:
    progressed = False
    for nid, n in list(remaining.items()):
        if all(i in values for i in n.input if i != ""):
            # evaluate this single-node model with the reference evaluator
            try:
                outs = []
                for o in n.output:
                    if o == "":
                        continue
                    outs.append(helper.make_tensor_value_info(o, TensorProto.UNDEFINED, None))
                single = helper.make_model(
                    helper.make_graph([n], "fold", [], outs, initializer=[
                        helper.make_tensor(k, numpy_helper.dtype_of(v) if isinstance(v, np.ndarray) else TensorProto.FLOAT,
                                           list(v.shape), v.reshape(-1).tolist())
                        for k, v in values.items() if k in n.input]))
                single.ir_version = model.ir_version
                single.opset_import[:] = model.opset_import
                ref = ReferenceEvaluator(single)
                feeds = {k: values[k] for k in n.input if k != ""}
                res = ref.run(None, feeds)
                for o, r in zip(n.output, res):
                    if o != "":
                        values[o] = np.asarray(r)
                folds.append(n)
                del remaining[nid]
                progressed = True
            except Exception:
                pass  # op not supported by the reference evaluator — leave as-is
    if not progressed:
        break

print(f"folded {len(folds)} nodes, remaining {len(remaining)}")

# rebuild the graph: keep only non-folded nodes; add folded outputs as initializers
keep = list(remaining.values())
keep_inputs = {}
for n in keep:
    for i in n.input:
        if i not in values:
            keep_inputs[i] = True
# inputs that are now constants (computed) must become initializers
new_inits = []
for k, v in values.items():
    if k not in {init.name for init in g.initializer}:
        new_inits.append(numpy_helper.from_array(v.astype(np.float32, copy=False), k))

del g.node[:]
g.node.extend(keep)
del g.initializer[:]
# keep original initializers (not overwritten) + computed constants
kept_orig = [init for init in onnx.load(src).graph.initializer if init.name in values and init.name not in {n.name for n in new_inits}]
g.initializer.extend([init for init in kept_orig if init.name not in {x.name for x in new_inits}])
g.initializer.extend(new_inits)

try:
    onnx.checker.check_model(model, full_check=False)
    print("onnx.checker: OK")
except Exception as e:  # noqa: BLE001
    print(f"onnx.checker: warning {e}")

onnx.save(model, dst)
print(f"saved {dst} ({os.path.getsize(dst) / 1e6:.1f} MB)")

# verify text identical to the unfolded static model
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


print("static :", run_and_decode(src)[:80])
print("folded :", run_and_decode(dst)[:80])

# NPU correctness + timing
import time  # noqa: E402
import openvino as ov  # noqa: E402

core = ov.Core()
for path, label in ((src, "static"), (dst, "folded")):
    try:
        t0 = time.time()
        c = core.compile_model(path, "NPU")
        tc = time.time() - t0
        r = c.create_infer_request()
        feats = np.load(os.path.join(ROOT, "testrefs", "kaldi_speech_ja_lfr_cmvn.npy"))
        x = np.zeros((1, 200, 560), np.float32)
        x[0, : feats.shape[0]] = feats
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