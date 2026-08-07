"""
What counts as a usable haptic clip.

Shared by the server (which rejects bad candidates at generation time) and visualise.py (which
reports on clips after the fact), so the two can never disagree about what "degenerate" means.
Stdlib only — the reporting tool must run without torch.

Two failure modes, measured over a 12-scene sweep at 1.5 s:

  silence   the model emits a clip below the perceptibility floor. HapticGen's own serve script
            screens for this.
  drone     NOT screened by HapticGen. Instead of a shaped effect the model emits a continuous
            near-full-amplitude buzz: RMS ~0.62, where a full-scale sine is 0.707. It renders as
            a solid 255 block on the controller -- no attack, no decay, nothing to feel -- and it
            masks every other haptic in the scene. Real effects measured RMS 0.07-0.25.

Crest factor (peak/RMS) separates them cleanly: drones ~1.5, real effects >3.
"""

MIN_PEAK_AMPLITUDE = 0.05
MIN_995TH_AMPLITUDE = 0.02
MAX_RMS = 0.45
MIN_CREST_FACTOR = 2.0


def is_silent(peak: float) -> bool:
    return peak < MIN_PEAK_AMPLITUDE


def is_degenerate(peak: float, rms: float) -> bool:
    """True for the constant-drone failure mode: loud everywhere, shaped nowhere."""
    return rms > MAX_RMS or (rms > 1e-6 and peak / rms < MIN_CREST_FACTOR)


def crest_factor(peak: float, rms: float) -> float:
    return peak / rms if rms > 1e-6 else 0.0


def classify(peak: float, rms: float) -> str:
    """One word for the clip's state: 'silent' | 'drone' | 'shaped'."""
    if is_silent(peak):
        return "silent"
    if is_degenerate(peak, rms):
        return "drone"
    return "shaped"
