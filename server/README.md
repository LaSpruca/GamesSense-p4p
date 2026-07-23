# Haptics inference server

The Quest headset can't run HaptiGen (a ~3.7 GB PyTorch model built on Meta AudioCraft), so this
HTTP server hosts it on a GPU machine and the headset calls it over the network.

As of the prompting move, **the whole prompt-generation process also runs here**: the headset
sends the raw world event and this server builds the context brief, runs the Gemini prompt chain,
and feeds the result into HaptiGen — all in one call. The headset just plays the returned waveform.

## Why off-device
- **HaptiGen** needs a PyTorch runtime + realistically a GPU; the Quest's XR2 has neither the
  runtime nor the memory headroom, and autoregressive generation is too slow for real time.
- **Gemini** now runs server-side too (`haptic_prompt.py`). This keeps the API key off the headset
  (it no longer ships inside the APK) and means Unity makes a single request per event.

## Setup (GPU box, Linux + NVIDIA recommended)
```bash
# 1. HapticGen + its AudioCraft fork
git clone https://github.com/HapticGen/HapticGen
cd HapticGen
pip install -r requirements.txt            # installs torch/torchaudio/audiocraft fork
# weights: https://huggingface.co/HapticGen/HapticGen-Weights

# 2. this server's extra deps
pip install -r /path/to/server/requirements.txt

# 3. run (GEMINI_API_KEY is optional — omit it to use the offline prompt template)
export GEMINI_API_KEY=...                   # optionally: export GEMINI_MODEL=gemini-2.5-flash
python /path/to/server/hapticgen_server.py \
    --weights /path/to/HapticGen-Weights --host 0.0.0.0 --port 8080
```
Instead of exporting the key you can drop a `server/.env` file next to the script with
`GEMINI_API_KEY=...` (and optionally `GEMINI_MODEL=...`); it's auto-loaded on startup via
python-dotenv. Keep `.env` out of git (a `server/.gitignore` already excludes it).
Running with no `--weights` serves a placeholder synth, and running with no `GEMINI_API_KEY` uses
the deterministic offline prompt template — so you can smoke-test the full Unity round-trip before
either the model or a Gemini key is wired up.

## Contract
| | |
|---|---|
| `POST /haptics` | body `{"event","custom_name"?,"hand","numbers":{},"tags":{},"duration_s","seed"}` → `audio/wav` (mono PCM16). Response headers: `X-Haptic-Prompt`, `X-Haptic-Source` (`gemini`\|`offline`). **This is what the headset calls.** |
| `POST /generate` | body `{"prompt","duration_s","seed"}` → `audio/wav`. Low-level prompt→wav, kept for direct testing. |
| `GET /health` | `{"status","device","gemini"}` |

`event` is one of `Ignite`, `Spread`, `Extinguish`, `SprayTick`, `Custom`. `numbers` carries cues
like `intensity`, `distance`, `triggerDepth`; `tags` carries e.g. `surface: "TABLE"`. The mapping
from these cues to a HaptiGen prompt lives in `haptic_prompt.py` (ported from the Unity pipeline).

## Point the headset at it
Put the URL in `Assets/StreamingAssets/hapticai.local.json`:
```json
{ "hapticServiceUrl": "http://<gpu-box-ip>:8080" }
```
Use a machine reachable from the headset's network; for quick tests an SSH/ngrok tunnel works. With
no URL configured (or if the server is unreachable) the headset falls back to fully on-device
offline synthesis, so the scene still runs.

## Adapting the model call
`load_model()` and `_synthesize()` are marked where they mirror the HapticGen repo's
`audiogen-beam-serve.py`. If the repo's inference API differs, adjust those spots — the HTTP
contract above is all the Unity client depends on.

## Changing prompt generation
Edit `haptic_prompt.py`: `build_brief()` maps event cues → a natural-language brief (add a cue by
handling a new `numbers`/`tags` key), and `DEFAULT_PROMPT_STAGES` is the Gemini chain (reword,
add, or reorder stages). No Unity rebuild needed — the headset only sends raw cues.
