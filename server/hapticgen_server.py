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

    GET  /health -> {"status": ..., "device": "cuda", "gemini": true|false}

Gemini: set the GEMINI_API_KEY env var (optionally GEMINI_MODEL) to enable the real prompt
chain; without it /haptics uses the deterministic offline template. The key lives on this
server only -- it no longer ships inside the Unity APK.

Setup (see README.md for detail):
    git clone https://github.com/HapticGen/HapticGen
    # install its requirements + this file's requirements.txt, then:
    GEMINI_API_KEY=... python hapticgen_server.py \
        --weights /path/to/HapticGen-Weights --host 0.0.0.0 --port 8080
"""

import argparse
import io
import wave
from typing import Dict, Optional

import numpy as np
from fastapi import FastAPI, Response
from pydantic import BaseModel, Field

# The prompt-generation process that used to live on the headset now runs here.
from haptic_prompt import build_prompt, gemini_available

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


class HapticsRequest(BaseModel):
    """The raw world event the headset sends. Mirrors Unity's HapticEvent."""
    event: str                                              # Ignite | Spread | Extinguish | SprayTick | Custom
    custom_name: Optional[str] = None
    hand: str = "Right"
    numbers: Dict[str, float] = Field(default_factory=dict)  # e.g. intensity, distance, triggerDepth
    tags: Dict[str, str] = Field(default_factory=dict)       # e.g. surface = "TABLE"
    duration_s: float = 1.0
    seed: int = 0


app = FastAPI(title="HaptiGen Server")
_model = None
_device = "cuda" if (torch is not None and torch.cuda.is_available()) else "cpu"
_out_sample_rate = 16000  # HaptiGen operates at audio rate; Unity resamples to the haptic rate.


def load_model(weights_path: str):
    global _model
    if AudioGen is None:
        raise RuntimeError(
            "AudioCraft/HapticGen not importable. Install the HapticGen repo and its requirements."
        )
    # --- adapt to HapticGen repo ---
    model = AudioGen.get_pretrained(weights_path)  # load fine-tuned HapticGen weights
    model.to(_device)
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


def _synthesize(prompt: str, duration_s: float, seed: int):
    """Run HaptiGen (or the placeholder synth) for one prompt. Returns (samples, sample_rate)."""
    if _model is not None:
        torch.manual_seed(seed)
        _model.set_generation_params(duration=max(0.25, duration_s))
        with torch.no_grad():
            wav = _model.generate([prompt])  # [batch, channels, samples]
        samples = wav[0].mean(dim=0).detach().cpu().numpy().astype("float32")
        sr = int(getattr(_model, "sample_rate", _out_sample_rate))
    else:
        # Fallback so the endpoint is testable before weights are wired up.
        n = int(max(0.25, duration_s) * _out_sample_rate)
        t = np.arange(n) / _out_sample_rate
        rng = np.random.default_rng(seed)
        samples = (np.exp(-4 * t) * np.sin(2 * np.pi * 80 * t)
                   + 0.3 * rng.standard_normal(n)).astype("float32")
        sr = _out_sample_rate
    return samples, sr


def _header_safe(text: str, limit: int = 400) -> str:
    """HTTP header values must be single-line ASCII; sanitise the prompt for X-Haptic-Prompt."""
    one_line = " ".join((text or "").split())
    return one_line.encode("ascii", "replace").decode("ascii")[:limit]


@app.get("/health")
def health():
    return {
        "status": "ok" if _model is not None else "no-model",
        "device": _device,
        "gemini": gemini_available(),
    }


@app.post("/generate")
def generate(req: GenerateRequest):
    samples, sr = _synthesize(req.prompt, req.duration_s, req.seed)
    return Response(content=_to_wav_bytes(samples, sr), media_type="audio/wav")


@app.post("/haptics")
def haptics(req: HapticsRequest):
    """
    Full pipeline for one world event: cues -> context brief -> Gemini prompt chain ->
    HaptiGen -> audio/wav. This is what the Unity RemoteWaveformSource calls.
    """
    result = build_prompt(req.event, req.numbers, req.tags, req.custom_name)
    samples, sr = _synthesize(result.prompt, req.duration_s, req.seed)
    headers = {
        "X-Haptic-Prompt": _header_safe(result.prompt),
        "X-Haptic-Source": "gemini" if result.used_gemini else "offline",
    }
    return Response(content=_to_wav_bytes(samples, sr), media_type="audio/wav", headers=headers)


if __name__ == "__main__":
    parser = argparse.ArgumentParser()
    parser.add_argument("--weights", default=None, help="Path/id of HapticGen-Weights (omit to run the fallback synth).")
    parser.add_argument("--host", default="0.0.0.0")
    parser.add_argument("--port", type=int, default=8080)
    args = parser.parse_args()

    if args.weights:
        load_model(args.weights)
        print(f"HapticGen loaded on {_device}")
    else:
        print("No --weights given: serving fallback synth (for endpoint testing only).")

    import uvicorn
    uvicorn.run(app, host=args.host, port=args.port)
