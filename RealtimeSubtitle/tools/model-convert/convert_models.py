#!/usr/bin/env python3
"""Dev-time model conversion for RealtimeSubtitle (plan D7).

Produces, under ./out/<model>/:
  - OpenVINO IR (openvino_model.xml/.bin), INT8 preferred
  - tokenizer.json (marian pieces + special ids) for the C# greedy tokenizer
  - shapes.txt (input/output tensor names+shapes) for the C# translator design

Usage: python convert_models.py [--hf-endpoint https://hf-mirror.com]
"""
import argparse
import json
import os
import subprocess
import sys

ROOT = os.path.dirname(os.path.abspath(__file__))
OUT = os.path.join(ROOT, "out")

# The shell may export SSL_CERT_FILE pointing at a missing file, which makes httpx die
# before any request; drop it and default to the hf-mirror endpoint (CN-friendly).
os.environ.pop("SSL_CERT_FILE", None)
os.environ.pop("SSL_CERT_DIR", None)
os.environ.setdefault("HF_ENDPOINT", "https://hf-mirror.com")
# Keep the HuggingFace cache inside the workspace (sandbox / portability friendly).
os.environ.setdefault("HF_HOME", os.path.join(ROOT, ".hf-cache"))
# The harness may point TMP at a stale sandbox dir; redirect temp to the workspace.
_tmp = os.path.join(ROOT, ".tmp")
os.makedirs(_tmp, exist_ok=True)
for _k in ("TMP", "TEMP", "TMPDIR"):
    os.environ[_k] = _tmp
# CJK console: rich/nncf progress bars use • which GBK cannot encode; force UTF-8 for
# this process AND subprocesses (optimum-cli inherits os.environ).
os.environ.setdefault("PYTHONUTF8", "1")
os.environ.setdefault("PYTHONIOENCODING", "utf-8")

AP = argparse.ArgumentParser()
AP.add_argument("--hf-endpoint", default=None, help="HuggingFace endpoint mirror if the default is unreachable")
AP.add_argument("--whisper-size", default="tiny", choices=["tiny", "base", "small"], help="whisper model size (default tiny; base recommended for accuracy)")
AP.add_argument("--marian-pair", default="en-zh", help="marian translation pair, e.g. en-zh or ja-zh")
AP.add_argument("--marian-repo", default=None, help="override the HF repo for the pair; official Helsinki-NLP/opus-mt-ja-zh is gated (401), use a public mirror like shun89/opus-mt-ja-zh")
AP.add_argument("--only", default=None, choices=["whisper", "whisper-past", "whisper-en", "sensevoice", "sensevoice-static", "sensevoice-fp32", "qwen3-asr", "sidecar", "marian", "compress-marian", "shapes"], help="run a single stage")
args = AP.parse_args()
if args.hf_endpoint:
    os.environ["HF_ENDPOINT"] = args.hf_endpoint

WHISPER_ID = f"openai/whisper-{args.whisper_size}"
WHISPER_DIR = os.path.join(OUT, f"whisper-{args.whisper_size}-int8")

os.makedirs(OUT, exist_ok=True)

def run(cmd):
    print(">>> " + " ".join(cmd), flush=True)
    r = subprocess.run(cmd)
    if r.returncode != 0:
        raise SystemExit(f"command failed ({r.returncode}): {' '.join(cmd)}")

def optimum_cli():
    """optimum 2.x ships the CLI as a console script; `python -m optimum.cli` is gone."""
    script = "optimum-cli.exe" if os.name == "nt" else "optimum-cli"
    here = os.path.dirname(sys.executable)
    return os.path.join(here, script)

def export_openvino(model_id, out_dir, weight_format="int8", task=None):
    marker = os.path.join(out_dir, "openvino_encoder_model.xml") \
        if os.path.exists(os.path.join(out_dir, "openvino_encoder_model.xml")) \
        else os.path.join(out_dir, "openvino_model.xml")
    if os.path.exists(marker):
        print(f"skip export, exists: {out_dir}", flush=True)
        return
    cmd = [optimum_cli(), "export", "openvino",
           "--model", model_id, out_dir, "--weight-format", weight_format]
    if task:
        cmd += ["--task", task]
    run(cmd)

# ---- Stage 1: Whisper (ASR backend, P2-3) ----------------------------------
# The `-with-past` task makes optimum hide the decoder KV cache inside the graph as
# OpenVINO states (ReadValue/Assign). The recognizer then feeds ONE token per decode step
# instead of re-running the whole prefix, which is ~5x faster per step (P6-2).
if args.only in (None, "whisper"):
    export_openvino(WHISPER_ID, WHISPER_DIR,
                    weight_format="int8",
                    task="automatic-speech-recognition-with-past")

# ---- Stage 1a: retrofit a KV-cache decoder into an existing no-past export ------------
# Older checkouts exported the decoder without past; re-export it (weights are identical)
# and drop the stateful decoder into the model dir, keeping the encoder + sidecars.
if args.only == "whisper-past":
    past_dir = os.path.join(OUT, f"whisper-{args.whisper_size}-past")
    export_openvino(WHISPER_ID, past_dir,
                    weight_format="int8",
                    task="automatic-speech-recognition-with-past")
    import shutil
    for f in ("openvino_decoder_model.xml", "openvino_decoder_model.bin"):
        src = os.path.join(past_dir, f)
        if not os.path.exists(src):
            raise SystemExit(f"missing {src} — did the export use -with-past?")
        shutil.copy2(src, os.path.join(WHISPER_DIR, f))
    print(f"stateful decoder installed into {WHISPER_DIR}", flush=True)

# ---- Stage 1b: whisper sidecar data for the classic-API recognizer --------------
if args.only in (None, "whisper", "sidecar"):
    import numpy as np
    from transformers import WhisperTokenizer, WhisperFeatureExtractor  # noqa: E402

    wdir = WHISPER_DIR
    wok = WhisperTokenizer.from_pretrained(WHISPER_ID)
    vocab = wok.get_vocab()
    n_total = max(vocab.values()) + 1  # includes added specials beyond vocab_size
    pieces = [wok.convert_ids_to_tokens(i) for i in range(n_total)]
    special_ids = {}
    for sname, stok in (("sot", "<|startoftranscript|>"), ("eos", "<|endoftext|>"),
                        ("transcribe", "<|transcribe|>"), ("notimestamps", "<|notimestamps|>")):
        special_ids[sname] = wok.convert_tokens_to_ids(stok)
    # language tokens look like <|xx|> in the vocab
    lang_ids = {}
    for i, p in enumerate(pieces):
        if len(p) == 6 and p.startswith("<|") and p.endswith("|>") and p[2:4].isalpha():
            lang_ids[p[2:4]] = i
    with open(os.path.join(wdir, "whisper_meta.json"), "w", encoding="utf-8") as f:
        json.dump({
            "pieces": pieces,
            "lang_ids": lang_ids,
            "special_ids": special_ids,
            "byte_map": dict(wok.byte_decoder),  # unicode char → byte
        }, f, ensure_ascii=False)

    # OpenAI log-mel filterbank (slaney, 80 mels, sr 16k, n_fft 400). The feature
    # extractor stores it bins×mels; dump as mels×bins so C# multiplies filters[m,k]*mag[k].
    fe = WhisperFeatureExtractor.from_pretrained(WHISPER_ID)
    filters = np.array(fe.mel_filters.numpy() if hasattr(fe.mel_filters, "numpy") else fe.mel_filters)
    filters = filters.T  # → (n_mels, n_bins)
    with open(os.path.join(wdir, "mel_filters.bin"), "wb") as f:
        f.write(np.asarray(filters, dtype=np.float32).tobytes())
        f.write(np.array([filters.shape[0], filters.shape[1]], dtype=np.int32).tobytes())
    print(f"whisper_meta.json + mel_filters.bin ({filters.shape}) -> {wdir}", flush=True)

# ---- Stage 1c: Whisper English-only (whisper.en, P6-4) ----------------------------
# Same architecture as multilingual whisper but the tokenizer has a single language token
# (<|en|>); the sidecar gains "mono": true so the recognizer skips language detection and
# the language token in the prompt.
if args.only == "whisper-en":
    import numpy as np
    from transformers import WhisperTokenizer, WhisperFeatureExtractor  # noqa: E402

    mid = f"openai/whisper-{args.whisper_size}.en"
    wdir = os.path.join(OUT, f"whisper-en-{args.whisper_size}-int8")
    export_openvino(mid, wdir, weight_format="int8", task="automatic-speech-recognition-with-past")

    wok = WhisperTokenizer.from_pretrained(mid)
    vocab = wok.get_vocab()
    n_total = max(vocab.values()) + 1
    pieces = [wok.convert_ids_to_tokens(i) for i in range(n_total)]
    special_ids = {}
    for sname, stok in (("sot", "<|startoftranscript|>"), ("eos", "<|endoftext|>"),
                        ("transcribe", "<|transcribe|>"), ("notimestamps", "<|notimestamps|>")):
        special_ids[sname] = wok.convert_tokens_to_ids(stok)
    lang_ids = {}
    for i, p in enumerate(pieces):
        if len(p) == 6 and p.startswith("<|") and p.endswith("|>") and p[2:4].isalpha():
            lang_ids[p[2:4]] = i
    with open(os.path.join(wdir, "whisper_meta.json"), "w", encoding="utf-8") as f:
        json.dump({
            "pieces": pieces,
            "lang_ids": lang_ids,
            "special_ids": special_ids,
            "byte_map": dict(wok.byte_decoder),
            # whisper.en keeps the full language-token set in its vocab, but it is a
            # single-language model: prompt = [sot, transcribe, notimestamps] with no
            # language token and no detection (OpenAI's own decode contract).
            "mono": True,
        }, f, ensure_ascii=False)

    fe = WhisperFeatureExtractor.from_pretrained(mid)
    filters = np.array(fe.mel_filters.numpy() if hasattr(fe.mel_filters, "numpy") else fe.mel_filters)
    filters = filters.T  # → (n_mels, n_bins)
    with open(os.path.join(wdir, "mel_filters.bin"), "wb") as f:
        f.write(np.asarray(filters, dtype=np.float32).tobytes())
        f.write(np.array([filters.shape[0], filters.shape[1]], dtype=np.int32).tobytes())
    print(f"whisper.en ({mid}) exported + sidecar ({filters.shape}) -> {wdir}", flush=True)

# ---- Stage 5: SenseVoiceSmall (Japanese-focused ASR, P6-6) --------------------------
# Community INT8 ONNX (FunAudioLLM/SenseVoiceSmall, Apache-2.0). OpenVINO reads ONNX
# directly, so no IR conversion is needed; the recognizer needs the kaldi-style fbank
# sidecar (80 mel, hamming 25/10ms, CMVN am.mvn, LFR 7/6) and the token vocabulary.
if args.only == "sensevoice":
    import numpy as np  # noqa: F401
    from huggingface_hub import hf_hub_download  # noqa: E402

    repo = "DennisHuang648/SenseVoiceSmall-onnx"
    wdir = os.path.join(OUT, "sensevoice-small-int8")
    os.makedirs(wdir, exist_ok=True)
    for f in ("model_quant.onnx", "am.mvn", "tokens.json", "config.yaml"):
        hf_hub_download(repo, f, local_dir=wdir)
        print(f"downloaded {f} -> {wdir}", flush=True)

    # Parse the frontend config the recognizer must replicate, and the AM.MVN kaldi
    # nnet text (AddShift/Rescale on the 560-dim LFR-stacked features).
    import re as _re  # noqa: E402
    import yaml  # noqa: E402
    with open(os.path.join(wdir, "config.yaml"), encoding="utf-8") as f:
        cfg = yaml.safe_load(f)
    fe_conf = cfg["frontend_conf"]
    n_mels = int(fe_conf["n_mels"])
    am_text = open(os.path.join(wdir, "am.mvn"), encoding="utf-8").read()

    def kaldi_component(txt, comp):
        m = _re.search(rf"<{comp}>\s+\d+\s+\d+\s*(?:<[^>]+>\s+[^\s]+\s*)*\[(.*?)\]", txt, _re.S)
        if not m:
            raise SystemExit(f"am.mvn: component <{comp}> not found")
        return [float(x) for x in _re.findall(
            r"[-+]?[0-9]*\.?[0-9]+[eE][-+]?[0-9]+|[-+]?\d+\.\d+", m.group(1))]

    shift = kaldi_component(am_text, "AddShift")
    rescale = kaldi_component(am_text, "Rescale")
    if len(shift) != n_mels * 7 or len(rescale) != n_mels * 7:
        raise SystemExit(f"am.mvn dims unexpected: shift={len(shift)} rescale={len(rescale)}")
    meta = {
        "fs": int(fe_conf["fs"]),
        "n_mels": n_mels,
        "window": fe_conf.get("window", "hamming"),
        "frame_length_ms": int(fe_conf["frame_length"]),
        "frame_shift_ms": int(fe_conf["frame_shift"]),
        "lfr_m": int(fe_conf["lfr_m"]),
        "lfr_n": int(fe_conf["lfr_n"]),
        "cmvn_shift": shift,
        "cmvn_rescale": rescale,
    }
    with open(os.path.join(wdir, "sensevoice_meta.json"), "w", encoding="utf-8") as f:
        json.dump(meta, f, ensure_ascii=False)
    print(f"sensevoice_meta.json -> {wdir}", flush=True)

# ---- Stage 5b: static SenseVoice ONNX (P6-6 follow-up) ------------------------
# The community export keeps speech/speech_lengths/language/textnorm dynamic; the NPU
# rejects it and OpenVINO's dynamic shapes misbehave for some lengths. Fold the scalar
# inputs into constants and fix speech to [1, N, 560] (verified: unmasked zero-padding
# yields identical transcripts).
if args.only == "sensevoice-static":
    import subprocess as _sp
    n = os.environ.get("SENSEVOICE_STATIC_N", "200")
    r = _sp.run([sys.executable, os.path.join(ROOT, "export_sensevoice_static.py"),
                 os.path.join(OUT, "sensevoice-small-int8"), n])
    if r.returncode != 0:
        raise SystemExit(f"sensevoice-static failed ({r.returncode})")
    # remember the static length for the C# recognizer
    meta_path = os.path.join(OUT, "sensevoice-small-int8", "sensevoice_meta.json")
    with open(meta_path, encoding="utf-8") as f:
        meta = json.load(f)
    meta["static_n"] = int(n)
    with open(meta_path, "w", encoding="utf-8") as f:
        json.dump(meta, f, ensure_ascii=False)
    print(f"sensevoice static_n={n} recorded in sensevoice_meta.json", flush=True)

# ---- Stage 5c: dequantize the static SenseVoice to pure FP32 (P6-9 NPU path) ----------
# The int8 DynamicQuantizeLinear/MatMulInteger chains mis-compute on the NPU (wrong logits);
# the equivalent FP32 model is exact and ~4.6x faster on the NPU (58 ms vs 267 ms).
if args.only == "sensevoice-fp32":
    import subprocess as _sp
    r = _sp.run([sys.executable, os.path.join(ROOT, "dequantize_sensevoice.py"),
                 os.path.join(OUT, "sensevoice-small-int8")])
    if r.returncode != 0:
        raise SystemExit(f"sensevoice-fp32 failed ({r.returncode})")
    print("sensevoice FP32 static export ready", flush=True)

# ---- Stage 6: Qwen3-ASR (Chinese-focused ASR, P6-5) --------------------------------
# Community OpenVINO export of Qwen3-ASR-1.7B (Apache-2.0): audio encoder (mel 128×1000 →
# 130 audio embeddings) + Qwen3-1.7B LM with explicit KV cache. The recognizer also needs
# the 128-mel filterbank (same slaney formula as whisper, n_mels=128) and the prompt
# template/ids.
if args.only == "qwen3-asr":
    import numpy as np  # noqa: F401
    from huggingface_hub import hf_hub_download  # noqa: E402
    from transformers import WhisperFeatureExtractor  # noqa: E402

    repo = "dseditor/Qwen3-ASR-1.7B-INT8_OpenVINO"
    wdir = os.path.join(OUT, "qwen3-asr-1.7b-int8")
    os.makedirs(wdir, exist_ok=True)
    for f in (
        "audio_encoder_model.xml", "audio_encoder_model.bin",
        "thinker_embeddings_model.xml", "thinker_embeddings_model.bin",
        "decoder_prefill_kv_model.xml", "decoder_prefill_kv_model.bin",
        "decoder_kv_model.xml", "decoder_kv_model.bin",
        "vocab.json", "merges.txt", "prompt_template.json", "preprocessor_config.json",
        "config.json", "tokenizer_config.json",
    ):
        hf_hub_download(repo, f, local_dir=wdir)
        print(f"downloaded {f} -> {wdir}", flush=True)

    # 128-mel filterbank: whisper-style slaney filters with n_mels=128 (feature_size).
    fe = WhisperFeatureExtractor(feature_size=128)
    filters = np.array(fe.mel_filters.numpy() if hasattr(fe.mel_filters, "numpy") else fe.mel_filters)
    filters = filters.T  # → (128, n_bins)
    with open(os.path.join(wdir, "mel_filters_128.bin"), "wb") as f:
        f.write(np.asarray(filters, dtype=np.float32).tobytes())
        f.write(np.array([filters.shape[0], filters.shape[1]], dtype=np.int32).tobytes())
    print(f"mel_filters_128.bin ({filters.shape}) -> {wdir}", flush=True)

# ---- Stage 2: Marian MT (translation backend, D4) --------------------------
marian_id = args.marian_repo or f"Helsinki-NLP/opus-mt-{args.marian_pair}"
marian_pair = args.marian_pair.replace("-", "_")  # "en-zh" -> "en_zh"
marian_out = os.path.join(OUT, f"opus-mt-{args.marian_pair}-int8")
marian_fp32 = os.path.join(OUT, f"opus-mt-{args.marian_pair}-fp32")
if args.only == "sidecar":
    print("sidecar-only: done (marian stages skipped).", flush=True)
    sys.exit(0)
if args.only in (None, "marian"):
    # optimum may reject marian architectures for int8; try, and fall back to
    # fp32 export + NNCF PTQ below. Task must be explicit: mirrors often lack the
    # pipeline metadata and auto-detection fails for marian ("task auto").
    marian_task = "text2text-generation-with-past"
    try:
        export_openvino(marian_id, marian_out, weight_format="int8", task=marian_task)
    except SystemExit as e:
        print(f"int8 export failed ({e}); falling back to fp32 + NNCF PTQ", flush=True)
        export_openvino(marian_id, marian_fp32, weight_format="fp32", task=marian_task)
        import nncf
        import openvino as ov
        from datasets import load_dataset
        core = ov.Core()
        model = core.read_model(os.path.join(marian_fp32, "openvino_model.xml"))
        # Tiny calibration set from OPUS, truncated to a few sentences.
        ds = load_dataset("Helsinki-NLP/opus-100", marian_pair, split="train", streaming=True)
        def calib():
            for i, row in enumerate(ds):
                if i >= 8:
                    break
                yield {k: v for k, v in model.input().items()} if hasattr(model, "input") and isinstance(model.input(), dict) else None
        # NNCF needs representative data tensors by input name; use the tokenizer below is complex,
        # so fall back to weight-only compression instead of full PTQ for marian.
        print("marian: NNCF dataset-based PTQ requires tokenized calibration; using INT8 export path check", flush=True)

# ---- Stage 2b: marian INT8 via NNCF weight compression (optimum rejects marian int8) ----
if args.only in (None, "compress-marian"):
    import openvino as ov
    import nncf
    fp32 = marian_fp32
    int8 = marian_out
    xml = os.path.join(fp32, "openvino_model.xml")
    if not os.path.exists(xml):
        print("compress-marian: fp32 source missing, run marian stage first", flush=True)
    elif os.path.exists(os.path.join(int8, "openvino_model.xml")):
        print("compress-marian: already exists, skip", flush=True)
    else:
        os.makedirs(int8, exist_ok=True)
        core = ov.Core()
        model = core.read_model(xml)
        print("compress-marian: compressing weights to INT8 ...", flush=True)
        compressed = nncf.compress_weights(model, mode=nncf.CompressWeightsMode.INT8)
        ov.save_model(compressed, os.path.join(int8, "openvino_model.xml"))
        # Reuse tokenizer/config sidecar files from the fp32 export.
        import shutil
        for f in ("config.json", "generation_config.json", "source.spm", "target.spm",
                  "tokenizer_config.json", "vocab.json", "tokenizer.json"):
            src = os.path.join(fp32, f)
            if os.path.exists(src):
                shutil.copy2(src, os.path.join(int8, f))
        print(f"compress-marian: INT8 model saved -> {int8}", flush=True)

# ---- Stage 3: dump marian tokenizer pieces for the C# greedy tokenizer ----
if args.only in (None, "marian", "shapes"):
    from transformers import MarianTokenizer  # noqa: E402
    tok = MarianTokenizer.from_pretrained(marian_id)
    n = tok.vocab_size
    pieces = [tok.convert_ids_to_tokens(i) for i in range(n)]

    def sid(name, default):
        v = getattr(tok, name, None)
        return int(v) if v is not None else default

    # decoder start comes from generation_config.json of the exported model (marian: 65000)
    gstart = 0
    gcfg_path = os.path.join(marian_out, "generation_config.json")
    if os.path.exists(gcfg_path):
        try:
            with open(gcfg_path, encoding="utf-8") as f:
                v = json.load(f).get("decoder_start_token_id")
            if isinstance(v, int):
                gstart = v
        except Exception:
            pass

    data = {
        "src_pieces": pieces,
        "tgt_pieces": pieces,
        "bos_id": sid("bos_token_id", 1),   # marian: <s>
        "eos_id": sid("eos_token_id", 0),   # marian: </s> (target vocab id 0)
        "unk_id": sid("unk_token_id", 1),
        "pad_id": sid("pad_token_id", 65000),
        "decoder_start_id": gstart,        # from generation_config (e.g. 65000)
        "lowercase": True,
    }
    print(f"special ids: bos={data['bos_id']} eos={data['eos_id']} unk={data['unk_id']} pad={data['pad_id']} decoder_start={data['decoder_start_id']}", flush=True)
    tok_path = os.path.join(marian_out, "tokenizer.json")
    os.makedirs(marian_out, exist_ok=True)
    with open(tok_path, "w", encoding="utf-8") as f:
        json.dump(data, f, ensure_ascii=False)
    # Also mirror into the fp32 dir so either variant is usable standalone.
    os.makedirs(marian_fp32, exist_ok=True)
    with open(os.path.join(marian_fp32, "tokenizer.json"), "w", encoding="utf-8") as f:
        json.dump(data, f, ensure_ascii=False)
    print(f"tokenizer.json -> {tok_path} ({n} pieces)", flush=True)

# ---- Stage 4: dump input/output shapes of the exported models --------------
if args.only in (None, "shapes"):
    import glob
    import openvino as ov
    core = ov.Core()
    whisper_dirs = sorted(glob.glob(os.path.join(OUT, "whisper-*-int8")))
    for name, d in ([(f"whisper-{os.path.basename(x)}", x) for x in whisper_dirs] +
                    [("marian", marian_out)]):
        if not os.path.isdir(d):
            print(f"shapes: missing dir {d}", flush=True)
            continue
        xmls = [f for f in os.listdir(d) if f.endswith(".xml")]
        if not xmls:
            print(f"shapes: no xml in {d}", flush=True)
            continue
        lines = [f"== {name} =="]
        for xml in sorted(xmls):
            try:
                m = core.read_model(os.path.join(d, xml))
            except Exception as e:
                lines.append(f"  !! cannot read {xml}: {e}")
                continue
            lines.append(f"  -- {xml}")
            for inp in m.inputs:
                lines.append(f"     IN  {inp.any_name} shape={list(inp.partial_shape)} dtype={inp.element_type}")
            for out in m.outputs:
                lines.append(f"     OUT {out.any_name} shape={list(out.partial_shape)} dtype={out.element_type}")
        text = "\n".join(lines) + "\n"
        print(text, flush=True)
        with open(os.path.join(d, "shapes.txt"), "w", encoding="utf-8") as f:
            f.write(text)

print("DONE", flush=True)