#!/usr/bin/env python3
"""Probe: can the exported whisper encoder run on shorter mel windows than 3000 frames?

Usage: python probe_encoder.py <model_dir> [device]

The recognizer always feeds [1,80,3000] (30 s). If the graph accepts [1,80,N] with N<3000,
the encoder cost scales with real audio length instead of a fixed 30 s window.
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

d = sys.argv[1] if len(sys.argv) > 1 else os.path.join(ROOT, "out", "whisper-base-int8")
dev = sys.argv[2] if len(sys.argv) > 2 else "CPU"
xml = os.path.join(d, "openvino_encoder_model.xml")

core = ov.Core()
m0 = core.read_model(xml)
print("encoder inputs:", [(i.any_name, str(i.partial_shape), str(i.element_type)) for i in m0.inputs])
print("encoder outputs:", [(o.any_name, str(o.partial_shape)) for o in m0.outputs])

for n in (3000, 2000, 1500, 1000, 600, 400, 200):
    try:
        m = core.read_model(xml)
        m.reshape({"input_features": [1, 80, n]})
        t0 = time.time()
        c = core.compile_model(m, dev)
        t1 = time.time()
        r = c.create_infer_request()
        t2 = time.time()
        r.infer({"input_features": np.zeros((1, 80, n), np.float32)})
        t3 = time.time()
        out = r.get_output_tensor(0)
        print(f"OK n={n:5d} compile={1000*(t1-t0):7.0f}ms first_infer={1000*(t3-t2):7.0f}ms "
              f"out={out.any_name}{list(out.shape)}")
        # second infer = steady state
        for _ in range(3):
            r.infer({"input_features": np.zeros((1, 80, n), np.float32)})
        t4 = time.time()
        r.infer({"input_features": np.zeros((1, 80, n), np.float32)})
        print(f"          steady_infer={1000*(time.time()-t4):7.0f}ms")
    except Exception as e:  # noqa: BLE001
        print(f"FAIL n={n:5d} {type(e).__name__}: {str(e).splitlines()[0][:220]}")
