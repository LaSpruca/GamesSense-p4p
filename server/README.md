# HaptiGen inference server

The Quest headset can't run HaptiGen (a ~3.7 GB PyTorch model built on Meta AudioCraft), so this
small HTTP server hosts it on a GPU machine and the headset calls it over the network.

## Why off-device
- **HaptiGen** needs a PyTorch runtime + realistically a GPU; the Quest's XR2 has neither the
  runtime nor the memory headroom, and autoregressive generation is too slow for real time.
- **Gemini** is called separately, straight from the headset to Google's cloud REST API (Gemini
  Nano's on-device path needs Android AICore, which Horizon OS doesn't have).

## Setup (GPU box, Linux + NVIDIA recommended)
```bash
# 1. HapticGen + its AudioCraft fork
git clone https://github.com/HapticGen/HapticGen
cd HapticGen
pip install -r requirements.txt            # installs torch/torchaudio/audiocraft fork
# weights: https://huggingface.co/HapticGen/HapticGen-Weights

# 2. this server's extra deps
pip install -r /path/to/server/requirements.txt

# 3. run
python /path/to/server/hapticgen_server.py \
    --weights /path/to/HapticGen-Weights --host 0.0.0.0 --port 8080
```
Running with no `--weights` serves a placeholder synth so you can smoke-test the endpoint and the
Unity round-trip before the model is wired up.

## Contract
| | |
|---|---|
| `POST /generate` | body `{"prompt", "duration_s", "seed"}` → `audio/wav` (mono PCM16) |
| `GET /health` | `{"status","device"}` |

## Point the headset at it
Put the URL in `Assets/StreamingAssets/hapticai.local.json`:
```json
{ "geminiApiKey": "…", "hapticGenUrl": "http://<gpu-box-ip>:8080" }
```
and set the `HapticAIConfig` asset's **provider** to **HaptiGenHttp**. Use a machine reachable
from the headset's network; for quick tests an SSH/ngrok tunnel works.

## Adapting the model call
`load_model()` and `generate()` are marked where they mirror the HapticGen repo's
`audiogen-beam-serve.py`. If the repo's inference API differs, adjust those two spots — the HTTP
contract above is all the Unity client depends on.
