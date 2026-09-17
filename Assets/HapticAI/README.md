# AI-generated haptics (`HapticAI`)

A modular pipeline that generates controller haptics at runtime instead of using a fixed rumble.

The build has two scenes:

| Scene | What it is |
|---|---|
| **Setup** (build index 0, the app's entry point) | The setup screen the person running the study uses: participant, condition, and the address of the laptop running the haptics server. `OperatorConsole`. |
| **FireTraining** | The experience itself. Carries both conditions — the fixed-rumble baseline and the AI pipeline — so a participant can be flipped between them without a rebuild. `FireTrainingBootstrap`. |

Everything the setup screen sets is saved on the headset (`StudySettings`), so a headset that has
been configured once opens on the same settings tomorrow. See **Running a session** below.

The AI condition routes every haptic through a backend service:

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

## Running a session (headset + laptop)

The headset and the laptop have to be on the same network; the laptop runs the server and the
headset calls it.

1. **On the laptop**, start the server bound to the network, not to loopback — `--host 0.0.0.0`,
   as in `/server/README.md`. A server bound to `127.0.0.1` is reachable from the laptop and from
   nowhere else, which looks identical to a wrong address from inside the headset.
2. Find the laptop's address on that network (`ipconfig` on Windows, `ip addr` on Linux) — the
   `192.168.x.x` / `10.x.x.x` one.
3. **Put the headset on.** The app opens on the setup screen.
4. Set **Server address** to the laptop's address (press A, then the stick: left/right picks a
   number, up/down changes it, hold to move faster, A to finish). The port is the server's, 8080
   by default.
5. **Test connection.** This asks the server for `/health`. Do it before every session: it is the
   only way to know the laptop is really reachable, and the alternative is finding out from a
   participant that every clip felt like a buzz.
6. Set **Participant** and **Haptics condition** for whoever is about to go in, leave **Participant
   can switch condition** locked, and press A on **Start the experience**.
7. Between participants, press **Y** at any briefing or result screen to come back here.

Everything is saved as it is changed, so steps 4–5 are a first-day-of-the-study job, not a
per-participant one — the headset comes back tomorrow already pointing at the laptop.

Controls on the setup screen: **stick** moves and changes, **A** selects, **Y** (held twice) resets
everything to defaults. In the Editor the arrow keys, Enter and Escape do the same, so the screen
can be driven without controllers.

### What each completed run records
Every run a participant finishes appends a line to `study-runs.csv` in the app's storage on the
headset: timestamp, participant, condition, time taken, fires extinguished, peak fires, clips
generated vs fallen back, and which source the haptics came from.

```bash
adb shell run-as <package> cat files/study-runs.csv
```

The result panel shows a participant the same numbers, but it shows them for thirty seconds to
somebody wearing a headset, which is not somewhere data can be read back from.

## Layout
| Path | Assembly | What |
|---|---|---|
| `Scripts/` (`HapticAI.Core.asmdef`) | HapticAI.Core | Pure, testable pipeline + settings — no OVR |
| `Scene/` | Assembly-CSharp | OVR glue + experience: `HapticPlayer`, `FireHapticRelay`, `TrainingSession`, `FireTrainingBootstrap`, `OperatorConsole` |
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

## Pointing Unity at the backend
The URL is resolved from the first of these that is set:

1. the `HAPTIC_SERVICE_URL` environment variable,
2. **the address saved on this headset from the setup scene** — and once that screen has been used,
   switching the server off there means off, rather than falling through to the sources below,
3. `Assets/StreamingAssets/hapticai.local.json` — copy
   `hapticai.config.template.json` next to it (the `.local.json` is gitignored) and set
   `hapticServiceUrl`,
4. **in the Editor only**, `http://localhost:8080`.

Step 2 sits above the shipped file because it is the only one the person running a study can
change, and it is a deliberate choice made minutes ago on that headset. Step 3 is still how you
point at a host*name* — the setup screen edits four numbers, so it can only express an IP address.
A headset that has never been through the setup screen behaves exactly as it did before: the screen
prefills itself from whatever the file or the environment variable says, so the first thing an
operator sees is the address the app would have used anyway.

Step 4 exists because the failure it prevents is silent. With no URL the factory quietly builds an
on-device mock pipeline: a running local server sits unused, and every clip is a hashed sine blob
that feels nothing like generated haptics. It is Editor-only, because on a headset "localhost" is
the headset. Turn it off with `useEditorLocalhostFallback` on the config asset.

Whatever is resolved is logged at startup, checked against the backend's `/health`, and shown on
the briefing panel in-headset — so "was the server actually reachable?" is never a guess.

The Gemini key is set on the **server** (`GEMINI_API_KEY`), not here.

### Cached clips are per-source
The persistent clip cache is keyed by a profile that includes sample rate, duration, gain, texture
depth **and** whether the clips came from the backend or the mock. Without that last part, one run
with no URL fills the cache with mock clips, and every later run — correctly configured or not —
finds every event already cached and never calls the backend at all.

## Timing (hybrid)
Common events are pre-warmed at scene load; first-time events play an instant procedural fallback
while the real clip generates in the background and is cached. See `Pipeline/HapticDirector.cs`.

The warm is skipped entirely when `/health` says the backend is down: every request would otherwise
sit through the full HTTP timeout before falling back to the on-device synth, which is instant and
needs no warming.

## Playback: cues vs sustained
`HapticPlayer` treats fire events and the extinguisher spray differently, and the distinction is
load-bearing:

- **Cues** (ignite / spread / extinguish / complete) `Preempt` the channel and play in full.
- **The spray** is a loop, fed as ~40 ms slices, so it plays seamlessly and stops within one slice
  of the trigger releasing.

Both used to go through one `Clear()` + `Queue()`, with the spray re-requesting every 100 ms. That
meant only the first 100 ms of any clip ever reached the controller — the same attack transient at
10 Hz, indistinguishable from a fixed rumble — and any fire cue raised while the trigger was held
was wiped within 100 ms.

## Texture (`textureDepth`)
A plain peak-envelope conversion throws away everything above the envelope, so a gritty crackle and
a clean thump differ only in attack shape and otherwise feel like the same buzz. `textureDepth` on
the config asset re-imposes the source's own roughness by modulating the envelope with a carrier
read out of the waveform's zero-crossing rate, octave-folded into the band the actuator can
actually reproduce (up to ~160 Hz at a 320 Hz sample rate).

Both the envelope and the carrier come from the generated waveform — nothing is invented — but it
is a rendering choice, so `textureDepth = 0` restores the plain-envelope behaviour if you want to
compare the two. Changing it invalidates the persistent cache automatically.
