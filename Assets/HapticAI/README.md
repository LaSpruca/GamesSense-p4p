# AI-generated haptics (`HapticAI`)

A modular pipeline that generates controller haptics at runtime instead of using a fixed rumble.
Used by the **FireExtinguisherTrainingAI** scene, which mirrors `FireExtinguisherTraining` but
routes every haptic through a backend service:

```
world event ──▶ HapticDirector ──▶ RemoteWaveformSource ──▶ 🖥 backend /haptics ──▶ waveform→amplitudes ──▶ controller
 (cues)          cache / fallback     (one HTTP call)         context + Gemini + HaptiGen    WaveformToAmplitudes   OVRHapticsClip
```

The headset now does **one** thing per event: send the raw cues and play the audio it gets back.
All of the prompt generation — context building **and** the Gemini chain — plus HaptiGen synthesis
happens on the server (`/server`). See `/server/README.md`.

## Where the AI runs (and why not on the headset)
- **Gemini + HaptiGen** → both run on the backend GPU box you host (`/server`). The ~3.7 GB
  HaptiGen model can't run on the Quest, and moving the Gemini chain server-side keeps the API key
  off the headset (it no longer ships in the APK) and collapses two network calls into one.
- The headset **just orchestrates timing and playback**; generation is a single call behind
  `IHapticWaveformSource`.

## Runs out of the box (no server)
With no `hapticServiceUrl` configured, the pipeline uses the on-device **offline source**
(`LocalWaveformSource`: offline prompt template + mock synth), so the scene plays real generated
*waveforms* on-device immediately. Set a `hapticServiceUrl` to route generation through the backend
— no code change. If the backend is unreachable at runtime, the remote source automatically falls
back to the same on-device offline synthesis, so the scene always responds.

## Layout
| Path | Assembly | What |
|---|---|---|
| `Scripts/` (`HapticAI.Core.asmdef`) | HapticAI.Core | Pure, testable pipeline — no OVR |
| `Scene/` | Assembly-CSharp | OVR glue: `HapticPlayer`, `FireHapticRelay`, `FireSimAIBootstrap` |
| `Tests/` (`HapticAI.Tests.asmdef`) | EditMode | Unit tests for the pure logic |
| `HapticAIConfig.asset` | — | Inspector config (offline synth, cache, request timeout) |

The two waveform sources live in `Scripts/Pipeline/`: `RemoteWaveformSource` (backend call) and
`LocalWaveformSource` (on-device offline). `Scripts/Prompt/` now holds only the offline template
(`OfflinePromptStage`); the real prompt chain lives in `/server/haptic_prompt.py`.

## How to vary prompt generation (the modular part)
- **Feed in a new environmental cue**: raise it on the `HapticEvent` (`evt.WithNumber(...)` /
  `evt.WithTag(...)`) where the event is created (`Scene/FireHapticRelay.cs`). It is sent to the
  backend as-is; teach the server to use it in `/server/haptic_prompt.py` (`build_brief`).
- **Change the LLM chain**: edit `DEFAULT_PROMPT_STAGES` in `/server/haptic_prompt.py` — reword,
  add, or reorder stages. No Unity rebuild; the headset only sends raw cues.
- **Swap the whole source**: implement `IHapticWaveformSource` and wire it in
  `Scripts/Config/HapticPipelineFactory.cs`.

## Secrets
Copy `Assets/StreamingAssets/hapticai.config.template.json` →
`Assets/StreamingAssets/hapticai.local.json` (gitignored) and set `hapticServiceUrl` to your
backend. Leave it empty to run fully on-device. The Gemini key is set on the **server**
(`GEMINI_API_KEY`), not here.

## Timing (hybrid)
Common events are pre-warmed at scene load; first-time events play an instant procedural fallback
while the real clip generates in the background and is cached. See `Pipeline/HapticDirector.cs`.
