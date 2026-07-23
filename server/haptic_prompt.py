"""
Server-side prompt generation for the AI-haptics pipeline.

This is the part that used to run ON the Quest headset (Assets/HapticAI/Scripts/Pipeline
+ Prompt). It has been moved here so the headset makes a single call: it sends the raw
event cues and this module turns them into a HaptiGen text prompt.

Two stages, mirroring the original Unity code:
  1. build_brief()  -- ports Pipeline/DefaultContributors.cs: cues -> a natural-language brief.
  2. run_prompt_chain() -- ports Prompt/GeminiPromptStage.cs + Config/HapticAIConfig.cs's default
     three-stage chain (environment-summary -> haptic-design-brief -> haptigen-prompt), run
     through Gemini. If no GEMINI_API_KEY is configured it falls back to the deterministic
     offline template (ports Prompt/OfflinePromptStage.cs) so the endpoint always works.

Keeping the wording identical to the Unity offline path means the server's offline output
matches what the headset would produce on its own.
"""

from __future__ import annotations

import os
from dataclasses import dataclass, field
from typing import Dict, List, Optional

# Load a local server/.env (e.g. GEMINI_API_KEY=..., GEMINI_MODEL=...) when python-dotenv is
# installed. Optional — you can also just export the vars in your shell.
try:
    from dotenv import load_dotenv
    load_dotenv(os.path.join(os.path.dirname(__file__), ".env"))
except ImportError:
    pass

# Gemini is optional: the module runs (offline template) without it installed/configured.
try:
    import google.generativeai as genai
except Exception:  # pragma: no cover - only needed when a key is configured
    genai = None


DEFAULT_MODEL = os.environ.get("GEMINI_MODEL", "gemini-2.5-flash")


# --- Prompt chain definition (ports HapticAIConfig.cs promptStages defaults) ----------------

@dataclass
class PromptStage:
    name: str
    system_instruction: str
    user_template: str


DEFAULT_PROMPT_STAGES: List[PromptStage] = [
    PromptStage(
        name="environment-summary",
        system_instruction=(
            "You describe a VR fire-training moment in one vivid sentence, "
            "focusing on the physical sensation a hand would feel."
        ),
        user_template="Cues: {brief}\nSummarise the moment in one sentence.",
    ),
    PromptStage(
        name="haptic-design-brief",
        system_instruction=(
            "You are a haptics designer. Turn a described moment into a short "
            "brief naming texture, sharpness, rhythm and how it evolves over time."
        ),
        user_template="Moment: {input}\nWrite the haptic design brief.",
    ),
    PromptStage(
        name="haptigen-prompt",
        system_instruction=(
            "You write concise text prompts for a text-to-haptics model (HaptiGen). "
            "Output ONLY the prompt, one line, no preamble."
        ),
        user_template="Design brief: {input}\nWrite the HaptiGen prompt.",
    ),
]


# --- Event -> brief (ports Pipeline/DefaultContributors.cs) ---------------------------------

_EVENT_DESCRIPTORS = {
    "Ignite": "a fire suddenly ignites nearby",
    "Spread": "flames spread to a new surface",
    "Extinguish": "a burning patch is snuffed out into a hiss of steam",
    "SprayTick": "extinguisher foam is being sprayed under pressure",
}


def _clamp01(v: float) -> float:
    return 0.0 if v < 0.0 else 1.0 if v > 1.0 else v


def event_label(event: str, custom_name: Optional[str]) -> str:
    """Mirrors HapticEvent.Name: Custom events use their custom_name."""
    if event == "Custom":
        return custom_name or "custom"
    return event


def build_brief(
    event: str,
    numbers: Dict[str, float],
    tags: Dict[str, str],
    custom_name: Optional[str] = None,
) -> str:
    """Turn the raw cues into the same natural-language brief the headset used to build."""
    label = event_label(event, custom_name)
    descriptors: List[str] = []

    # EventTypeContributor
    descriptors.append(_EVENT_DESCRIPTORS.get(event, label))

    # SurfaceMaterialContributor
    surface = tags.get("surface")
    if surface:
        descriptors.append(f"on a {surface.lower().replace('_', ' ')} surface")

    # FireIntensityContributor
    if "intensity" in numbers:
        i = _clamp01(float(numbers["intensity"]))
        word = "faint" if i < 0.33 else "moderate" if i < 0.66 else "intense"
        descriptors.append(f"{word} in intensity")

    # DistanceContributor
    if "distance" in numbers:
        d = max(0.0, float(numbers["distance"]))
        word = (
            "very close to the hand" if d < 0.5
            else "an arm's length away" if d < 1.5
            else "across the room"
        )
        descriptors.append(word)

    # TriggerDepthContributor
    if "triggerDepth" in numbers:
        t = _clamp01(float(numbers["triggerDepth"]))
        word = (
            "a gentle trickle" if t < 0.4
            else "a steady stream" if t < 0.8
            else "a full-force blast"
        )
        descriptors.append(f"delivered as {word}")

    # HapticContext.ToBrief(): "Event: <label>; d1; d2; ..."
    return "Event: " + label + "".join(f"; {d}" for d in descriptors)


def _offline_prompt(brief: str) -> str:
    """Ports Prompt/OfflinePromptStage.cs — deterministic, no network."""
    return (
        f"A short vibrotactile haptic effect representing: {brief}. "
        "Texture and sharpness should match the described intensity and materials."
    )


# --- Prompt chain execution ------------------------------------------------------------------

@dataclass
class PromptResult:
    prompt: str
    trace: List[str] = field(default_factory=list)
    used_gemini: bool = False


def gemini_available() -> bool:
    return genai is not None and bool(os.environ.get("GEMINI_API_KEY"))


def run_prompt_chain(
    brief: str,
    label: str,
    stages: Optional[List[PromptStage]] = None,
    model_name: str = DEFAULT_MODEL,
) -> PromptResult:
    """
    Run the multi-stage chain and return the final HaptiGen prompt + a trace of every stage.
    Falls back to the offline template when Gemini is unavailable.
    """
    if not gemini_available():
        return PromptResult(prompt=_offline_prompt(brief), trace=[f"[offline] {brief}"], used_gemini=False)

    stages = stages or DEFAULT_PROMPT_STAGES
    genai.configure(api_key=os.environ["GEMINI_API_KEY"])

    current = brief  # PromptState.Current starts as the context brief.
    trace: List[str] = []
    for stage in stages:
        user_text = (
            stage.user_template
            .replace("{input}", current or "")
            .replace("{brief}", brief)
            .replace("{event}", label)
        )
        model = genai.GenerativeModel(model_name, system_instruction=stage.system_instruction)
        resp = model.generate_content(
            user_text,
            generation_config={"temperature": 0.7, "max_output_tokens": 256},
        )
        text = (getattr(resp, "text", None) or "").strip()
        if not text:
            # If a stage returns nothing, keep the previous text rather than blanking the chain.
            text = current
        current = text
        trace.append(f"[{stage.name}] {text}")

    return PromptResult(prompt=current, trace=trace, used_gemini=True)


def build_prompt(
    event: str,
    numbers: Dict[str, float],
    tags: Dict[str, str],
    custom_name: Optional[str] = None,
    model_name: str = DEFAULT_MODEL,
) -> PromptResult:
    """Convenience: cues -> brief -> chain -> final HaptiGen prompt."""
    label = event_label(event, custom_name)
    brief = build_brief(event, numbers, tags, custom_name)
    return run_prompt_chain(brief, label, model_name=model_name)
