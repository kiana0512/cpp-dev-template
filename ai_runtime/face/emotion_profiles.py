from __future__ import annotations

from ai_runtime.audio.smoothing import clamp01

from .morph_map_loader import MorphMapLoader


PROFILES: dict[str, dict[str, float]] = {
    "neutral": {},
    "happy": {
        "mouthSmile": 0.45,
        "eyeSquintLeft": 0.18,
        "eyeSquintRight": 0.18,
    },
    "sad": {
        "mouthFrown": 0.42,
        "browInnerUp": 0.22,
    },
    "angry": {
        "browDown": 0.52,
        "eyeSquintLeft": 0.16,
        "eyeSquintRight": 0.16,
    },
    "surprised": {
        "eyeWideLeft": 0.5,
        "eyeWideRight": 0.5,
        "jawOpen": 0.16,
    },
}


def emotion_to_morphs(emotion: str, loader: MorphMapLoader, emotion_strength: float = 0.7) -> dict[str, float]:
    profile = PROFILES.get((emotion or "neutral").lower(), PROFILES["neutral"])
    strength = clamp01(emotion_strength)
    out: dict[str, float] = {}
    for key, value in profile.items():
        if loader.is_supported_protocol_key(key):
            out[key] = clamp01(value * strength)
    return out
