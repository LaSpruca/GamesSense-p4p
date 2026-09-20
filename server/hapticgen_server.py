"""
Reference HaptiGen inference server for the AI-haptics Unity scene.

Why this exists: HaptiGen is a ~3.7 GB PyTorch model (built on Meta AudioCraft) that cannot run
on the Quest headset. Run this on a machine with an NVIDIA GPU; the headset calls it over HTTP.

Endpoints:

    POST /haptics   -- what the headset calls now. The whole prompt-generation process
        (context building + the Gemini chain) runs HERE, then feeds HaptiGen.
        body: {"event": str, "custom_name": str?, "hand": str,
               "numbers": {str: float}, "tags": {str: str},
               "duration_s": float, "seed": int}
        200 : audio/wav  (mono PCM16 vibrotactile waveform)
              headers: X-Haptic-Prompt (final prompt), X-Haptic-Source ("gemini"|"offline")

    POST /generate  -- low-level prompt -> wav (kept for direct testing / back-compat).
        body: {"prompt": str, "duration_s": float, "seed": int}
        200 : audio/wav

    GET  /health -> {"status": ..., "device": {...}, "gemini": true|false}

Gemini: set the GEMINI_API_KEY env var (optionally GEMINI_MODEL) to enable the real prompt
chain; without it /haptics uses the deterministic offline template. The key lives on this
server only -- it no longer ships inside the Unity APK.

Hardware: --device auto picks CUDA > Intel GPU (xpu) > NPU > CPU. On an NPU host the EnCodec
decode stage is offloaded to the NPU while the autoregressive LM stays in PyTorch -- see
device.py for why that split is the only one that makes sense.

Setup (see README.md for detail):
    git clone https://github.com/HapticGen/HapticGen
    # install its requirements + this file's requirements.txt, then:
    GEMINI_API_KEY=... python hapticgen_server.py \
        --weights /path/to/HapticGen-Weights --host 0.0.0.0 --port 8080
"""

import argparse
import io
import threading
import wave
from typing import Dict, Optional

import numpy as np
from fastapi import FastAPI, Response
from pydantic import BaseModel, Field

# The prompt-generation process that used to live on the headset now runs here.
from haptic_prompt import build_prompt, gemini_available
import device as device_mod
import haptic_quality
import npu_decode

# torch + HapticGen/AudioCraft are only needed to run the real model. They are optional so the
# server can still serve the numpy-only placeholder synth — enough to smoke-test the /haptics
# endpoint and the full Unity round-trip before the ~3.7 GB model and its deps are installed.
try:
    import torch
except ImportError:  # pragma: no cover - present on a configured GPU box
    torch = None

# HapticGen builds on AudioCraft. The exact loader mirrors the HapticGen repo's
# audiogen-beam-serve.py — adapt the two marked lines to the repo's current API.
try:
    from audiocraft.models import AudioGen  # provided by HapticGen's AudioCraft fork
except Exception:  # pragma: no cover - only importable on a configured GPU box
    AudioGen = None


class GenerateRequest(BaseModel):
    prompt: str
    duration_s: float = 1.0
    seed: int = 0
    candidates: int = 1  # generate N and keep the first perceptible one (HapticGen uses 5)


class HapticsRequest(BaseModel):
    """The raw world event the headset sends. Mirrors Unity's HapticEvent."""
    event: str                                              # Ignite | Spread | Extinguish | SprayTick | Custom
    custom_name: Optional[str] = None
    hand: str = "Right"
    numbers: Dict[str, float] = Field(default_factory=dict)  # e.g. intensity, distance, triggerDepth
    tags: Dict[str, str] = Field(default_factory=dict)       # e.g. surface = "TABLE"
    duration_s: float = 1.0
    seed: int = 0
    candidates: int = 1  # see GenerateRequest


app = FastAPI(title="HaptiGen Server")
_model = None
# Guards every use of _model. See _synthesize for why.
_model_lock = threading.Lock()
_device = device_mod.resolve_device("auto")  # replaced by --device at startup
_npu_status = "off"
# Placeholder-synth rate only. The real HapticGen model reports 8000 Hz (25 Hz frame rate) and we
# use model.sample_rate for it; Unity resamples whatever it gets to the ~320 Hz haptic rate.
_out_sample_rate = 16000

# Sampling defaults copied from HapticGen's own audiogen-beam-serve.py, so a clip generated here
# matches what the paper's demo produces. Changing these changes the character of every haptic.
_GEN_PARAMS = dict(use_sampling=True, top_k=250, top_p=0.0, temperature=1.0, cfg_coef=3.0)

# Acceptance thresholds live in haptic_quality so the server and the reporting tool agree.
# --candidates controls how many clips we generate before picking one.
_MIN_PEAK_AMPLITUDE = haptic_quality.MIN_PEAK_AMPLITUDE
_MIN_995TH_AMPLITUDE = haptic_quality.MIN_995TH_AMPLITUDE


def load_model(weights_path: str, choice=None):
    """Load HapticGen onto the resolved device and attach the NPU decoder if one was selected."""
    global _model, _npu_status
    if AudioGen is None:
        raise RuntimeError(
            "AudioCraft/HapticGen not importable. Install the HapticGen repo and its requirements."
        )
    choice = choice or _device
    # AudioGen is a plain wrapper class, not an nn.Module -- it has no .to(), so the device has to
    # go in here. (Calling model.to(...) afterwards raises AttributeError.)
    model = AudioGen.get_pretrained(weights_path, device=choice.torch_device)
    _npu_status = npu_decode.maybe_wrap(model, choice)
    _model = model
    return model


def _to_wav_bytes(samples: np.ndarray, sample_rate: int) -> bytes:
    samples = np.clip(samples, -1.0, 1.0)
    pcm16 = (samples * 32767.0).astype("<i2")
    buf = io.BytesIO()
    with wave.open(buf, "wb") as w:
        w.setnchannels(1)
        w.setsampwidth(2)
        w.setframerate(sample_rate)
        w.writeframes(pcm16.tobytes())
    return buf.getvalue()


def _pick_best(wavs):
    """
    Prefer the first candidate that is both perceptible (HapticGen's own filter) and actually
    shaped (ours). Falling back, a too-quiet clip beats a drone: quiet is a weak haptic, a drone
    is a buzz that masks everything else in the scene.

    Returns (samples, peak, p995, rms, degenerate) for the winner.
    """
    scored = []
    for wav in wavs:
        mono = wav.mean(dim=0).detach().cpu()
        absolute = mono.abs()
        peak = float(absolute.max())
        rms = float(mono.pow(2).mean().sqrt())
        scored.append((mono, peak, float(absolute.quantile(0.995)), rms,
                       haptic_quality.is_degenerate(peak, rms)))

    audible = [s for s in scored if s[1] >= _MIN_PEAK_AMPLITUDE and s[2] >= _MIN_995TH_AMPLITUDE]
    for candidate in audible:
        if not candidate[4]:
            return candidate
    if audible:  # every audible candidate was a drone -- take the least drone-like
        return min(audible, key=lambda s: s[3])
    return max(scored, key=lambda s: s[2])  # all too quiet -- best of a bad set


def _synthesize(prompt: str, duration_s: float, seed: int, candidates: int = 1):
    """
    Run HaptiGen (or the placeholder synth) for one prompt.
    Returns (samples, sample_rate, info) where info describes how the clip was produced.
    """
    if _model is not None:
        n = max(1, candidates)
        # One generation at a time. FastAPI runs these sync endpoints in a threadpool, so two
        # requests can land in here at once -- and the model is shared mutable state twice over:
        # set_generation_params writes to it, and AudioCraft keeps the LM's KV cache in each
        # attention module's _streaming_state. Overlapping generates interleave their appends to
        # past_keys/past_values until the two disagree, which trips the shape assert in
        # transformer.py's _complete_kv. Generation is compute-bound and already saturates the
        # device, so serialising costs no real throughput.
        with _model_lock:
            torch.manual_seed(seed)
            _model.set_generation_params(duration=max(0.25, duration_s), **_GEN_PARAMS)
            with torch.no_grad():
                wavs = _model.generate([prompt] * n)  # [batch, channels, samples]
            # Read inside the lock: the NPU decoder rewrites this per decode.
            backend = getattr(_model.compression_model, "last_backend", "torch")
            sr = int(getattr(_model, "sample_rate", _out_sample_rate))
        mono, peak, p995, rms, degenerate = _pick_best(wavs)
        samples = mono.numpy().astype("float32")
        info = {
            "backend": backend,
            "peak": peak,
            "p995": p995,
            "rms": rms,
            "candidates": n,
            "silent": peak < _MIN_PEAK_AMPLITUDE,
            "degenerate": degenerate,
        }
    else:
        # Fallback so the endpoint is testable before weights are wired up.
        n = int(max(0.25, duration_s) * _out_sample_rate)
        t = np.arange(n) / _out_sample_rate
        rng = np.random.default_rng(seed)
        samples = (np.exp(-4 * t) * np.sin(2 * np.pi * 80 * t)
                   + 0.3 * rng.standard_normal(n)).astype("float32")
        sr = _out_sample_rate
        info = {"backend": "placeholder", "peak": float(np.abs(samples).max()),
                "p995": float(np.quantile(np.abs(samples), 0.995)),
                "rms": float(np.sqrt(np.mean(samples ** 2))), "candidates": 1,
                "silent": False, "degenerate": False}
    return samples, sr, info


def _header_safe(text: str, limit: int = 400) -> str:
    """HTTP header values must be single-line ASCII; sanitise the prompt for X-Haptic-Prompt."""
    one_line = " ".join((text or "").split())
    return one_line.encode("ascii", "replace").decode("ascii")[:limit]


def _synth_headers(info: Dict) -> Dict[str, str]:
    """Expose how the clip was made, so a client (or an end-to-end test) can tell at a glance."""
    return {
        "X-Haptic-Device": _device.kind,
        "X-Haptic-Backend": str(info.get("backend", "torch")),   # npu | torch | placeholder
        "X-Haptic-Peak": f"{info.get('peak', 0.0):.4f}",
        "X-Haptic-Rms": f"{info.get('rms', 0.0):.4f}",
        "X-Haptic-Silent": "1" if info.get("silent") else "0",
        "X-Haptic-Degenerate": "1" if info.get("degenerate") else "0",
    }


@app.get("/health")
def health():
    return {
        "status": "ok" if _model is not None else "no-model",
        "device": {
            "kind": _device.kind,
            "torch": _device.torch_device,
            "openvino": _device.ov_device,
            "detail": _device.detail,
            "npu_offload": _npu_status,
        },
        "hardware": device_mod.probe(),
        "gemini": gemini_available(),
    }


@app.post("/generate")
def generate(req: GenerateRequest):
    samples, sr, info = _synthesize(req.prompt, req.duration_s, req.seed, req.candidates)
    return Response(content=_to_wav_bytes(samples, sr), media_type="audio/wav",
                    headers=_synth_headers(info))


@app.post("/haptics")
def haptics(req: HapticsRequest):
    """
    Full pipeline for one world event: cues -> context brief -> Gemini prompt chain ->
    HaptiGen -> audio/wav. This is what the Unity RemoteWaveformSource calls.
    """
    result = build_prompt(req.event, req.numbers, req.tags, req.custom_name)
    samples, sr, info = _synthesize(result.prompt, req.duration_s, req.seed, req.candidates)
    headers = _synth_headers(info)
    headers["X-Haptic-Prompt"] = _header_safe(result.prompt)
    headers["X-Haptic-Source"] = "gemini" if result.used_gemini else "offline"
    return Response(content=_to_wav_bytes(samples, sr), media_type="audio/wav", headers=headers)


if __name__ == "__main__":
    parser = argparse.ArgumentParser()
    parser.add_argument("--weights", default=None, help="Path/id of HapticGen-Weights (omit to run the fallback synth).")
    parser.add_argument("--host", default="0.0.0.0")
    parser.add_argument("--port", type=int, default=8080)
    parser.add_argument("--device", default="auto", choices=device_mod.VALID_PREFERENCES,
                        help="Accelerator to use. auto = cuda > xpu > npu > cpu.")
    parser.add_argument("--list-devices", action="store_true",
                        help="Print detected accelerators and exit.")
    args = parser.parse_args()

    if args.list_devices:
        for key, value in device_mod.probe().items():
            print(f"{key:18} {value}")
        raise SystemExit(0)

    _device = device_mod.resolve_device(args.device)
    print(f"Device: {_device.describe()}")

    if args.weights:
        load_model(args.weights, _device)
        print(f"HapticGen loaded (npu offload: {_npu_status})")
    else:
        print("No --weights given: serving fallback synth (for endpoint testing only).")

    import uvicorn
    uvicorn.run(app, host=args.host, port=args.port)
