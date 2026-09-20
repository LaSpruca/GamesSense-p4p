# Haptics inference server

The Quest headset can't run HaptiGen (a ~3.7 GB PyTorch model built on Meta AudioCraft), so this
server hosts it on a laptop/GPU machine and the headset talks to it over the network.

The headset holds **one WebSocket** to this server for the whole session (`/ws`), and this server
holds **one WebSocket** to Gemini (the Live API) for the whole session. Nothing is set up per
event, which is what keeps both the start-up and the per-decision latency down.

```
headset ── ws /ws ──▶ server ── Gemini Live (ws) ──▶ plan_profiles / select_profile (tool calls)
                        │
                        └── HaptiGen (torch) ──▶ one clip per profile, streamed down as it finishes
```

## What happens on the socket
1. `scene` — the headset sends a snapshot (surfaces, room size, extinguisher, the stage rules).
   Gemini designs the **palette**: one HaptiGen prompt per haptic role (`ROLES` in
   `haptic_profiles.py`, mirrored by `HapticRoles.cs`). The plan is cached per scene on disk
   (`.plan_cache/`), so an unchanged room replans in milliseconds.
2. `profiles` — the plan goes back immediately; the headset checks its own store and asks for only
   the clips it lacks (`clips_needed`). Each is synthesised with HaptiGen (cached in `.clip_cache/`
   by prompt) and sent as a binary frame the moment it exists, loops first.
3. `state` — while the trigger is held the headset streams a spray reading ~8×/s. The rule-based
   selection goes back instantly when the situation changes; Gemini is asked (`select_profile`) at
   most about once a second and its answer is sent only if the situation it was asked about still
   holds. `event` messages (ignite, spread, …) give Gemini context.

Full message shapes are in `ws_session.py`; `ws_test.py` drives the whole flow from a terminal:

```bash
python server/ws_test.py --url ws://localhost:8080/ws            # plan, clips, scripted spray
python server/ws_test.py --no-clips                              # plan + selection only (fast)
python server/ws_test.py --refresh                               # ignore the cached plan
```

### Gemini
Set `GEMINI_API_KEY` (env var, or in `server/.env` — auto-loaded, gitignored). The Live model is
`gemini-3.8-live` (`GEMINI_LIVE_MODEL`); the Live models only speak audio, so structured answers are
obtained as **tool calls** (`plan_profiles`, `select_profile`) rather than text — which is also what
makes them reliably parseable. Measured: a plan in ~4 s, a selection in 1–2 s on an open session.

Everything degrades: Live unavailable → `generate_content` over HTTPS (`GEMINI_MODEL`,
`gemini-3.6-flash`) with JSON text; no key at all → the offline template palette and the rule
selector. The headset runs the same rules itself, so a slow or absent Gemini never delays feedback.

Quota: one planning turn per new scene, then at most ~1 selection turn per second *while spraying*.
Far fewer calls than the old three-per-event chain.

## HTTP endpoints (kept)
| | |
|---|---|
| `GET /health` | `{"status","device","hardware","gemini","gemini_live_model","ws","roles"}` — what the setup screen's **Test connection** asks. |
| `POST /haptics` | The previous request-per-event path: `{"event","numbers","tags",...}` → `audio/wav`. Kept for `e2e_test.py` and older clients. |
| `POST /generate` | `{"prompt","duration_s","seed","candidates"?}` → `audio/wav`. Low-level prompt→wav for direct testing. |

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

# 3. run (GEMINI_API_KEY is optional — omit it to use the offline palette)
export GEMINI_API_KEY=...
python /path/to/server/hapticgen_server.py \
    --weights /path/to/HapticGen-Weights --host 0.0.0.0 --port 8080
```
Running with no `--weights` serves a placeholder synth; with no `GEMINI_API_KEY` the offline
palette — so the full headset round-trip can be smoke-tested before either is wired up.

`--candidates N` (default 3) generates N clips per profile and keeps the first that is perceptible
and shaped; see *Two failure modes* below for why the default is above 1.

### Version pins that actually matter
HapticGen pins `torch==2.1.2`, and the modern versions of its transitive deps have moved past it:

| Package | Pin | Why |
|---|---|---|
| `numpy` | `<2` | torch 2.1.2 is built against the NumPy 1.x C API. |
| `transformers` | `4.x` (e.g. `4.40.2`) | transformers 5 requires torch ≥ 2.4 and silently disables the PyTorch backend on 2.1.2. |
| `huggingface_hub` | `<1.0` | transformers 4.x needs the pre-1.0 API. |
| `xformers` | `0.0.23.post1` | The only release built for torch 2.1.2. |

```bash
pip install "numpy<2" "transformers==4.40.2" "huggingface_hub<1.0" "xformers==0.0.23.post1"
```

## Hardware / accelerators
`--device auto` (the default) picks **cuda → xpu (Intel GPU) → cpu**. `--list-devices` prints what
was detected; `/health` reports the same at runtime.

`--device npu` offloads only the EnCodec decode stage to an Intel NPU via OpenVINO (the
autoregressive LM stays in PyTorch — NPUs compile per static shape, and the LM's KV cache changes
shape every step). Measured on a Core Ultra 7 155U: decode 234 → 146 ms, full generation ~33 s
either way, and a ~22-minute one-off OpenVINO compile per clip duration (cached in `.ov_cache/`).
That is why it is opt-in, and why **every profile is 1.0 s long** (`ROLES` in `haptic_profiles.py`):
1.0 s is the shape already cached, and a new duration would stall the first session for the length
of a compile. Everything falls back to PyTorch with a logged reason.

## Two failure modes worth knowing about
- **Silence.** The model emits clips below the perceptibility floor. HapticGen's own filter catches
  this; `X-Haptic-Silent: 1` on the HTTP path, and the WebSocket path re-rolls via `--candidates`.
- **Constant drone.** Instead of a shaped effect the model emits a near-full-amplitude buzz (RMS
  ~0.62; real effects 0.07–0.25). Crest factor separates them (drones ~1.5, real >3), so
  `haptic_quality.py` rejects them. A drone in a *looping spray profile* would mask every other
  haptic for the whole session, which is why the socket path defaults to 3 candidates. Drones and
  silent clips are never written to the clip cache, so the next session re-rolls them.

## Testing and visualising
`e2e_test.py` exercises the HTTP path and prints the controller amplitude envelope (a stdlib port
of `WaveformToAmplitudes.cs`); `visualise.py` turns saved clips into an HTML report. Both are
unchanged:

```bash
python server/e2e_test.py --url http://localhost:8080 --all
python server/ws_test.py --save clip && python server/visualise.py --glob "clip-*.wav" --out haptic-report.html
```

## Point the headset at it
Set the address on the headset's setup screen (or put it in
`Assets/StreamingAssets/hapticai.local.json` as `{"hapticServiceUrl": "http://<laptop-ip>:8080"}`).
The headset derives `ws://<laptop-ip>:8080/ws` from it. Bind with `--host 0.0.0.0` so the headset
can reach it; with no reachable server the headset runs the same palette on-device.
