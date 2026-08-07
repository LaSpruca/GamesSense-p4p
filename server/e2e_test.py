"""
End-to-end check for the haptics server: sends a world event exactly as the Unity headset does,
then reports what came back — including the controller amplitude envelope the Quest would play.

The point is to see the haptic, not just prove the HTTP call worked. The envelope maths is a
line-for-line port of Assets/HapticAI/Scripts/Playback/WaveformToAmplitudes.cs, so what this
prints is what the controller gets.

    python e2e_test.py --url http://localhost:8080 --event Ignite --intensity 0.9
    python e2e_test.py --all          # one clip per event type

Dependency-free (stdlib only) so it runs against a remote server from any box.
"""

from __future__ import annotations

import argparse
import io
import json
import sys
import time
import urllib.request
import wave

# Defaults mirror HapticAIConfig.cs so the envelope matches the headset's.
TARGET_RATE_HZ = 320
GAIN = 1.4
MAX_SECONDS = 4.0

EVENT_PRESETS = {
    "Ignite":     {"numbers": {"intensity": 0.9, "distance": 0.4}, "tags": {"surface": "TABLE"}},
    "Spread":     {"numbers": {"intensity": 0.6, "distance": 1.2}, "tags": {"surface": "CARPET"}},
    "Extinguish": {"numbers": {"intensity": 0.2, "distance": 0.6}, "tags": {"surface": "TABLE"}},
    "SprayTick":  {"numbers": {"triggerDepth": 0.85, "distance": 0.3}, "tags": {}},
}


def post_haptics(url: str, payload: dict, timeout: int = 900):
    body = json.dumps(payload).encode("utf-8")
    req = urllib.request.Request(
        url.rstrip("/") + "/haptics", data=body,
        headers={"Content-Type": "application/json", "Accept": "audio/wav"}, method="POST",
    )
    started = time.time()
    with urllib.request.urlopen(req, timeout=timeout) as resp:
        # Lower-cased: HTTP header names are case-insensitive and uvicorn sends them lowercase,
        # so a plain dict() of them would miss "X-Haptic-Prompt".
        headers = {k.lower(): v for k, v in resp.headers.items()}
        return resp.read(), headers, time.time() - started


def decode_wav(raw: bytes):
    """PCM16 mono wav -> (samples as floats in -1..1, sample_rate)."""
    with wave.open(io.BytesIO(raw), "rb") as w:
        n, rate, width, channels = w.getnframes(), w.getframerate(), w.getsampwidth(), w.getnchannels()
        frames = w.readframes(n)
    if width != 2:
        raise ValueError(f"expected PCM16, got {width * 8}-bit")
    import array
    pcm = array.array("h")
    pcm.frombytes(frames)
    samples = [s / 32768.0 for s in pcm]
    if channels > 1:  # average down, same as the Unity side
        samples = [sum(samples[i:i + channels]) / channels for i in range(0, len(samples), channels)]
    return samples, rate


def to_amplitudes(samples, sample_rate, target_rate=TARGET_RATE_HZ, gain=GAIN, max_seconds=MAX_SECONDS):
    """Port of WaveformToAmplitudes.Convert — rectify, windowed peak downsample, gain, 0..255."""
    if not samples or target_rate <= 0:
        return []
    duration = min(len(samples) / sample_rate, max_seconds)
    out_count = max(1, int(duration * target_rate))
    in_per_out = sample_rate / target_rate

    amps = []
    for o in range(out_count):
        start = int(o * in_per_out)
        end = int((o + 1) * in_per_out)
        if end <= start:
            end = start + 1
        end = min(end, len(samples))
        peak = max((abs(s) for s in samples[start:end]), default=0.0)
        amps.append(int(min(peak * gain, 1.0) * 255 + 0.5))
    return amps


def render(amps, width=72, height=12):
    """ASCII envelope so the haptic is legible in a terminal."""
    if not amps:
        return "(empty)"
    cols = []
    for c in range(width):
        lo = int(c * len(amps) / width)
        hi = max(lo + 1, int((c + 1) * len(amps) / width))
        cols.append(max(amps[lo:hi], default=0))

    lines = []
    for row in range(height, 0, -1):
        threshold = 255 * row / height
        lines.append("".join("#" if v >= threshold else " " for v in cols))
    lines.append("-" * width)
    return "\n".join(lines)


def summarise(amps):
    if not amps:
        return "no samples"
    peak, mean = max(amps), sum(amps) / len(amps)
    active = sum(1 for a in amps if a >= 13)  # ~0.05 of full scale, HapticGen's perceptibility floor
    return (f"peak={peak}/255  mean={mean:.1f}  perceptible={100 * active / len(amps):.0f}% of frames  "
            f"frames={len(amps)}")


def run_one(url, event, args, scene=None):
    if scene is not None:
        payload = {
            "event": scene["event"],
            "hand": "Right",
            "numbers": dict(scene.get("numbers", {})),
            "tags": dict(scene.get("tags", {})),
            "duration_s": args.duration,
            "seed": args.seed,
            "candidates": args.candidates,
        }
        if scene.get("custom_name"):
            payload["custom_name"] = scene["custom_name"]
        label = scene["name"]
    else:
        preset = EVENT_PRESETS.get(event, {"numbers": {}, "tags": {}})
        payload = {
            "event": event,
            "hand": "Right",
            "numbers": dict(preset["numbers"]),
            "tags": dict(preset["tags"]),
            "duration_s": args.duration,
            "seed": args.seed,
            "candidates": args.candidates,
        }
        if args.intensity is not None:
            payload["numbers"]["intensity"] = args.intensity
        if args.distance is not None:
            payload["numbers"]["distance"] = args.distance
        label = event

    print(f"\n=== {label} ===")
    if scene is not None:
        print(f"probing: {scene['note']}")
    print(f"request: {json.dumps(payload)}")
    try:
        raw, headers, elapsed = post_haptics(url, payload, args.timeout)
    except Exception as e:
        print(f"FAILED: {e}")
        return False

    samples, rate = decode_wav(raw)
    amps = to_amplitudes(samples, rate)

    print(f"took {elapsed:.1f}s | {len(raw)} bytes | {len(samples)} samples @ {rate} Hz "
          f"({len(samples) / rate:.2f}s)")
    print(f"prompt [{headers.get('x-haptic-source', '?')}]: {headers.get('x-haptic-prompt', '(none)')}")
    print(f"device={headers.get('x-haptic-device', '?')} backend={headers.get('x-haptic-backend', '?')} "
          f"peak={headers.get('x-haptic-peak', '?')} rms={headers.get('x-haptic-rms', '?')} "
          f"silent={headers.get('x-haptic-silent', '?')} degenerate={headers.get('x-haptic-degenerate', '?')}")
    print(f"controller envelope @ {TARGET_RATE_HZ}Hz gain {GAIN}: {summarise(amps)}")
    print(render(amps))

    if args.save:
        path = f"{args.save}-{label}.wav"
        with open(path, "wb") as f:
            f.write(raw)
        print(f"saved {path}")

    if headers.get("x-haptic-silent") == "1":
        print("WARNING: server flagged this clip as below the perceptibility threshold.")
    if headers.get("x-haptic-degenerate") == "1":
        print("WARNING: constant-drone clip (no attack/decay). Retry with a higher --candidates.")
    return True


def main():
    p = argparse.ArgumentParser()
    p.add_argument("--url", default="http://localhost:8080")
    p.add_argument("--event", default="Ignite", choices=list(EVENT_PRESETS) + ["Custom"])
    p.add_argument("--all", action="store_true", help="run every event preset")
    p.add_argument("--scenes", action="store_true",
                   help="run the full scene library from scenes.py")
    p.add_argument("--scene", default=None, help="run one named scene from scenes.py")
    p.add_argument("--duration", type=float, default=1.0)
    p.add_argument("--seed", type=int, default=0)
    p.add_argument("--candidates", type=int, default=1)
    p.add_argument("--intensity", type=float, default=None)
    p.add_argument("--distance", type=float, default=None)
    p.add_argument("--timeout", type=int, default=900)
    p.add_argument("--save", default=None, help="filename prefix for the returned wavs")
    args = p.parse_args()

    try:
        with urllib.request.urlopen(args.url.rstrip("/") + "/health", timeout=30) as r:
            print("health:", json.dumps(json.loads(r.read()), indent=2))
    except Exception as e:
        print(f"cannot reach {args.url}: {e}")
        return 1

    if args.scenes or args.scene:
        import scenes as scene_lib
        chosen = scene_lib.SCENES if args.scenes else [scene_lib.by_name(args.scene)]
        results = [run_one(args.url, None, args, scene=s) for s in chosen]
    else:
        events = list(EVENT_PRESETS) if args.all else [args.event]
        results = [run_one(args.url, e, args) for e in events]
    return 0 if all(results) else 1


if __name__ == "__main__":
    sys.exit(main())
