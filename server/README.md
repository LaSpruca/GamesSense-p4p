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

## Hardware / accelerators
`--device auto` (the default) picks **cuda → xpu (Intel GPU) → cpu**.
`--list-devices` prints what was detected and exits; `/health` reports the same at runtime.

```bash
python hapticgen_server.py --list-devices
python hapticgen_server.py --weights ... --device npu    # opt-in, see below
```

**What `--device npu` actually does.** HapticGen is two very different models stapled together, and
only one half suits an NPU:

| Stage | Where it runs | Why |
|---|---|---|
| Autoregressive LM (1.5B) | best torch device (cuda/xpu/cpu) | KV cache grows every step, so the shape changes every step. NPUs compile per static shape — stepping an LM on one is a losing trade. |
| EnCodec decode | **NPU**, via OpenVINO | Fixed-shape conv stack, run once per clip. Exactly what an NPU is good at. |

So `npu` means "LM in PyTorch, decode on the NPU", not "the whole model on the NPU".

**Measured** on an Intel Core Ultra 7 155U (AI Boost NPU), 1 s clip, CPU-only otherwise:

| | CPU | NPU |
|---|---|---|
| EnCodec decode | 234 ms | **146 ms** (1.6×, matches PyTorch to 0.05% relative error) |
| Full generation | ~33 s | ~33 s (**~0.3%** better — the LM dominates) |
| First compile, per clip duration | — | **~22 min**, then cached (~8 s on later starts) |

That is why the NPU is **not** in the `auto` chain: a 22-minute first-run stall for a sub-1%
end-to-end win is a bad default. It becomes worth enabling if the LM ever moves to a GPU, since
decode is then a much larger share of the total. Compiled blobs cache to `server/.ov_cache/`
(~67 MB per shape) — keep that directory between runs.

Everything degrades gracefully: no `openvino` installed, no NPU present, or a stage that won't
compile all fall back to PyTorch with a logged reason. Enabling the NPU can't break generation.
Requires `pip install openvino` (in `requirements.txt`).

## Setup (GPU box, Linux + NVIDIA recommended)
```bash
# 1. HapticGen + its AudioCraft fork
git clone https://github.com/HapticGen/HapticGen
cd HapticGen
pip install -r requirements.txt            # installs torch/torchaudio/audiocraft fork
pip install -e . --no-deps                 # makes `audiocraft` importable from anywhere
# weights: https://huggingface.co/HapticGen/HapticGen-Weights
#   -> a directory holding state_dict.bin (3.7 GB) + compression_state_dict.bin (236 MB)

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

### Version pins that actually matter
HapticGen pins `torch==2.1.2`, and the modern versions of its transitive deps have moved past it.
Installing `requirements.txt` as-is on a fresh box gets you a stack that imports but cannot run:

| Package | Pin | Why |
|---|---|---|
| `numpy` | `<2` | torch 2.1.2 is built against the NumPy 1.x C API. NumPy 2 gives `Failed to initialize NumPy: _ARRAY_API not found` and tensor↔array conversion breaks. |
| `transformers` | `4.x` (e.g. `4.40.2`) | transformers 5 requires torch ≥ 2.4 and *silently disables the PyTorch backend* on 2.1.2 — the T5 text conditioner AudioGen needs then can't load. |
| `huggingface_hub` | `<1.0` | transformers 4.x needs the pre-1.0 API. |
| `xformers` | `0.0.23.post1` | The only release built for torch 2.1.2. Unpinned, pip backtracks into a source build. On a CPU-only box it warns that CUDA kernels are unavailable — harmless, attention falls back to the torch path. |

```bash
pip install "numpy<2" "transformers==4.40.2" "huggingface_hub<1.0" "xformers==0.0.23.post1"
```
On Windows, pin `xformers` explicitly — letting pip resolve it triggers a source build that fails
on the 260-char path limit unless long paths are enabled.

## End-to-end test
`e2e_test.py` sends an event exactly as the headset does and prints the **controller amplitude
envelope** — a stdlib-only port of `WaveformToAmplitudes.cs`, so what it draws is what the Quest
would play. Use it to check the haptics are perceptible, not just that HTTP worked.

```bash
python server/e2e_test.py --url http://localhost:8080 --all
python server/e2e_test.py --scenes --save clip          # the whole scene library (scenes.py)
python server/e2e_test.py --event Ignite --intensity 0.9 --candidates 5 --save clip
```

## Visualising the results
`visualise.py` turns saved clips into a browser report — one panel per clip, drawn as the
controller envelope (it imports `e2e_test.to_amplitudes`, so the picture is what the hardware
plays). A drone shows up instantly as a flat line pinned at 255.

```bash
# interactive HTML report (self-contained; hover tooltips, table view, dark mode)
python server/visualise.py --glob "%TEMP%/scene-*.wav" --out haptic-report.html

# plus one standalone .svg per clip, for slides/papers/issues
python server/visualise.py --glob "%TEMP%/scene-*.wav" --out haptic-report.html \
    --svg-dir haptic-svg [--svg-dark]
```

For a **PNG** of the whole report, screenshot it headlessly — no extra dependency:
```powershell
& "C:\Program Files (x86)\Microsoft\Edge\Application\msedge.exe" --headless=new --disable-gpu `
    --hide-scrollbars --window-size=1400,2600 --screenshot="haptic-report.png" `
    --virtual-time-budget=4000 "file:///<abs-path>/haptic-report.html"
```
Any Chromium (`chrome`, `msedge`) takes the same flags. Use an absolute `file:///` URL and
percent-encode spaces. The per-clip SVGs convert the same way, or via Inkscape/`rsvg-convert`.
Both `haptic-report.html` and `haptic-svg/` are gitignored — they are build artefacts.

## Contract
| | |
|---|---|
| `POST /haptics` | body `{"event","custom_name"?,"hand","numbers":{},"tags":{},"duration_s","seed","candidates"?}` → `audio/wav` (mono PCM16). Response headers: `X-Haptic-Prompt`, `X-Haptic-Source` (`gemini`\|`offline`), `X-Haptic-Device`, `X-Haptic-Backend` (`npu`\|`torch`\|`placeholder`), `X-Haptic-Peak`, `X-Haptic-Silent`. **This is what the headset calls.** |
| `POST /generate` | body `{"prompt","duration_s","seed","candidates"?}` → `audio/wav`. Low-level prompt→wav, kept for direct testing. |
| `GET /health` | `{"status","device":{...},"hardware":{...},"gemini"}` |

`candidates` generates N clips and returns the first good one (HapticGen's own serve script uses 5).
Cost scales linearly with N — on CPU each candidate is a full generation.

### Two failure modes worth knowing about
Measured over a 12-scene sweep (`e2e_test.py --scenes`), 1.5 s clips, `candidates: 1`:

- **Silence.** The model emits clips below the perceptibility floor. HapticGen's own filter catches
  this; `X-Haptic-Silent: 1` flags it.
- **Constant drone.** *Not* caught by HapticGen's filter, and it hit **4 of 12 scenes**. Instead of
  a shaped effect the model emits a continuous near-full-amplitude buzz — RMS ~0.62, where a
  full-scale sine is 0.707. It renders as a solid 255 block: no attack, no decay, nothing to feel,
  and it masks everything else in the scene. Real effects in the same sweep were RMS 0.07–0.25.
  Crest factor separates them cleanly (drones ~1.5, real effects >3), so the server now rejects
  them and flags `X-Haptic-Degenerate: 1`.

Lowering the Unity-side gain does **not** fix a drone — at gain 1.0 those clips still average
224/255. Re-rolling does: the same scene at `--candidates 4` came back RMS 0.18 with a clean
attack-and-decay. **Use `candidates: 3`+ in anything user-facing.**

### Gemini quota
The chain is **3 LLM calls per haptic event**. A free-tier key exhausted its quota after 5 scenes
(15 calls) mid-sweep; the remaining 7 fell back to the offline template (`X-Haptic-Source:
offline`), which is why the fallback exists. For a sim raising events continuously this is the
binding constraint — cache aggressively per event+cue bucket (Unity already does, see
`HapticClipCache`), collapse the chain to one call, or budget for paid quota.

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
