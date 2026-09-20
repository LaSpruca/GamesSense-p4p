"""
Disk cache for synthesised clips, keyed by everything that determines the waveform.

Profile prompts come from Gemini and are the same text every time the same scene is planned (the
planner caches its plan by scene key), so the second session in a room can serve every clip from
here instead of paying ~30 s of HaptiGen per profile. Placeholder-synth clips are keyed
separately so a run without weights can never shadow a real clip.
"""

from __future__ import annotations

import hashlib
import os
from typing import Optional

CACHE_DIR = os.path.join(os.path.dirname(__file__), ".clip_cache")


def key_for(prompt: str, duration_s: float, seed: int, candidates: int, backend: str) -> str:
    material = f"{backend}|{duration_s:.3f}|{seed}|{candidates}|{prompt}".encode("utf-8")
    return hashlib.sha1(material).hexdigest()


def _path(key: str) -> str:
    return os.path.join(CACHE_DIR, key + ".wav")


def get(key: str) -> Optional[bytes]:
    try:
        with open(_path(key), "rb") as f:
            return f.read()
    except FileNotFoundError:
        return None
    except OSError:
        return None


def put(key: str, wav: bytes) -> None:
    """Best effort: a full or read-only disk must not fail a generation."""
    try:
        os.makedirs(CACHE_DIR, exist_ok=True)
        tmp = _path(key) + ".tmp"
        with open(tmp, "wb") as f:
            f.write(wav)
        os.replace(tmp, _path(key))
    except OSError as e:
        print(f"[clip-cache] could not write {key}: {e}")
