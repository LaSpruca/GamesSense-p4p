"""
Reference HaptiGen inference server for the AI-haptics Unity scene.

Why this exists: HaptiGen is a ~3.7 GB PyTorch model (built on Meta AudioCraft) that cannot run
on the Quest headset. Run this on a machine with an NVIDIA GPU; the headset calls it over HTTP.

Contract (matches Assets/HapticAI/Scripts/Synth/HaptiGenHttpSynth.cs):

    POST /generate
        body: {"prompt": str, "duration_s": float, "seed": int}
        200 : audio/wav  (mono PCM16 vibrotactile waveform)

    GET  /health -> {"status": "ok", "device": "cuda"}

Setup (see README.md for detail):
    git clone https://github.com/HapticGen/HapticGen
    # install its requirements + this file's requirements.txt, then:
    python hapticgen_server.py --weights /path/to/HapticGen-Weights --host 0.0.0.0 --port 8080
"""

import argparse
import io
import wave

import numpy as np
import torch
from fastapi import FastAPI, Response
from pydantic import BaseModel

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


app = FastAPI(title="HaptiGen Server")
_model = None
_device = "cuda" if torch.cuda.is_available() else "cpu"
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


@app.get("/health")
def health():
    return {"status": "ok" if _model is not None else "no-model", "device": _device}


@app.post("/generate")
def generate(req: GenerateRequest):
    if _model is not None:
        torch.manual_seed(req.seed)
        _model.set_generation_params(duration=max(0.25, req.duration_s))
        with torch.no_grad():
            wav = _model.generate([req.prompt])  # [batch, channels, samples]
        samples = wav[0].mean(dim=0).detach().cpu().numpy().astype("float32")
        sr = int(getattr(_model, "sample_rate", _out_sample_rate))
    else:
        # Fallback so the endpoint is testable before weights are wired up.
        n = int(max(0.25, req.duration_s) * _out_sample_rate)
        t = np.arange(n) / _out_sample_rate
        rng = np.random.default_rng(req.seed)
        samples = (np.exp(-4 * t) * np.sin(2 * np.pi * 80 * t)
                   + 0.3 * rng.standard_normal(n)).astype("float32")
        sr = _out_sample_rate

    return Response(content=_to_wav_bytes(samples, sr), media_type="audio/wav")


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
