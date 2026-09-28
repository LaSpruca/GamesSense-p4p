"""
Profile-based haptic planning and selection.

The old per-event flow asked Gemini to write a fresh HaptiGen prompt for every world event, which
was three LLM calls and a ~30 s synthesis per event - far too slow for anything that has to track
what the hand is doing right now. This module inverts it:

  1. plan(scene)   Once per session the headset sends a snapshot of the scene: which surfaces the
                   room has, what the stages ask of the trainee, what the extinguisher is. Gemini
                   reads it and writes ONE prompt per haptic ROLE (see ROLES) - a whole palette of
                   profiles at once. Each profile is synthesised with HaptiGen and streamed to the
                   headset, which then holds every clip it could need.

  2. select(state) While the trigger is held the headset streams what the foam is doing (hitting
                   the base, hitting the tips, missing, soaking a smouldering patch...) and Gemini
                   picks which of the sustained profiles should play, and how loud. The headset
                   applies the same choice through a rule-based selector the instant the state
                   changes, so Gemini's answer refines rather than gates the feedback.

Gemini is reached over the Live API: one persistent WebSocket per headset session, so a selection
turn is a sub-second round-trip on an open connection with the scene already in context, rather
than a fresh HTTPS request carrying it all again. The Live models only speak audio, so structured
answers come back as TOOL CALLS (plan_profiles / select_profile) - which is also what makes them
reliably parseable. Everything degrades: Live unavailable -> generate_content over HTTPS with JSON
text; no key -> the offline templates below and the rule selector.
"""

from __future__ import annotations

import asyncio
import collections
import hashlib
import json
import os
import re
import time
from dataclasses import dataclass, asdict
from typing import Any, Dict, List, Optional

try:
    from dotenv import load_dotenv
    load_dotenv(os.path.join(os.path.dirname(__file__), ".env"))
except ImportError:
    pass

try:
    from google import genai
    from google.genai import types as genai_types
except Exception:  # pragma: no cover
    genai = None
    genai_types = None


# Live-capable model for the persistent session; a plain generate_content model as the HTTPS
# fallback. Both pinned: the wording drives what gets synthesised. (gemini-3.8-live answered
# tool calls in ~1 s in testing; the -extended-thinking variant needs a thinking level and is
# no faster for this.)
LIVE_MODEL = os.environ.get("GEMINI_LIVE_MODEL", "gemini-3.8-live")
HTTP_MODEL = os.environ.get("GEMINI_MODEL", "gemini-3.6-flash")
THINKING_LEVEL = os.environ.get("GEMINI_THINKING_LEVEL", "low")
# How long a selection turn may take before we stop waiting. The headset already has a rule-based
# answer playing; a late Gemini answer is worth less than a fresh one. HTTPS turns think for a few
# seconds before answering, so they get longer.
SELECT_TIMEOUT_LIVE_S = float(os.environ.get("GEMINI_SELECT_TIMEOUT", "4.0"))
SELECT_TIMEOUT_HTTP_S = float(os.environ.get("GEMINI_SELECT_TIMEOUT_HTTP", "8"))
PLAN_TIMEOUT_S = float(os.environ.get("GEMINI_PLAN_TIMEOUT", "60"))

PLAN_CACHE_DIR = os.path.join(os.path.dirname(__file__), ".plan_cache")


# --- Roles ------------------------------------------------------------------------------------
# The fixed vocabulary shared with the headset (HapticRoles.cs). "loop" profiles are the
# sustained spray states the selector chooses between; the rest are one-shot cues the headset
# fires itself when the matching world event happens.
#
# Every duration is 1.0 s on purpose: with --device npu the EnCodec decoder is compiled per clip
# length (~20 min the first time, see npu_decode.py) and 1.0 s is the shape already cached. The
# headset loops the spray profiles, so a 1 s loop is as good as a longer one; a cue that wants to
# be shorter simply has a shorter designed decay.

@dataclass(frozen=True)
class Role:
    name: str
    loop: bool
    duration_s: float
    what: str          # what the trainee is doing / what happened, for the LLM
    should_feel: str   # the design intent, so profiles stay distinct from each other


ROLES: List[Role] = [
    Role("spray_dry", True, 1.0,
         "the trigger is held but the foam is landing on nothing that is burning",
         "an even, uneventful pressurised hiss - steady and slightly hollow, nothing to reward"),
    Role("spray_top", True, 1.0,
         "foam is hitting the TOP of the flames instead of the base - poor aim",
         "thin, fluttery and unsatisfying: the foam is flashing off in the heat, little resistance"),
    Role("spray_base", True, 1.0,
         "foam is landing squarely on the BASE of the fire - correct technique",
         "dense and rewarding: a firm, chugging resistance with a crisp fizz as flames are smothered"),
    Role("spray_sweep", True, 1.0,
         "foam is being swept side to side across the base of the fire - ideal technique",
         "rolling and rhythmic: the same dense resistance as the base hit, with a slow lateral surge"),
    Role("spray_soak", True, 1.0,
         "foam is soaking an already knocked-down, smouldering patch to stop it re-igniting",
         "soft, bubbling and settling - a wet crackle that gradually calms to a low hum"),
    Role("ignite", False, 1.0,
         "a fire suddenly ignites nearby",
         "a sharp whump: fast attack, short roar, quick decay"),
    Role("spread", False, 1.0,
         "flames jump to a neighbouring surface",
         "a crackling flare-up, lighter and faster than ignition"),
    Role("extinguish", False, 1.0,
         "a burning patch is snuffed out into a hiss of steam",
         "a relieving hiss that fades - no impact, all release"),
    Role("reignite", False, 1.0,
         "a smouldering patch the trainee walked away from flares back up",
         "an unwelcome double-pulse: a warning thud then a rising crackle"),
    Role("smoulder_warning", False, 1.0,
         "a knocked-down patch is smouldering, unattended, and could flare back",
         "a subtle, insistent ember pulse - a gentle nudge, not an alarm"),
    Role("stage_complete", False, 1.0,
         "every fire in this stage is out; the room falls quiet",
         "a firm, settling all-clear: two soft confirming taps then a long calm fade"),
]

ROLE_BY_NAME = {r.name: r for r in ROLES}
LOOP_ROLES = [r.name for r in ROLES if r.loop]


# --- Data ---------------------------------------------------------------------------------------

@dataclass
class Profile:
    id: str
    role: str
    prompt: str
    duration_s: float
    loop: bool
    gain: float = 1.0

    def to_wire(self) -> Dict[str, Any]:
        return asdict(self)


@dataclass
class ProfileSet:
    set_id: str
    scene_key: str
    source: str                 # "gemini-live" | "gemini-http" | "offline" | "cache:<source>"
    profiles: List[Profile]
    notes: str = ""

    def to_wire(self) -> Dict[str, Any]:
        return {
            "type": "profiles",
            "set_id": self.set_id,
            "scene_key": self.scene_key,
            "source": self.source,
            "notes": self.notes,
            "profiles": [p.to_wire() for p in self.profiles],
        }

    def by_id(self, profile_id: str) -> Optional[Profile]:
        for p in self.profiles:
            if p.id == profile_id:
                return p
        return None


@dataclass
class Selection:
    profile: str
    gain: float
    source: str                 # "gemini" | "rules"
    reason: str = ""
    state_key: str = ""

    @property
    def pwm(self) -> int:
        """Gain (0-1) rescaled to the 0-100% duty cycle the ESP32 haptic driver expects.

        Gain and PWM are the same "how strong" decision; a physical motor just wants it as a
        percentage instead of a 0-1 float. Silent (profile == "") always means 0%, regardless
        of a stale gain value.
        """
        if not self.profile:
            return 0
        return max(0, min(100, round(self.gain * 100)))

    def to_wire(self) -> Dict[str, Any]:
        return {"type": "select", "profile": self.profile, "gain": round(self.gain, 3),
                "pwm": self.pwm, "source": self.source, "reason": self.reason,
                "state_key": self.state_key}


# --- Offline templates ----------------------------------------------------------------------------

def offline_profiles(scene: Dict[str, Any]) -> List[Profile]:
    """Deterministic palette used when Gemini is unavailable. Mirrors HapticRoles.cs offline set."""
    surfaces = ", ".join(scene.get("room", {}).get("surfaces", [])[:4]) or "unknown surfaces"
    out = []
    for role in ROLES:
        prompt = (f"A vibrotactile haptic effect for a VR fire-extinguisher drill in a room with "
                  f"{surfaces.lower()}: {role.what}. It should feel {role.should_feel}.")
        out.append(Profile(id=role.name, role=role.name, prompt=prompt,
                           duration_s=role.duration_s, loop=role.loop, gain=1.0))
    return out


# --- Scene / state keys ---------------------------------------------------------------------------

def scene_key(scene: Dict[str, Any]) -> str:
    """Stable identity for a scene snapshot, so an unchanged room reuses its plan and clips."""
    room = scene.get("room", {})
    stages = scene.get("stages") or scene.get("stage") or {}
    material = json.dumps({"surfaces": sorted(room.get("surfaces", [])),
                           "size": room.get("size_m"),
                           "stages": stages,
                           "extinguisher": scene.get("extinguisher")}, sort_keys=True)
    return hashlib.sha1(material.encode("utf-8")).hexdigest()[:16]


def make_set_id(scene_id: str, profiles: List[Profile]) -> str:
    material = scene_id + "|" + "|".join(f"{p.id}:{p.prompt}:{p.duration_s}" for p in profiles)
    return hashlib.sha1(material.encode("utf-8")).hexdigest()[:16]


def coarse_state_key(state: Dict[str, Any]) -> str:
    """
    Buckets the spray telemetry into the handful of situations a selection can differ on. The
    headset computes the same key (HapticProfileSelector.cs) so a Gemini choice can be tied to the
    situation it was made for and dropped the moment that situation ends.
    """
    if not state.get("spraying"):
        return "idle"
    trig = float(state.get("trigger", 0.0))
    tb = "t0" if trig < 0.4 else "t1" if trig < 0.8 else "t2"
    hit_rate = float(state.get("hit_rate", 0.0))
    if hit_rate < 15.0:
        contact = "soak" if state.get("soaking") else "dry"
    else:
        q = float(state.get("hit_quality", 0.0))
        contact = "base" if q >= 0.6 else "top"
        if contact == "base" and state.get("sweeping"):
            contact = "sweep"
    return f"{contact}|{tb}"


def rule_select(state: Dict[str, Any]) -> Selection:
    """Port of HapticProfileSelector.cs. Used server-side only as Gemini's stand-in."""
    key = coarse_state_key(state)
    if key == "idle":
        return Selection("", 0.0, "rules", "not spraying", key)
    contact, tb = key.split("|")
    trig = float(state.get("trigger", 0.0))
    gain = 0.45 + 0.55 * max(0.0, min(1.0, trig))
    profile = {"dry": "spray_dry", "top": "spray_top", "base": "spray_base",
               "sweep": "spray_sweep", "soak": "spray_soak"}[contact]
    return Selection(profile, gain, "rules", f"rule: {contact}", key)


# --- Prompts ----------------------------------------------------------------------------------------

READING_FIELDS = (
    "Reading fields: spraying (trigger held); trigger 0-1 (how hard); hit_rate = foam particles per "
    "second landing on fire (0 = missing everything, 100+ = squarely on it); hit_quality 0-1 where "
    "1.0 means the foam lands at the BASE of the flames and 0 means at the tips - HIGH is GOOD; "
    "sweeping = nozzle is moving side to side across the base; soaking = foam is on an already "
    "knocked-down smouldering patch; coverage 0-1 = how much of the patch's base has been swept; "
    "nearest_fire_m; aim_error_deg; fires_burning; fires_smouldering; stage (1 easy .. 5 hard)."
)

DIRECTOR_SYSTEM = (
    "You are the haptics designer and live haptic director for a VR fire-extinguisher training "
    "simulation on a Meta Quest. You never speak or write prose: you ONLY answer by calling the "
    "provided functions.\n\n"
    "Design (plan_profiles): you write short text prompts for HaptiGen, a text-to-vibrotactile model "
    "that renders ~1 s waveforms played through a controller's actuator. Good prompts name texture, "
    "sharpness, rhythm and how the effect evolves, in plain physical language, and are consistent "
    "with the scene (foam on a wooden table feels different from foam on a carpet). Every role's "
    "prompt must be clearly distinguishable from the others by feel.\n\n"
    "Direction (select_profile): you receive compact readings of what the trainee's foam spray is "
    "doing and choose which LOOPING profile plays right now, with a gain 0.2-1.0, so the feel "
    "reinforces correct technique: spray_base when foam lands on the base (hit_quality high), "
    "spray_sweep when it is also sweeping, spray_top when foam lands on the tips (hit_quality low), "
    "spray_dry when it is missing (hit_rate near 0), spray_soak when soaking a smouldering patch. "
    "Gain should follow trigger depth and, for good technique, sit a little higher than for poor.\n"
    + READING_FIELDS
)

PLANNER_SYSTEM = DIRECTOR_SYSTEM   # HTTPS fallback: same words, JSON text instead of a tool call
SELECTOR_SYSTEM = DIRECTOR_SYSTEM


def _roles_wire() -> List[Dict[str, Any]]:
    return [{"id": r.name, "loop": r.loop, "duration_s": r.duration_s,
             "situation": r.what, "should_feel": r.should_feel} for r in ROLES]


def _plan_request(scene: Dict[str, Any], as_tool: bool) -> str:
    text = ("Scene snapshot:\n" + json.dumps(scene, indent=1) +
            "\n\nRoles:\n" + json.dumps(_roles_wire(), indent=1) + "\n\n")
    if as_tool:
        text += ("Call plan_profiles with one entry per role id (prompt: one line, <= 40 words; "
                 "gain 0.5-1.0) and a one-sentence note on the palette.")
    else:
        text += ("Return JSON: {\"notes\": \"<one sentence on the palette>\", \"profiles\": "
                 "[{\"id\": \"<role id>\", \"prompt\": \"<HaptiGen prompt, one line, <= 40 words>\", "
                 "\"gain\": <0.5-1.0>}]} with exactly one entry per role id.")
    return text


def _select_request(state: Dict[str, Any], events: List[str], as_tool: bool) -> str:
    compact = {k: (round(v, 2) if isinstance(v, float) else v) for k, v in state.items()
               if k != "type"}
    text = "Spray reading: " + json.dumps(compact, separators=(",", ":"))
    if events:
        text += "\nRecent events: " + "; ".join(events[-4:])
    text += "\nLooping profiles: " + ", ".join(LOOP_ROLES)
    text += "\nCall select_profile now." if as_tool else "\nAnswer with JSON only: {\"profile\",\"gain\",\"reason\"}."
    return text


_JSON_FENCE = re.compile(r"^```(?:json)?\s*|\s*```$", re.MULTILINE)


def parse_json(text: str) -> Optional[Any]:
    """Tolerant: strips code fences and trailing prose, then finds the outermost object."""
    if not text:
        return None
    cleaned = _JSON_FENCE.sub("", text.strip())
    try:
        return json.loads(cleaned)
    except json.JSONDecodeError:
        pass
    start, end = cleaned.find("{"), cleaned.rfind("}")
    if start >= 0 and end > start:
        try:
            return json.loads(cleaned[start:end + 1])
        except json.JSONDecodeError:
            return None
    return None


def profiles_from_plan(plan: Any, fallback: List[Profile]) -> Optional[List[Profile]]:
    """Validate a plan against the role vocabulary; roles the model skipped keep their template."""
    if not isinstance(plan, dict) or not isinstance(plan.get("profiles"), list):
        return None
    by_role = {p.role: p for p in fallback}
    got = 0
    for item in plan["profiles"]:
        if not isinstance(item, dict):
            continue
        rid = str(item.get("id", "")).strip()
        prompt = str(item.get("prompt", "")).strip()
        if rid not in ROLE_BY_NAME or not prompt:
            continue
        role = ROLE_BY_NAME[rid]
        try:
            gain = float(item.get("gain", 1.0))
        except (TypeError, ValueError):
            gain = 1.0
        by_role[rid] = Profile(id=rid, role=rid, prompt=prompt[:600], duration_s=role.duration_s,
                               loop=role.loop, gain=max(0.3, min(1.0, gain)))
        got += 1
    if got == 0:
        return None
    return [by_role[r.name] for r in ROLES]


# --- Gemini access ---------------------------------------------------------------------------------

def gemini_available() -> bool:
    return genai is not None and bool(os.environ.get("GEMINI_API_KEY"))


def _live_tools():
    plan_fn = genai_types.FunctionDeclaration(
        name="plan_profiles",
        description="Submit the haptic profile palette for this scene: one HaptiGen prompt per role.",
        parameters=genai_types.Schema(
            type="OBJECT",
            properties={
                "notes": genai_types.Schema(type="STRING", description="One sentence on the palette."),
                "profiles": genai_types.Schema(
                    type="ARRAY",
                    items=genai_types.Schema(
                        type="OBJECT",
                        properties={
                            "id": genai_types.Schema(type="STRING", enum=[r.name for r in ROLES]),
                            "prompt": genai_types.Schema(type="STRING",
                                                         description="HaptiGen prompt, one line, <= 40 words"),
                            "gain": genai_types.Schema(type="NUMBER", description="0.5 to 1.0"),
                        },
                        required=["id", "prompt"])),
            },
            required=["profiles"]))
    select_fn = genai_types.FunctionDeclaration(
        name="select_profile",
        description="Choose which looping haptic profile should play on the controller right now.",
        parameters=genai_types.Schema(
            type="OBJECT",
            properties={
                "profile": genai_types.Schema(type="STRING", enum=LOOP_ROLES),
                "gain": genai_types.Schema(type="NUMBER", description="0.2 to 1.0"),
                "reason": genai_types.Schema(type="STRING", description="A few words."),
            },
            required=["profile", "gain"]))
    return [genai_types.Tool(function_declarations=[plan_fn, select_fn])]


class GeminiLink:
    """
    One Gemini conversation per headset session. Prefers a Live API session (persistent WebSocket,
    context kept server-side so a selection turn carries only the new reading, answers as tool
    calls); falls back to generate_content over HTTPS with the context re-sent each turn and JSON
    text answers.
    """

    def __init__(self):
        self._client = genai.Client(api_key=os.environ["GEMINI_API_KEY"]) if gemini_available() else None
        self._live_cm = None
        self._session = None
        self._live_failed = False
        self._lock = asyncio.Lock()
        self._context: List[str] = []     # HTTPS fallback: the scene + plan, re-sent each turn
        self.transport = "none"
        self.turns = 0
        # Live turns in flight, oldest first. A dedicated reader pairs each model answer with the
        # head of this queue, so a caller that stops waiting (timeout) leaves its future here and
        # the stream never falls one answer out of step - which is what closing the session on
        # every timeout, or reading inline, both did.
        self._pending: "collections.deque[asyncio.Future]" = collections.deque()
        self._reader: Optional[asyncio.Task] = None
        self._skip_turn_complete = 0

    @property
    def available(self) -> bool:
        return self._client is not None

    @property
    def is_live(self) -> bool:
        return self._session is not None

    @property
    def select_timeout(self) -> float:
        return SELECT_TIMEOUT_LIVE_S if self.is_live else SELECT_TIMEOUT_HTTP_S

    async def open(self) -> None:
        """Idempotent; safe to call before every turn."""
        if not self.available or self._live_failed or self._session is not None:
            return
        async with self._lock:
            if self._session is not None or self._live_failed:
                return
            await self._connect_live()

    async def _connect_live(self) -> None:
        try:
            config = genai_types.LiveConnectConfig(
                response_modalities=["AUDIO"],     # the only modality Live models offer; ignored
                temperature=0.5,
                system_instruction=DIRECTOR_SYSTEM,
                tools=_live_tools(),
                context_window_compression=genai_types.ContextWindowCompressionConfig(
                    sliding_window=genai_types.SlidingWindow()),
            )
            self._live_cm = self._client.aio.live.connect(model=LIVE_MODEL, config=config)
            self._session = await asyncio.wait_for(self._live_cm.__aenter__(), timeout=15)
            self._pending.clear()
            self._skip_turn_complete = 0
            self._reader = asyncio.create_task(self._read_live(self._session))
            self.transport = f"live:{LIVE_MODEL}"
            print(f"[gemini] live session open ({LIVE_MODEL})")
        except Exception as e:
            print(f"[gemini] live connect failed ({_describe(e)}); using generate_content over HTTPS")
            self._live_failed = True
            self._session = None
            self._live_cm = None
            self.transport = f"http:{HTTP_MODEL}"

    async def close(self) -> None:
        reader, self._reader = self._reader, None
        if reader is not None:
            reader.cancel()
        if self._live_cm is not None:
            try:
                await self._live_cm.__aexit__(None, None, None)
            except Exception:
                pass
        self._live_cm = None
        self._session = None
        self._fail_pending()

    def _fail_pending(self) -> None:
        while self._pending:
            fut = self._pending.popleft()
            if not fut.done():
                fut.set_result(None)

    async def _read_live(self, session) -> None:
        """
        The one reader for a Live session. Every model answer is matched to the oldest turn still
        waiting: a tool call resolves it with the call's arguments; a turn that completes without
        one resolves it with None. After a tool call the model emits one more (empty) turn in
        reply to the tool response, which is skipped so it cannot be mistaken for the next answer.
        """
        try:
            async for msg in session.receive():
                if msg.tool_call is not None and msg.tool_call.function_calls:
                    args = None
                    responses = []
                    for fc in msg.tool_call.function_calls:
                        if args is None:
                            args = (fc.name, dict(fc.args or {}))
                        responses.append(genai_types.FunctionResponse(name=fc.name, id=fc.id,
                                                                      response={"ok": True}))
                    await session.send_tool_response(function_responses=responses)
                    self._skip_turn_complete += 1
                    if self._pending:
                        fut = self._pending.popleft()
                        if not fut.done():
                            fut.set_result(args)
                    continue
                sc = msg.server_content
                if sc is not None and sc.turn_complete:
                    if self._skip_turn_complete > 0:
                        self._skip_turn_complete -= 1
                    elif self._pending:
                        fut = self._pending.popleft()
                        if not fut.done():
                            fut.set_result(None)
                if msg.go_away is not None:
                    print("[gemini] live session go_away; will reconnect on the next turn")
                    break
        except asyncio.CancelledError:
            raise
        except Exception as e:
            print(f"[gemini] live reader stopped ({_describe(e)}); will reconnect on the next turn")
        finally:
            if self._session is session:
                self._session = None
                self._live_cm = None
            self._fail_pending()

    async def ask(self, system: str, text: str, timeout: float, expect_tool: str,
                  remember: bool = False) -> Optional[Dict[str, Any]]:
        """
        One turn. Returns the structured answer (tool-call args on Live, parsed JSON over HTTPS) or
        None on any failure. Never raises: the headset always has the rule selector to fall back on.
        """
        if not self.available:
            return None
        await self.open()
        self.turns += 1
        if self._session is not None:
            try:
                return await self._ask_live(text, expect_tool, timeout)
            except asyncio.TimeoutError:
                # The answer will still arrive and be paired with this turn by the reader; only
                # the caller has moved on. Do not chase it over HTTPS either.
                return None
            except Exception as e:
                print(f"[gemini] live turn failed ({_describe(e)}); reconnecting on the next turn")
                await self.close()
        async with self._lock:
            try:
                self.transport = f"http:{HTTP_MODEL}"
                out = await asyncio.wait_for(self._ask_http(system, text, remember),
                                             timeout=max(timeout, SELECT_TIMEOUT_HTTP_S))
                data = parse_json(out)
                return data if isinstance(data, dict) else None
            except Exception as e:
                print(f"[gemini] http turn failed ({_describe(e)})")
                return None

    async def _ask_live(self, text: str, expect_tool: str, timeout: float) -> Optional[Dict[str, Any]]:
        session = self._session
        fut: asyncio.Future = asyncio.get_running_loop().create_future()
        self._pending.append(fut)
        content = genai_types.Content(role="user", parts=[genai_types.Part(text=text)])
        await session.send_client_content(turns=content, turn_complete=True)
        answer = await asyncio.wait_for(asyncio.shield(fut), timeout=timeout)
        if answer is None:
            return None
        name, args = answer
        return args if name == expect_tool else None

    async def _ask_http(self, system: str, text: str, remember: bool) -> str:
        contents = "\n\n".join(self._context + [text]) if self._context else text
        resp = await self._client.aio.models.generate_content(
            model=HTTP_MODEL,
            contents=contents,
            config=genai_types.GenerateContentConfig(
                system_instruction=system,
                temperature=0.5,
                max_output_tokens=4096,
                thinking_config=genai_types.ThinkingConfig(thinking_level=THINKING_LEVEL),
            ),
        )
        out = (getattr(resp, "text", None) or "").strip()
        if remember:
            self._context.append(text)
            self._context.append("Your previous answer: " + out[:4000])
        return out


def _describe(e: BaseException) -> str:
    return f"{type(e).__name__}: {e}" if str(e) else type(e).__name__


# --- Planner ---------------------------------------------------------------------------------------

def _load_cached_plan(key: str) -> Optional[ProfileSet]:
    try:
        with open(os.path.join(PLAN_CACHE_DIR, key + ".json"), "r", encoding="utf-8") as f:
            data = json.load(f)
        profiles = [Profile(**p) for p in data["profiles"]]
        return ProfileSet(set_id=data["set_id"], scene_key=key, source="cache:" + data.get("source", "?"),
                          profiles=profiles, notes=data.get("notes", ""))
    except (OSError, KeyError, TypeError, ValueError, json.JSONDecodeError):
        return None


def _store_plan(pset: ProfileSet) -> None:
    try:
        os.makedirs(PLAN_CACHE_DIR, exist_ok=True)
        with open(os.path.join(PLAN_CACHE_DIR, pset.scene_key + ".json"), "w", encoding="utf-8") as f:
            json.dump({"set_id": pset.set_id, "source": pset.source, "notes": pset.notes,
                       "profiles": [p.to_wire() for p in pset.profiles]}, f, indent=1)
    except OSError as e:
        print(f"[planner] could not cache plan: {e}")


class ProfilePlanner:
    """Plans a profile set for a scene and selects among its loops as readings come in."""

    def __init__(self, link: GeminiLink):
        self.link = link
        self.profile_set: Optional[ProfileSet] = None
        self.events: List[str] = []

    async def plan(self, scene: Dict[str, Any], allow_cache: bool = True) -> ProfileSet:
        key = scene_key(scene)
        if allow_cache:
            cached = _load_cached_plan(key)
            if cached is not None:
                self.profile_set = cached
                # Even on a cached plan, tell Gemini what it designed so selection turns have context.
                asyncio.create_task(self._prime_selector(scene, cached))
                return cached

        fallback = offline_profiles(scene)
        profiles, source, notes = None, "offline", "offline template palette"
        if self.link.available:
            await self.link.open()
            plan = await self.link.ask(PLANNER_SYSTEM, _plan_request(scene, self.link.is_live),
                                       PLAN_TIMEOUT_S, expect_tool="plan_profiles", remember=True)
            profiles = profiles_from_plan(plan, fallback)
            if profiles is not None:
                source = "gemini-" + ("live" if self.link.is_live else "http")
                notes = str(plan.get("notes", ""))
            else:
                print("[planner] Gemini returned no usable plan; using offline templates")
        if profiles is None:
            profiles = fallback

        pset = ProfileSet(set_id=make_set_id(key, profiles), scene_key=key, source=source,
                          profiles=profiles, notes=notes)
        self.profile_set = pset
        _store_plan(pset)
        return pset

    async def _prime_selector(self, scene: Dict[str, Any], pset: ProfileSet) -> None:
        if not self.link.available:
            return
        await self.link.open()
        summary = ("Context for this session (no function call needed yet). Scene: " +
                   json.dumps(scene, separators=(",", ":")) +
                   "\nProfiles already designed and synthesised: " +
                   json.dumps([{"id": p.id, "prompt": p.prompt} for p in pset.profiles],
                              separators=(",", ":")) +
                   "\nFrom now on answer spray readings by calling select_profile.")
        await self.link.ask(SELECTOR_SYSTEM, summary, PLAN_TIMEOUT_S, expect_tool="select_profile",
                            remember=True)

    def note_event(self, name: str, detail: Dict[str, Any]) -> None:
        stamp = time.strftime("%H:%M:%S")
        self.events.append(f"{stamp} {name} {json.dumps(detail, separators=(',', ':'))}")
        del self.events[:-12]

    async def select(self, state: Dict[str, Any]) -> Selection:
        """Gemini's choice for this reading, or the rule choice when Gemini is unavailable/slow."""
        rules = rule_select(state)
        if rules.profile == "" or not self.link.available or self.profile_set is None:
            return rules
        data = await self.link.ask(SELECTOR_SYSTEM, _select_request(state, self.events, self.link.is_live),
                                   self.link.select_timeout, expect_tool="select_profile")
        if not isinstance(data, dict):
            return rules
        profile = str(data.get("profile", "")).strip()
        if profile not in LOOP_ROLES or self.profile_set.by_id(profile) is None:
            return rules
        try:
            gain = float(data.get("gain", rules.gain))
        except (TypeError, ValueError):
            gain = rules.gain
        gain = max(0.2, min(1.0, gain))
        return Selection(profile, gain, "gemini", str(data.get("reason", ""))[:80], rules.state_key)
