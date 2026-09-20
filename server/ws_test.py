"""
Exercise the WebSocket path exactly as the headset does: connect, send a scene snapshot, collect
the profile plan and the clips as they stream in, then stream a few seconds of spray readings and
print every selection the server makes.

    python ws_test.py --url ws://localhost:8080/ws
    python ws_test.py --url ws://localhost:8080/ws --no-clips      # plan + selection only (fast)
    python ws_test.py --refresh                                    # bypass the plan cache

Needs the `websockets` package (installed with uvicorn[standard]).
"""

from __future__ import annotations

import argparse
import asyncio
import json
import struct
import sys
import time

import websockets

SCENE = {
    "room": {"surfaces": ["TABLE", "FLOOR", "WALL_FACE", "COUCH", "STORAGE"],
             "size_m": [4.2, 2.6, 3.8], "anchors": 9},
    "extinguisher": {"type": "foam", "hand": "right"},
    "stages": [
        {"index": 1, "name": "First flames", "aim": "forgiving", "spread": False,
         "sweep_sectors": 1, "reignite": False},
        {"index": 5, "name": "Full room", "aim": "strict", "spread": True,
         "sweep_sectors": 3, "reignite": True},
    ],
}

# A short scripted spray: dry -> top -> base -> sweep -> soak -> release.
READINGS = [
    dict(spraying=True, trigger=0.5, hit_rate=0.0, hit_quality=0.0, soaking=False, sweeping=False,
         nearest_fire_m=1.8, fires_burning=1, fires_smouldering=0, aim_error_deg=25.0, stage=2),
    dict(spraying=True, trigger=0.9, hit_rate=80.0, hit_quality=0.25, soaking=False, sweeping=False,
         nearest_fire_m=1.2, fires_burning=1, fires_smouldering=0, aim_error_deg=9.0, stage=2),
    dict(spraying=True, trigger=0.9, hit_rate=140.0, hit_quality=0.9, soaking=False, sweeping=False,
         nearest_fire_m=1.1, fires_burning=1, fires_smouldering=0, aim_error_deg=3.0, stage=2),
    dict(spraying=True, trigger=1.0, hit_rate=150.0, hit_quality=0.85, soaking=False, sweeping=True,
         nearest_fire_m=1.1, fires_burning=1, fires_smouldering=0, aim_error_deg=4.0, stage=2),
    dict(spraying=True, trigger=0.7, hit_rate=5.0, hit_quality=0.0, soaking=True, sweeping=False,
         nearest_fire_m=1.0, fires_burning=0, fires_smouldering=1, aim_error_deg=2.0, stage=2),
    dict(spraying=False, trigger=0.0, hit_rate=0.0, hit_quality=0.0, soaking=False, sweeping=False,
         nearest_fire_m=1.0, fires_burning=0, fires_smouldering=0, aim_error_deg=0.0, stage=2),
]


async def main(args) -> int:
    started = time.time()
    async with websockets.connect(args.url, max_size=64 * 1024 * 1024) as ws:
        print(f"connected in {time.time() - started:.2f}s")
        await ws.send(json.dumps({"type": "hello", "client": "ws_test", "version": 1}))
        await ws.send(json.dumps({"type": "scene", "scene": SCENE, "refresh": args.refresh,
                                  "send_clips": not args.no_clips}))

        profiles = None
        clips = {}
        deadline = time.time() + args.timeout
        while time.time() < deadline:
            try:
                msg = await asyncio.wait_for(ws.recv(), timeout=max(0.1, deadline - time.time()))
            except asyncio.TimeoutError:
                break
            if isinstance(msg, bytes):
                (n,) = struct.unpack(">I", msg[:4])
                header = json.loads(msg[4:4 + n])
                wav = msg[4 + n:]
                clips[header["profile_id"]] = wav
                print(f"  clip {header['profile_id']:18} {len(wav):7d} bytes  {header['source']}  "
                      f"(+{time.time() - started:.1f}s)")
                if profiles is not None and len(clips) >= len(profiles):
                    break
                continue
            data = json.loads(msg)
            kind = data.get("type")
            if kind == "welcome":
                print("welcome:", data)
            elif kind == "profiles":
                profiles = data["profiles"]
                print(f"profiles from {data['source']} (set {data['set_id']}) "
                      f"in {time.time() - started:.1f}s: {data.get('notes', '')}")
                for p in profiles:
                    print(f"  {p['id']:18} loop={p['loop']!s:5} {p['duration_s']:.1f}s gain={p['gain']:.2f}  {p['prompt']}")
                if args.no_clips:
                    break
            elif kind == "error":
                print("error:", data["message"])
            elif kind == "select":
                print("select:", data)

        if profiles is None:
            print("no profile plan received")
            return 1
        if args.save and clips:
            for pid, wav in clips.items():
                with open(f"{args.save}-{pid}.wav", "wb") as f:
                    f.write(wav)
            print(f"saved {len(clips)} clips with prefix {args.save}")

        print("\nstreaming spray readings...")
        selections = []

        async def reader():
            try:
                while True:
                    msg = await ws.recv()
                    if isinstance(msg, bytes):
                        continue
                    data = json.loads(msg)
                    if data.get("type") == "select":
                        selections.append(data)
                        print(f"  select -> {data['profile'] or '(stop)':12} gain={data['gain']:.2f} "
                              f"[{data['source']}] {data.get('reason', '')}  (+{time.time() - started:.1f}s)")
            except Exception:
                pass

        reader_task = asyncio.create_task(reader())
        for reading in READINGS:
            print(f"  reading: {json.dumps(reading, separators=(',', ':'))}")
            for _ in range(int(args.hold * 8)):
                await ws.send(json.dumps({"type": "state", **reading}))
                await asyncio.sleep(1 / 8)
        await asyncio.sleep(1.5)
        reader_task.cancel()

    print(f"\n{len(selections)} selection(s); gemini-sourced: "
          f"{sum(1 for s in selections if s['source'] == 'gemini')}")
    return 0


if __name__ == "__main__":
    p = argparse.ArgumentParser()
    p.add_argument("--url", default="ws://localhost:8080/ws")
    p.add_argument("--timeout", type=float, default=900, help="seconds to wait for the clip stream")
    p.add_argument("--hold", type=float, default=3.0, help="seconds to hold each scripted reading")
    p.add_argument("--no-clips", action="store_true", help="skip clip synthesis")
    p.add_argument("--refresh", action="store_true", help="ignore the server's cached plan")
    p.add_argument("--save", default=None, help="filename prefix for received clips")
    sys.exit(asyncio.run(main(p.parse_args())))
