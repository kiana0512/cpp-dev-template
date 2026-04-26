from __future__ import annotations

import random

from ai_runtime.audio.smoothing import clamp01

from .morph_map_loader import MorphMapLoader


def generate_blinks(
    frame_count: int,
    fps: float,
    loader: MorphMapLoader,
    seed: int = 1337,
    interval_min_sec: float = 2.0,
    interval_max_sec: float = 5.0,
) -> list[dict[str, float]]:
    frames = [dict[str, float]() for _ in range(frame_count)]
    if frame_count <= 0:
        return frames
    if not (loader.is_supported_protocol_key("eyeBlinkLeft") or loader.is_supported_protocol_key("eyeBlinkRight")):
        return frames

    rng = random.Random(seed)
    t = rng.uniform(interval_min_sec, min(interval_max_sec, interval_min_sec + 1.0))
    while t < frame_count / fps:
        start = int(round(t * fps))
        length = rng.randint(2, 4)
        if length == 2:
            curve = [0.35, 1.0]
        elif length == 3:
            curve = [0.25, 1.0, 0.35]
        else:
            curve = [0.15, 0.85, 1.0, 0.25]
        for offset, value in enumerate(curve):
            idx = start + offset
            if 0 <= idx < frame_count:
                if loader.is_supported_protocol_key("eyeBlinkLeft"):
                    frames[idx]["eyeBlinkLeft"] = clamp01(value)
                if loader.is_supported_protocol_key("eyeBlinkRight"):
                    frames[idx]["eyeBlinkRight"] = clamp01(value)
        t += rng.uniform(interval_min_sec, interval_max_sec)
    return frames
