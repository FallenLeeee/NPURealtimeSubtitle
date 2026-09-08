#!/usr/bin/env python3
"""Probe Qwen3-ASR OpenVINO pipeline: shapes, mel, prompt, KV-cache decode, NPU feasibility.

Usage: python probe_qwen3.py <model_dir> <wav> [device]
"""
import json
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

d = sys.argv[1] if len(sys.argv) > 1 else os.path.join(ROOT, "out", "qwen3-asr-1.7b-int8")
wav_path = sys.argv[2] if len(sys.argv) > 2 else os.path.join(ROOT, "..", "..", "testdata", "speech_zh.wav")
enc_dev = sys.argv[3] if len(sys.argv) > 3 else "CPU"

with wave.open(wav_path, "rb") as w:
    pcm = np.frombuffer(w.readframes(w.getnframes()), dtype=np.int16).astype(np.float32) / 32768.0
print(f"audio {len(pcm)/16000:.2f}s")

core = ov.Core()
for name in ("audio_encoder_model", "thinker_embeddings_model", "decoder_prefill_kv_model", "decoder_kv_model"):
    m = core.read_model(os.path.join(d, f"{name}.xml"))
    print(f"== {name} ==")
    for i in m.inputs:
        print(f"  IN  {i.any_name} {list(i.partial_shape)} {i.element_type}")
    for o in m.outputs:
        nm = ""
        try:
            nm = o.any_name
        except Exception:
            nm = f"<idx{o.get_index()}>"
        print(f"  OUT {nm} {list(o.partial_shape)} {o.element_type}")

# ---- 128-mel features (whisper feature extractor, matches dumped mel_filters_128.bin) ----
from transformers import WhisperFeatureExtractor  # noqa: E402
fe = WhisperFeatureExtractor(feature_size=128)
feats = fe(pcm, sampling_rate=16000, return_tensors="np").input_features.astype(np.float32)
print(f"mel {feats.shape}")

prompt = json.load(open(os.path.join(d, "prompt_template.json"), encoding="utf-8"))
prefix = prompt["prefix_ids"]
suffix = prompt["suffix_ids"]
audio_pad = prompt["audio_pad_id"]
eos = prompt["eos_id"]
n_audio = prompt["n_audio_tokens"]
lang_suffix = prompt["language_suffix_ids"]["Chinese"]  # zh -> Chinese bias
print("prefix", prefix, "suffix", suffix, "lang_suffix", lang_suffix, "n_audio", n_audio)

# ---- audio encoder: dynamic 2D mel [128, T] (bins-major), n_audio read from the output ----
em = core.read_model(os.path.join(d, "audio_encoder_model.xml"))
t0 = time.time()
enc = core.compile_model(em, enc_dev)
t_comp = time.time() - t0
er = enc.create_infer_request()
# actual frames for this audio: nb = 1 + n_samples/160; the encoder reshape requires
# the mel time length to be a multiple of 100 frames (1 s chunks), so pad to the next.
nb_frames = min(1 + pcm.shape[0] // 160, 3000)
padded = ((nb_frames + 99) // 100) * 100
mel2d = np.zeros((128, padded), np.float32)
mel2d[:, :nb_frames] = feats[0, :, :nb_frames]
print(f"mel2d {mel2d.shape} (real frames {nb_frames})")
t0 = time.time()
audio_embeds = er.infer({"mel": mel2d})[enc.outputs[0]]
t_enc = time.time() - t0
n_audio = audio_embeds.shape[1]
print(f"audio_encoder [{enc_dev}]: compile={1000*t_comp:.0f} ms infer={1000*t_enc:.0f} ms out={audio_embeds.shape} n_audio={n_audio}")

# ---- prompt + embeddings ----
ids = list(prefix) + [audio_pad] * n_audio + list(suffix) + list(lang_suffix)
print(f"prompt ids len={len(ids)}")
te = core.compile_model(os.path.join(d, "thinker_embeddings_model.xml"), "CPU")
ter = te.create_infer_request()
embeds = ter.infer({"input_ids": np.array([ids], dtype=np.int64)})[te.outputs[0]]
print(f"thinker embeds {embeds.shape}")
# splice audio embeds in place of the audio_pad tokens
start = len(prefix)
embeds[0, start:start + n_audio] = audio_embeds[0]

# ---- prefill KV ----
dp = core.compile_model(os.path.join(d, "decoder_prefill_kv_model.xml"), "CPU")
dpr = dp.create_infer_request()
pos_ids = np.arange(len(ids), dtype=np.int64)[None, :]
t0 = time.time()
prefill_out = dpr.infer({"input_embeds": embeds, "position_ids": pos_ids})
t_prefill = time.time() - t0
prefill_logits = prefill_out[dp.outputs[0]]
print(f"prefill: {1000*t_prefill:.0f} ms  logits {prefill_logits.shape}")

# ---- decode loop ----
dk = core.compile_model(os.path.join(d, "decoder_kv_model.xml"), "CPU")
dkr = dk.create_infer_request()
past_keys = prefill_out[dp.outputs[1]]
past_values = prefill_out[dp.outputs[2]]
dec_ids = [int(np.argmax(prefill_logits[0, -1]))]
step_ms = []
for step in range(128):
    in_emb = ter.infer({"input_ids": np.array([[dec_ids[-1]]], dtype=np.int64)})[te.outputs[0]]
    feed = {"new_embed": in_emb, "new_pos": np.array([[len(ids) + step]], dtype=np.int64),
            "past_keys": past_keys, "past_values": past_values}
    t0 = time.time()
    out = dkr.infer(feed)
    step_ms.append(1000 * (time.time() - t0))
    next_id = int(np.argmax(out[dk.outputs[0]][0, -1]))
    past_keys = out[dk.outputs[1]]
    past_values = out[dk.outputs[2]]
    if next_id == eos:
        break
    dec_ids.append(next_id)
print(f"decode: steps={len(step_ms)} total={sum(step_ms):.0f} ms avg={np.mean(step_ms):.1f} ms")

# ---- decode text with the Qwen tokenizer ----
from transformers import AutoTokenizer  # noqa: E402
tok = AutoTokenizer.from_pretrained("Qwen/Qwen3-ASR-1.7B", cache_dir=os.environ["HF_HOME"], trust_remote_code=True)
text = tok.decode(dec_ids, skip_special_tokens=True)
print("RAW ids:", dec_ids[:30])
print("TEXT:", text)
