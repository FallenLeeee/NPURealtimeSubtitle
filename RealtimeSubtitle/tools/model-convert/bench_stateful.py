#!/usr/bin/env python3
"""Device comparison for the stateful (KV-cache) whisper pipeline.

Usage: python bench_stateful.py <model_dir> [wav] [devices]

For each device: encoder compile/infer, then a full greedy decode through the stateful
decoder (fresh infer request per segment = fresh KV cache), reporting per-step latency and
the decoded text so a device is only accepted if it is both fast AND correct.
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

model_dir = sys.argv[1] if len(sys.argv) > 1 else os.path.join(ROOT, "out", "whisper-small-past")
wav_path = sys.argv[2] if len(sys.argv) > 2 else os.path.join(ROOT, "..", "..", "testdata", "test_speech.wav")
devices = sys.argv[3].split(",") if len(sys.argv) > 3 else ["CPU", "NPU", "GPU"]
size = os.path.basename(model_dir).replace("whisper-", "").replace("-int8", "").replace("-past", "")
hf_id = f"openai/whisper-{size}"

with wave.open(wav_path, "rb") as w:
    pcm = np.frombuffer(w.readframes(w.getnframes()), dtype=np.int16).astype(np.float32) / 32768.0

from transformers import WhisperFeatureExtractor, WhisperTokenizer  # noqa: E402
fe = WhisperFeatureExtractor.from_pretrained(hf_id)
tok = WhisperTokenizer.from_pretrained(hf_id)
feats = fe(pcm, sampling_rate=16000, return_tensors="np").input_features.astype(np.float32)

sot = tok.convert_tokens_to_ids("<|startoftranscript|>")
eos = tok.convert_tokens_to_ids("<|endoftext|>")
tr = tok.convert_tokens_to_ids("<|transcribe|>")
nt = tok.convert_tokens_to_ids("<|notimestamps|>")
lang = tok.convert_tokens_to_ids("<|en|>")
prompt = [sot, lang, tr, nt]

core = ov.Core()
print(f"model={model_dir} devices={core.available_devices}")

for dev in devices:
    if dev not in core.available_devices:
        print(f"\n== {dev}: unavailable ==")
        continue
    print(f"\n== {dev} ==")
    try:
        t0 = time.time()
        em = core.read_model(os.path.join(model_dir, "openvino_encoder_model.xml"))
        # NPU needs fully static shapes; the recognizer always feeds [1,80,3000].
        em.reshape({"input_features": [1, 80, 3000]})
        enc = core.compile_model(em, dev)
        t_enc_compile = time.time() - t0
        er = enc.create_infer_request()
        t0 = time.time()
        hid = er.infer({"input_features": feats})[enc.outputs[0].any_name]
        t_enc_first = time.time() - t0
        ts = []
        for _ in range(3):
            t0 = time.time()
            er.infer({"input_features": feats})
            ts.append(1000 * (time.time() - t0))
        print(f"encoder: compile={1000*t_enc_compile:.0f} ms first={1000*t_enc_first:.0f} ms "
              f"steady={np.mean(ts):.0f} ms")
    except Exception as e:  # noqa: BLE001
        print(f"encoder: FAILED {type(e).__name__}: {str(e).splitlines()[0][:200]}")
        continue

    try:
        t0 = time.time()
        dec = core.compile_model(os.path.join(model_dir, "openvino_decoder_model.xml"), dev)
        t_dec_compile = time.time() - t0
        in_names = [i.any_name for i in dec.inputs]
        out_name = dec.outputs[0].any_name
        r = dec.create_infer_request()  # fresh KV cache
        ids = list(prompt)
        out_ids = []
        step_ms = []
        for step in range(64):
            cur = ids if step == 0 else [ids[-1]]
            feed = {in_names[0]: np.array([cur], dtype=np.int64), in_names[1]: hid}
            for extra in in_names[2:]:
                feed[extra] = np.zeros((1,), dtype=np.int32)
            t0 = time.time()
            res = r.infer(feed)
            step_ms.append(1000 * (time.time() - t0))
            nxt = int(np.argmax(res[out_name][0, -1]))
            if nxt == eos:
                break
            ids.append(nxt)
            out_ids.append(nxt)
        text = tok.decode(out_ids)
        print(f"decoder: compile={1000*t_dec_compile:.0f} ms steps={len(step_ms)} "
              f"total={sum(step_ms):.0f} ms avg={np.mean(step_ms):.1f} ms first={step_ms[0]:.0f} "
              f"last={step_ms[-1]:.0f}")
        print(f"  text: {text!r}")
    except Exception as e:  # noqa: BLE001
        print(f"decoder: FAILED {type(e).__name__}: {str(e).splitlines()[0][:250]}")
