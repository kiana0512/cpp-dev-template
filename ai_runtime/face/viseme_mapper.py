from __future__ import annotations

from .morph_map_loader import MorphMapLoader
from ai_runtime.audio.smoothing import clamp01


def _put(out: dict[str, float], loader: MorphMapLoader, key: str, value: float) -> None:
    if loader.is_supported_protocol_key(key):
        out[key] = clamp01(value)


def map_viseme_to_morphs(phoneme: str, rms_norm: float, morph_map_loader: MorphMapLoader) -> dict[str, float]:
    p = (phoneme or "pause").lower()
    amp = clamp01(0.15 + 0.85 * rms_norm)
    out: dict[str, float] = {}

    if p == "pause":
        _put(out, morph_map_loader, "jawOpen", 0.0)
        return out
    if p in {"m", "b", "p", "n"}:
        _put(out, morph_map_loader, "jawOpen", 0.06 * amp)
        return out

    jaw_by_phoneme = {
        "a": 0.72,
        "i": 0.28,
        "u": 0.42,
        "e": 0.48,
        "o": 0.62,
    }
    key = p if p in jaw_by_phoneme else "a"
    _put(out, morph_map_loader, "jawOpen", jaw_by_phoneme[key] * amp)
    _put(out, morph_map_loader, key, (0.55 + 0.45 * rms_norm) * amp)
    return out
