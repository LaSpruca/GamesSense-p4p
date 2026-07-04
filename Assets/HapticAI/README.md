# AI-generated haptics (`HapticAI`)

A modular pipeline that generates controller haptics at runtime instead of using a fixed rumble.
Used by the **FireExtinguisherTrainingAI** scene, which mirrors `FireExtinguisherTraining` but
routes every haptic through:

```
world event ──▶ ContextBuilder ──▶ PromptChain (Gemini) ──▶ HaptiGen ──▶ waveform→amplitudes ──▶ controller
 (cues)          IContextContributor   IPromptStage[]        IHapticSynthProvider   WaveformToAmplitudes   OVRHapticsClip
```

Each arrow is an interface, so any stage swaps without touching the others.

## Where the AI runs (and why not on the headset)
- **Gemini** → Google's cloud REST API, called straight from the headset. On-device Gemini Nano
  needs Android AICore, which Horizon OS doesn't ship.
- **HaptiGen** → a GPU server you host (`/server`). The ~3.7 GB PyTorch model can't run on the
  Quest. See `/server/README.md`.
- The headset **orchestrates**; both AI stages are network calls behind interfaces.

## Runs out of the box (no servers)
With no secrets file, the pipeline uses the **offline prompt stage** + **mock synth**, so the
scene plays real generated *waveforms* on-device immediately. Add a Gemini key and/or a HaptiGen
URL to swap in the real stages — no code change.

## Layout
| Path | Assembly | What |
|---|---|---|
| `Scripts/` (`HapticAI.Core.asmdef`) | HapticAI.Core | Pure, testable pipeline — no OVR |
| `Scene/` | Assembly-CSharp | OVR glue: `HapticPlayer`, `FireHapticRelay`, `FireSimAIBootstrap` |
| `Tests/` (`HapticAI.Tests.asmdef`) | EditMode | Unit tests for the pure logic |
| `HapticAIConfig.asset` | — | Inspector config (provider, prompt chain, cache) |

## How to vary prompt generation (the modular part)
- **Feed in a new environmental cue**: raise it on the `HapticEvent` (`evt.WithNumber(...)` /
  `evt.WithTag(...)`) where the event is created (`Scene/FireHapticRelay.cs`), then add an
  `IContextContributor` that reads it and add it to the list in
  `Scripts/Config/HapticPipelineFactory.cs`.
- **Change the LLM chain**: edit the `promptStages` list on the `HapticAIConfig` asset — reword a
  stage, add a stage (e.g. a "user skill level" pass), or reorder them. No recompile.
- **Swap the synth or LLM**: implement `IHapticSynthProvider` or `IPromptStage` and wire it in the
  factory.

## Secrets
Copy `Assets/StreamingAssets/hapticai.config.template.json` →
`Assets/StreamingAssets/hapticai.local.json` (gitignored) and fill in `geminiApiKey` /
`hapticGenUrl`.

## Timing (hybrid)
Common events are pre-warmed at scene load; first-time events play an instant procedural fallback
while the real clip generates in the background and is cached. See `Pipeline/HapticDirector.cs`.
