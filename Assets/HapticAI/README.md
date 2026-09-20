# AI-generated haptics (`HapticAI`)

Controller haptics for the fire-extinguisher drill that are *designed for the room you are in*
and *chosen for what your hand is doing*, instead of a fixed rumble.

The build has two scenes:

| Scene | What it is |
|---|---|
| **Setup** (build index 0, the app's entry point) | The setup screen the person running the study uses: participant, condition, and the address of the laptop running the haptics server. `OperatorConsole`. |
| **FireTraining** | The experience itself: five training stages (`FireSim/TrainingStage.cs`). Carries both conditions — the fixed-rumble baseline and the AI pipeline — so a participant can be flipped between them without a rebuild. `FireTrainingBootstrap`. |

Everything the setup screen sets is saved on the headset (`StudySettings`), so a headset that has
been configured once opens on the same settings tomorrow. See **Running a session** below.

## How the AI condition works

```
 session start                       while the trigger is held (every frame)
 ─────────────                       ────────────────────────────────────────
 room scan ──▶ scene snapshot ──▶ 🖥 server: Gemini plans a PALETTE     fire sim telemetry ──▶ rule selector ──▶ controller
 + stage list      (one message)      (one profile per role) and         (foam on base? tips?     instant        (loop + gain)
                                      HaptiGen synthesises each clip     sweeping? soaking?)         ▲
                                              │                                 │                   │ override while the
                                              ▼ clips stream down               ▼ readings ~8/s     │ situation holds
                                      headset palette + store             🖥 server: Gemini picks the profile ─┘
```

1. **Plan once.** As soon as the room is known, the headset sends one *scene snapshot* over the
   session socket: the surfaces MRUK found, the room size, the extinguisher, and the rules of every
   stage. Gemini (over its Live API) designs one HaptiGen prompt per haptic **role** — the palette —
   and the server synthesises each and streams the clips down as they finish. The roles are fixed
   (`Profiles/HapticRoles.cs`, mirrored by `server/haptic_profiles.py`): five looping spray states
   (`spray_dry`, `spray_top`, `spray_base`, `spray_sweep`, `spray_soak`) and six one-shot cues
   (`ignite`, `spread`, `extinguish`, `reignite`, `smoulder_warning`, `stage_complete`).
2. **Choose continuously.** The fire sim measures what the foam is doing (`SurfaceFireManager.Telemetry`):
   particles per second landing on fire, how high up the flames they land, whether the nozzle is
   sweeping, whether it is soaking a smouldering patch. `FireHapticRelay` turns that into a
   `SprayReading` every frame, the rule-based `HapticProfileSelector` picks a looping role and gain
   on the spot, and the same reading streams to the server. When Gemini's choice comes back it
   overrides the rules — but only while the coarse situation it was asked about still holds
   (`state_key`), so a change from missing to hitting the base is felt on the frame it happens and a
   stale answer can never pin the wrong feel to a new situation.
3. **Never wait.** The briefing appears as soon as the palette *plan* arrives (sub-second when the
   room has been seen before). Clips still in flight are covered by per-role procedural stand-ins
   (`Playback/FallbackHaptics.cs`) that already differ in the right direction (base dense, top thin,
   dry hollow), and each real clip swaps in mid-loop as it lands. A palette that has been on this
   headset before is restored from disk (`HapticProfileStore`) and costs the server nothing.

The Gemini key lives on the **server** (`GEMINI_API_KEY`), never on the headset. See `/server/README.md`.

## Runs out of the box (no server)
With no `hapticServiceUrl` configured — or if the socket has not connected within
`connectTimeoutSeconds` — the same palette is built on-device from template prompts and synthesised
by the mock synth (`OfflineProfileSynth`). Same roles, same selector, just less interesting clips.
If the socket comes up later, the server's palette replaces it.

## Running a session (headset + laptop)

The headset and the laptop have to be on the same network; the laptop runs the server and the
headset connects to it.

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
   only way to know the laptop is really reachable.
6. Set **Participant** and **Haptics condition** for whoever is about to go in, leave **Participant
   can switch condition** locked, and press A on **Start the experience**.
7. Between participants, press **Y** at any briefing or result screen to come back here.

Controls on the setup screen: **stick** moves and changes, **A** selects, **Y** (held twice) resets
everything to defaults. In the Editor the arrow keys, Enter and Escape do the same.

### The stages
The drill is five stages, each adding one PASS habit (`Assets/FireSim/Scripts/TrainingStage.cs`):

| # | Stage | What it adds |
|---|---|---|
| 1 | First flames | One fire, no spread. Any hit on the flames counts. |
| 2 | Aim at the base | Foam high on the flames does little; the fire starts to spread. |
| 3 | Sweep side to side | The base is split into sectors that must each be doused. |
| 4 | Watch for re-ignition | A knocked-down patch smoulders; walk away and it flares back. Soak it. |
| 5 | Full room | Two fires, fast spread, strict aim, sweep and re-ignition all at once. |

After each stage the panel shows a **stage complete** notice with the time, fires put out, the
share of foam that landed on the base, and (from stage 4) how many patches flared back; the
`stage_complete` cue plays on the controller. **A** goes to the next stage's briefing; after the
last, a summary of every stage and **A** starts over.

### What each completed stage records
Every stage a participant finishes appends a line to `study-runs.csv` in the app's storage on the
headset: timestamp, participant, condition, stage, time taken, fires extinguished, re-ignitions,
peak fires, base-hit fraction, clips generated vs stand-in, how many loop switches Gemini decided,
and the haptic source.

```bash
adb shell run-as nz.laspruca.hapticai cat files/study-runs.csv
```

## Layout
| Path | Assembly | What |
|---|---|---|
| `Scripts/Profiles/` | HapticAI.Core | `HapticRoles` (the vocabulary), `HapticProfileSet`/`Library`/`Store` (the palette and its clips), `HapticProfileSelector` (rules + coarse state key), `HapticWire` (socket messages), `OfflineProfileSynth` |
| `Scripts/Net/` | HapticAI.Core | `HapticSessionClient` — the WebSocket to the server (`ClientWebSocket`, no package needed) |
| `Scripts/Playback/` | HapticAI.Core | `WaveformToAmplitudes` (audio-rate → controller amplitudes), `FallbackHaptics` (per-role stand-ins) |
| `Scripts/Synth/` | HapticAI.Core | `WavCodec`, `Waveform`, `MockHapticSynth` (on-device stand-in for HaptiGen) |
| `Scripts/Config/` | HapticAI.Core | `HapticAIConfig` asset, `HapticPipelineFactory` (the one wiring point), `HapticSecrets` (URL resolution), `StudySettings` |
| `Scene/` | Assembly-CSharp | OVR glue + experience: `HapticPlayer`, `FireHapticRelay`, `TrainingSession`, `FireTrainingBootstrap`, `OperatorConsole`, `StudyLog` |
| `Tests/` | EditMode | Unit tests for the pure logic |

## How to vary it
- **Add a role**: add it to `HapticRoles.All` *and* `ROLES` in `server/haptic_profiles.py` with the
  same name, give it a stand-in shape in `FallbackHaptics`, and fire or select it from the relay.
- **Change what Gemini knows about the scene**: `FireTrainingBootstrap.BuildSceneSnapshot` (headset)
  and `scene_key()` on the server, which decides when a plan can be reused.
- **Change the selection rules**: `HapticProfileSelector` and `rule_select()` on the server must
  agree on `CoarseKey`, or a Gemini choice could be pinned to the wrong situation.
- **Change how Gemini is prompted**: `DIRECTOR_SYSTEM` and the tool schemas in
  `server/haptic_profiles.py`. No Unity rebuild.

## Pointing Unity at the backend
The URL is resolved from the first of these that is set:

1. the `HAPTIC_SERVICE_URL` environment variable,
2. **the address saved on this headset from the setup scene** — and once that screen has been used,
   switching the server off there means off, rather than falling through to the sources below,
3. `Assets/StreamingAssets/hapticai.local.json` — copy `hapticai.config.template.json` next to it
   (the `.local.json` is gitignored) and set `hapticServiceUrl`,
4. **in the Editor only**, `http://localhost:8080`.

The socket URL is derived from it (`http://host:port` → `ws://host:port/ws`). Whatever is resolved
is logged at startup, checked against the backend's `/health`, and shown on the briefing panel
in-headset together with the socket state and which Gemini transport the server is using.

### Stored palettes are per-source
The profile store is keyed by a profile that includes sample rate, gain, texture depth **and**
whether the palette came from the server or the mock. Without that last part, one run with no URL
would fill the store with mock clips that a correctly configured run then reuses.

## Playback: cues vs sustained
`HapticPlayer` treats fire events and the extinguisher spray differently, and the distinction is
load-bearing:

- **Cues** (ignite / spread / extinguish / re-ignite / smoulder warning / stage complete) `Preempt`
  the channel and play in full, at the planner's gain for that profile.
- **The spray** is a loop, fed as ~40 ms slices with the selection gain applied per slice, so a
  profile or gain change takes effect within one slice and stops within one slice of the trigger
  releasing.

## Texture (`textureDepth`)
A plain peak-envelope conversion throws away everything above the envelope, so a gritty crackle and
a clean thump differ only in attack shape. `textureDepth` on the config asset re-imposes the source's
own roughness by modulating the envelope with a carrier read out of the waveform's zero-crossing
rate, octave-folded into the band the actuator can reproduce (up to ~160 Hz at 320 Hz). Changing it
invalidates the profile store automatically.
