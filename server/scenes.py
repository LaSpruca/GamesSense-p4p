"""
A scene library for exercising the haptics pipeline.

Each scene is a world event the fire-training sim could plausibly raise, chosen to probe a
different corner of the cue space: intensity extremes, near vs far, thermal vs mechanical, and
the four built-in event types plus free-form Custom ones.

`note` says what the scene is probing, so a run is interpretable rather than just a wall of
waveforms. Used by `e2e_test.py --scenes`.
"""

SCENES = [
    {
        "name": "ignite-close-table",
        "event": "Ignite",
        "numbers": {"intensity": 0.95, "distance": 0.3},
        "tags": {"surface": "TABLE"},
        "note": "violent onset right at the hand - should be the sharpest attack in the set",
    },
    {
        "name": "ignite-distant-carpet",
        "event": "Ignite",
        "numbers": {"intensity": 0.3, "distance": 2.5},
        "tags": {"surface": "CARPET"},
        "note": "same event, faint and across the room - tests that distance/intensity actually soften it",
    },
    {
        "name": "spread-curtain",
        "event": "Spread",
        "numbers": {"intensity": 0.7, "distance": 1.0},
        "tags": {"surface": "CURTAIN"},
        "note": "light fabric - expect a lighter, faster flutter than solid surfaces",
    },
    {
        "name": "spread-wood-pallet",
        "event": "Spread",
        "numbers": {"intensity": 0.85, "distance": 0.6},
        "tags": {"surface": "WOODEN_PALLET"},
        "note": "crackling solid fuel - expect coarse, irregular texture",
    },
    {
        "name": "extinguish-steam",
        "event": "Extinguish",
        "numbers": {"intensity": 0.15, "distance": 0.5},
        "tags": {"surface": "TABLE"},
        "note": "the hiss of a fire going out - sustained fizz rather than an impact",
    },
    {
        "name": "extinguish-electrical",
        "event": "Extinguish",
        "numbers": {"intensity": 0.4, "distance": 0.8},
        "tags": {"surface": "ELECTRICAL_PANEL"},
        "note": "does an unusual surface tag change the character at all?",
    },
    {
        "name": "spray-trickle",
        "event": "SprayTick",
        "numbers": {"triggerDepth": 0.15, "distance": 0.4},
        "tags": {},
        "note": "barely squeezed trigger - low end of the continuous-feedback range",
    },
    {
        "name": "spray-full-blast",
        "event": "SprayTick",
        "numbers": {"triggerDepth": 1.0, "distance": 0.3},
        "tags": {},
        "note": "trigger fully down - should be markedly stronger than spray-trickle",
    },
    {
        "name": "custom-flashover",
        "event": "Custom",
        "custom_name": "a flashover erupts across the ceiling",
        "numbers": {"intensity": 1.0, "distance": 0.8},
        "tags": {"surface": "CEILING"},
        "note": "free-form catastrophic event - tests the Custom path end to end",
    },
    {
        "name": "custom-ember",
        "event": "Custom",
        "custom_name": "a smouldering ember pulses under a layer of ash",
        "numbers": {"intensity": 0.1, "distance": 0.2},
        "tags": {"surface": "ASH"},
        "note": "near-silent subtlety - most likely scene to come back imperceptible",
    },
    {
        "name": "custom-pin-pull",
        "event": "Custom",
        "custom_name": "the extinguisher safety pin is yanked free",
        "numbers": {"triggerDepth": 1.0, "distance": 0.05},
        "tags": {"surface": "METAL"},
        "note": "mechanical, not thermal - checks the model isn't just making fire noises",
    },
    {
        "name": "custom-gas-vent",
        "event": "Custom",
        "custom_name": "a gas cylinder vents under high pressure",
        "numbers": {"intensity": 0.8, "distance": 0.5},
        "tags": {"surface": "STEEL_CYLINDER"},
        "note": "sustained pressurised hiss - contrast with the impact-led scenes",
    },
]


def by_name(name: str):
    for scene in SCENES:
        if scene["name"] == name:
            return scene
    raise KeyError(f"no scene named {name!r}; have: {', '.join(s['name'] for s in SCENES)}")
