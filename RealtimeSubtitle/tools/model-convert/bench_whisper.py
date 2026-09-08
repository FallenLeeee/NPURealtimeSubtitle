#!/usr/bin/env python3
"""Benchmark whisper stages on this machine (encoder CPU/NPU, decoder no-past per-step).

Usage: python bench_whisper.py <model_dir> [wav] [device]

Reports where a transcribe spends its time so the C# recognizer can be optimized with data:
  mel (transformers) → encoder (CPU vs NPU) → greedy decoder steps (CPU).
Also measures the NPU model cache effect (CACHE_DIR) for repeat compiles.
"""
import os
import sys
import time
import wave

import numpy as np
import openvino as ov

ROOT = os.path.dirname(os.path.abspath(__file__))
os.environ.pop("SSL_CERT_FILE", None)
os.environ.pop("SSL_CERT_DIR", None)
os.environ.setdefault("HF_HOME", os.path.join(ROOT, ".hf-cache"))
_tmp = os.path.join(ROOT, ".tmp")
os.makedirs(_tmp, exist_ok=True)
for _k in ("TMP", "TEMP", "TMPDIR"):
    os.environ[_k] = _tmp

model_dir = sys.argv[1] if len(sys.argv) > 1 else os.path.join(ROOT, "out", "whisper-base-int8")
wav_path = sys.argv[2] if len(sys.argv) > 2 else os.path.join(ROOT, "..", "..", "testdata", "test_speech.wav")
dev = sys.argv[3] if len(sys.argv) > 3 else "CPU"
hf_id = "openai/whisper-" + os.path.basename(model_dir).replace("whisper-", "").replace("-int8", "").replace("-past", "")
if hf_id.endswith("-"):
    hf_id = "openai/whisper-base"

print(f"model={model_dir}\nwav={wav_path}\nencoder device={dev}\nhf={hf_id}\n")

with wave.open(wav_path, "rb") as w:
    assert w.getframerate() == 16000 and w.getnchannels() == 1, (w.getframerate(), w.getnchannels())
    pcm = np.frombuffer(w.readframes(w.getnframes()), dtype=np.int16).astype(np.float32) / 32768.0
print(f"audio: {len(pcm)/16000:.2f}s")

from transformers import WhisperFeatureExtractor  # noqa: E402
fe = WhisperFeatureExtractor.from_pretrained(hf_id)
t0 = time.time()
feats = fe(pcm, sampling_rate=16000, return_tensors="np").input_features.astype(np.float32)
print(f"mel(transformers) {1000*(time.time()-t0):.0f} ms  shape={feats.shape}")

core = ov.Core()

# --- encoder: CPU vs NPU (+ NPU cache effect) ---
for d in ("CPU", "NPU"):
    if d not in core.available_devices:
        print(f"encoder {d}: unavailable")
        continue
    try:
        m = core.read_model(os.path.join(model_dir, "openvino_encoder_model.xml"))
        m.reshape({"input_features": [1, 80, 3000]})
        t0 = time.time()
        c = core.compile_model(m, d)
        t_compile = time.time() - t0
        r = c.create_infer_request()
        r.infer({"input_features": feats})
        hidden = r.get_output_tensor(0).data.copy()
        ts = []
        for _ in range(5):
            t0 = time.time()
            r.infer({"input_features": feats})
            ts.append(1000 * (time.time() - t0))
        print(f"encoder {d}: compile={1000*t_compile:.0f} ms  infer={np.mean(ts):.0f} ms (min {min(ts):.0f})")
    except Exception as e:  # noqa: BLE001
        print(f"encoder {d}: FAILED {type(e).__name__}: {str(e).splitlines()[0][:200]}")

# --- NPU model cache: second compile of the same static model ---
try:
    cache = os.path.join(_tmp, "npu-cache")
    os.makedirs(cache, exist_ok=True)
    for label in ("cold", "warm"):
        core2 = ov.Core()
        core2.set_property({"CACHE_DIR": cache})
        m = core2.read_model(os.path.join(model_dir, "openvino_encoder_model.xml"))
        m.reshape({"input_features": [1, 80, 3000]})
        t0 = time.time()
        c = core2.compile_model(m, "NPU")
        print(f"npu-cache {label}: compile={1000*(time.time()-t0):.0f} ms")
        del c
except Exception as e:  # noqa: BLE001
    print(f"npu-cache: FAILED {type(e).__name__}: {str(e).splitlines()[0][:200]}")

# --- decoder (no past): greedy per-step cost ---
try:
    import json
    meta = json.load(open(os.path.join(model_dir, "whisper_meta.json"), encoding="utf-8"))
    sot = meta["special_ids"]["sot"]
    eos = meta["special_ids"]["eos"]
    tr = meta["special_ids"]["transcribe"]
    nt = meta["special_ids"]["notimestamps"]
    lang = meta["lang_ids"].get("en", sot)

    dm = core.read_model(os.path.join(model_dir, "openvino_decoder_model.xml"))
    print("decoder inputs:", [(i.any_name, str(i.partial_shape)) for i in dm.inputs])
    for d in ("CPU", "NPU"):
        if d not in core.available_devices:
            continue
        try:
            m = core.read_model(os.path.join(model_dir, "openvino_decoder_model.xml"))
            m.reshape({"input_ids": [1, 4], "encoder_hidden_states": [1, 1500, hidden.shape[-1]]})
            t0 = time.time()
            c = core.compile_model(m, d)
            t_compile = time.time() - t0
            r = c.create_infer_request()
            ids = [sot, lang, tr, nt]
            step_ms = []
            out_ids = []
            for step in range(40):
                arr = np.array([ids], dtype=np.int64)
                t0 = time.time()
                res = r.infer({"input_ids": arr, "encoder_hidden_states": hidden})
                step_ms.append(1000 * (time.time() - t0))
                logits = res[list(res)[0]]
                nxt = int(np.argmax(logits[0, -1]))
                if nxt == eos:
                    break
                ids.append(nxt)
                out_ids.append(nxt)
            print(f"decoder {d} (no-past): compile={1000*t_compile:.0f} ms  steps={len(step_ms)} "
                  f"total={sum(step_ms):.0f} ms  per-step avg={np.mean(step_ms):.1f} ms "
                  f"first={step_ms[0]:.0f} last={step_ms[-1]:.0f}")
        except Exception as e:  # noqa: BLE001
            print(f"decoder {d}: FAILED {type(e).__name__}: {str(e).splitlines()[0][:200]}")
except Exception as e:  # noqa: BLE001
    print(f"decoder: FAILED {type(e).__name__}: {str(e).splitlines()[0][:200]}")
