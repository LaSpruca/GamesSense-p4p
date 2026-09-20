"""
The headset <-> server WebSocket protocol (one HeadsetSession per connection).

Why a socket instead of the old request-per-event HTTP: the headset now streams what the spray is
doing several times a second and expects selections back within a frame or two, and the server
pushes clips down as they finish synthesising. Neither fits request/response, and holding one
connection open removes the per-request TCP+HTTP setup from every exchange.

Text frames are JSON objects with a "type" field. Binary frames carry a clip:

    [4-byte big-endian header length][header JSON][WAV bytes]
    header = {"type": "clip", "set_id": str, "profile_id": str, "source": str}

Client -> server
    hello         {"client": str, "version": int}
    scene         {"scene": {...snapshot...}, "refresh": bool?}   -> profiles (+ clips stream)
    clips_needed  {"set_id": str, "profiles": [ids]}              -> clips for those ids only
    state         {...spray telemetry...}                          -> select (when it changes)
    event         {"name": str, ...detail}                         (context for the selector)
    ping          {}                                               -> pong

Server -> client
    welcome       {"gemini": bool, "transport": str, "model": bool}
    profiles      {"set_id","scene_key","source","notes","profiles":[...]}
    clip          (binary, above)
    select        {"profile","gain","source","reason","state_key"}
    error         {"message": str}
"""

from __future__ import annotations

import asyncio
import json
import struct
import time
from typing import Any, Awaitable, Callable, Dict, List, Optional

from fastapi import WebSocket, WebSocketDisconnect

import haptic_profiles as hp

# What it costs to synthesise: seconds between selection turns to Gemini, so a trainee holding
# the trigger steadily does not burn a quota on identical readings.
SELECT_MIN_INTERVAL_S = 1.0
# Re-ask even with an unchanged coarse state this often, so Gemini can adjust gain over time.
SELECT_REFRESH_S = 4.0

# (prompt, duration_s, seed, candidates) -> (wav bytes, info dict). Provided by the server module;
# kept as a callable so this file has no torch dependency and can be unit-tested with a stub.
Synth = Callable[[str, float, int, int], Awaitable[tuple]]


def pack_clip(set_id: str, profile_id: str, source: str, wav: bytes) -> bytes:
    header = json.dumps({"type": "clip", "set_id": set_id, "profile_id": profile_id,
                         "source": source}).encode("utf-8")
    return struct.pack(">I", len(header)) + header + wav


class HeadsetSession:
    def __init__(self, ws: WebSocket, synth: Synth, candidates: int = 3):
        self.ws = ws
        self.synth = synth
        self.candidates = candidates
        self.link = hp.GeminiLink()
        self.planner = hp.ProfilePlanner(self.link)
        self._send_lock = asyncio.Lock()
        self._clip_task: Optional[asyncio.Task] = None
        self._select_task: Optional[asyncio.Task] = None
        self._latest_state: Optional[Dict[str, Any]] = None
        self._last_sent: Optional[hp.Selection] = None
        self._last_select_at = 0.0
        self._last_select_key = ""
        self._closed = False

    # --- lifecycle ------------------------------------------------------------------------

    async def run(self) -> None:
        await self.ws.accept()
        await self.send_json({"type": "welcome", "gemini": self.link.available,
                              "transport": self.link.transport, "model": self._model_loaded()})
        self._select_task = asyncio.create_task(self._select_loop())
        try:
            while True:
                message = await self.ws.receive()
                if message.get("type") == "websocket.disconnect":
                    break
                text = message.get("text")
                if text is None:
                    continue   # the headset never sends binary
                try:
                    data = json.loads(text)
                except json.JSONDecodeError:
                    await self.send_json({"type": "error", "message": "bad json"})
                    continue
                await self.handle(data)
        except WebSocketDisconnect:
            pass
        finally:
            await self.close()

    async def close(self) -> None:
        if self._closed:
            return
        self._closed = True
        for task in (self._clip_task, self._select_task):
            if task is not None:
                task.cancel()
        await self.link.close()

    def _model_loaded(self) -> bool:
        return getattr(self.synth, "model_loaded", lambda: False)()

    async def send_json(self, obj: Dict[str, Any]) -> None:
        async with self._send_lock:
            await self.ws.send_text(json.dumps(obj, separators=(",", ":")))

    async def send_bytes(self, data: bytes) -> None:
        async with self._send_lock:
            await self.ws.send_bytes(data)

    # --- dispatch ---------------------------------------------------------------------------

    async def handle(self, data: Dict[str, Any]) -> None:
        kind = data.get("type")
        if kind == "hello":
            print(f"[ws] hello from {data.get('client', '?')} v{data.get('version', '?')}")
        elif kind == "scene":
            await self.on_scene(data.get("scene") or {}, bool(data.get("refresh")),
                                bool(data.get("send_clips", True)))
        elif kind == "clips_needed":
            await self.on_clips_needed(str(data.get("set_id", "")), list(data.get("profiles") or []))
        elif kind == "state":
            self._latest_state = data
        elif kind == "event":
            detail = {k: v for k, v in data.items() if k not in ("type", "name")}
            self.planner.note_event(str(data.get("name", "?")), detail)
        elif kind == "ping":
            await self.send_json({"type": "pong", "t": data.get("t")})
        else:
            await self.send_json({"type": "error", "message": f"unknown type {kind!r}"})

    async def on_scene(self, scene: Dict[str, Any], refresh: bool, send_clips: bool) -> None:
        started = time.time()
        pset = await self.planner.plan(scene, allow_cache=not refresh)
        print(f"[ws] planned {len(pset.profiles)} profiles from {pset.source} in "
              f"{time.time() - started:.1f}s (set {pset.set_id})")
        await self.send_json(pset.to_wire())
        if send_clips:
            self._start_clips(pset, [p.id for p in pset.profiles])

    async def on_clips_needed(self, set_id: str, ids: List[str]) -> None:
        pset = self.planner.profile_set
        if pset is None or pset.set_id != set_id:
            await self.send_json({"type": "error", "message": f"unknown set {set_id}"})
            return
        self._start_clips(pset, [i for i in ids if pset.by_id(i) is not None])

    # --- clips --------------------------------------------------------------------------------

    def _start_clips(self, pset: hp.ProfileSet, ids: List[str]) -> None:
        if self._clip_task is not None and not self._clip_task.done():
            self._clip_task.cancel()
        self._clip_task = asyncio.create_task(self._stream_clips(pset, ids))

    async def _stream_clips(self, pset: hp.ProfileSet, ids: List[str]) -> None:
        """
        Loops first: they are what the trainee feels most of the time, and the cues have decent
        procedural stand-ins. Each clip is sent the moment it exists rather than when the set is
        complete, so the headset upgrades one profile at a time.
        """
        ordered = sorted(ids, key=lambda i: (not pset.by_id(i).loop, ids.index(i)))
        for pid in ordered:
            profile = pset.by_id(pid)
            if profile is None:
                continue
            seed = int.from_bytes(pid.encode("utf-8")[:4].ljust(4, b"\0"), "big") & 0x7FFFFFFF
            try:
                wav, info = await self.synth(profile.prompt, profile.duration_s, seed, self.candidates)
            except asyncio.CancelledError:
                raise
            except Exception as e:
                print(f"[ws] synth failed for {pid}: {e}")
                await self.send_json({"type": "error", "message": f"synth failed for {pid}: {e}"})
                continue
            source = str(info.get("backend", "?")) + (":cached" if info.get("cached") else "")
            await self.send_bytes(pack_clip(pset.set_id, pid, source, wav))
            print(f"[ws] sent clip {pid} ({len(wav)} bytes, {source})")

    # --- selection ------------------------------------------------------------------------------

    async def _select_loop(self) -> None:
        """
        Turns the latest reading into selections at a bounded rate.

        The moment the coarse situation changes, the rule-based choice goes out (the headset has
        the same rules, so this mostly serves as a liveness signal and a gain hint). Gemini is then
        asked, and its answer is sent only if the situation it was asked about still holds - a
        reading that lands while Gemini is thinking is simply the next one considered; nothing
        queues up behind a slow turn.
        """
        inflight: Optional[asyncio.Task] = None
        asked_key = ""
        try:
            while True:
                await asyncio.sleep(0.1)
                state = self._latest_state
                if state is None:
                    continue
                key = hp.coarse_state_key(state)
                now = time.time()

                # Harvest a finished Gemini turn. Its answer only counts if the situation it was
                # asked about is still the current one.
                if inflight is not None and inflight.done():
                    selection = None
                    try:
                        selection = inflight.result()
                    except Exception as e:
                        print(f"[ws] select turn failed: {type(e).__name__}: {e}")
                    inflight = None
                    if selection is not None and selection.source == "gemini" and selection.state_key == key:
                        await self._emit(selection)

                if key == "idle":
                    if self._last_sent is not None and self._last_sent.profile != "":
                        await self._emit(hp.Selection("", 0.0, "rules", "not spraying", key))
                    self._last_select_key = key
                    continue

                if key != self._last_select_key:
                    await self._emit(hp.rule_select(state))
                    self._last_select_key = key

                if inflight is not None:
                    continue   # one Gemini turn at a time; the next reading is considered when it lands
                interval = SELECT_MIN_INTERVAL_S if key != asked_key else SELECT_REFRESH_S
                if now - self._last_select_at < interval:
                    continue
                self._last_select_at = now
                asked_key = key
                inflight = asyncio.create_task(self.planner.select(dict(state)))
        except asyncio.CancelledError:
            pass
        except Exception as e:
            print(f"[ws] select loop stopped: {type(e).__name__}: {e}")
        finally:
            if inflight is not None:
                inflight.cancel()

    async def _emit(self, selection: hp.Selection) -> None:
        last = self._last_sent
        if (last is not None and selection.profile == last.profile and selection.source == last.source
                and abs(selection.gain - last.gain) <= 0.05 and selection.state_key == last.state_key):
            return
        self._last_sent = selection
        await self.send_json(selection.to_wire())
