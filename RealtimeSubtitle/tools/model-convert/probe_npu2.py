#!/usr/bin/env python3
"""Probe NPU encoder properties that can change infer speed for the int8 whisper encoder.

Usage: python probe_npu2.py <model_dir>
"""
import os
import sys
import time

import numpy as np
import openvino as ov

ROOT = os.path.dirname(os.path.abspath(__file__))
os.environ.pop("SSL_CERT_FILE", None)
os.environ.pop("SSL_CERT_DIR", None)
_tmp = os.path.join(ROOT, ".tmp")
os.makedirs(_tmp, exist_ok=True)
for _k in ("TMP", "TEMP", "TMPDIR"):
    os.environ[_k] = _tmp

model_dir = sys.argv[1] if len(sys.argv) > 1 else os.path.join(ROOT, "out", "whisper-base-int8")
cache = os.path.join(_tmp, "npu-cache2")
os.makedirs(cache, exist_ok=True)
feats = np.zeros((1, 80, 3000), np.float32)

core = ov.Core()
for p in ("NPU_MAX_TILES", "DEVICE_GOPS", "OPTIMIZATION_CAPABILITIES", "DEVICE_ARCHITECTURE"):
    try:
        print(f"{p} = {core.get_property('NPU', p)}")
    except Exception as e:  # noqa: BLE001
        print(f"{p} = ? ({e})")

cases = [
    ("baseline", {}),
    ("qdq_opt", {"NPU_QDQ_OPTIMIZATION": "YES"}),
    ("qdq_aggr", {"NPU_QDQ_OPTIMIZATION": "YES", "NPU_QDQ_OPTIMIZATION_AGGRESSIVE": "YES"}),
    ("exec_accuracy", {"EXECUTION_MODE_HINT": "ACCURACY"}),
    ("exec_perf", {"EXECUTION_MODE_HINT": "PERFORMANCE"}),
    ("dynquant_no", {"NPU_COMPILER_DYNAMIC_QUANTIZATION": "NO"}),
    ("defer_weights", {"NPU_DEFER_WEIGHTS_LOAD": "YES"}),
]

for label, props in cases:
    try:
        c2 = ov.Core()
        c2.set_property("NPU", {"CACHE_DIR": os.path.join(cache, label)})
        if props:
            c2.set_property("NPU", props)
        m = c2.read_model(os.path.join(model_dir, "openvino_encoder_model.xml"))
        m.reshape({"input_features": [1, 80, 3000]})
        t0 = time.time()
        c = c2.compile_model(m, "NPU")
        tc = time.time() - t0
        r = c.create_infer_request()
        r.infer({"input_features": feats})
        ts = []
        for _ in range(6):
            t0 = time.time()
            r.infer({"input_features": feats})
            ts.append(1000 * (time.time() - t0))
        print(f"[{label}] compile={1000*tc:7.0f} ms infer_avg={np.mean(ts):6.1f} min={min(ts):6.1f}")
    except Exception as e:  # noqa: BLE001
        print(f"[{label}] FAILED {type(e).__name__}: {str(e).splitlines()[0][:160]}")
