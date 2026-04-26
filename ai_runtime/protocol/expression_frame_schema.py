from __future__ import annotations

from typing import Any

from ai_runtime.audio.smoothing import clamp01


def _round_float(value: float, digits: int = 5) -> float:
    return round(float(value), digits)


def make_audio_state(playing: bool, rms: float, phoneme_hint: str) -> dict[str, Any]:
    return {
        "playing": bool(playing),
        "rms": _round_float(clamp01(rms)),
        "phoneme_hint": phoneme_hint or "pause",
    }


def make_emotion_state(label: str, confidence: float = 1.0) -> dict[str, Any]:
    return {
        "label": label or "neutral",
        "confidence": _round_float(clamp01(confidence)),
    }


def make_head_pose(pitch: float = 0.0, yaw: float = 0.0, roll: float = 0.0) -> dict[str, float]:
    return {
        "pitch": _round_float(pitch, 4),
        "yaw": _round_float(yaw, 4),
        "roll": _round_float(roll, 4),
    }


def make_meta(
    text: str,
    frame_index: int,
    time_sec: float,
    tts_backend: str,
    alignment_backend: str,
    skeleton_tree_available: bool,
    source: str = "indextts2_morph_v2f",
    extra: dict[str, Any] | None = None,
) -> dict[str, Any]:
    meta: dict[str, Any] = {
        "generated_by": "ai_runtime",
        "source": source,
        "text": text,
        "frame_index": int(frame_index),
        "time_sec": _round_float(time_sec, 4),
        "tts_backend": tts_backend,
        "alignment_backend": alignment_backend,
        "morph_only": True,
        "skeleton_motion": False,
        "skeleton_tree_available": bool(skeleton_tree_available),
    }
    if extra:
        meta.update(extra)
    meta["skeleton_motion"] = False
    meta["morph_only"] = True
    return meta


def make_expression_frame(
    sequence_id: int,
    time_sec: float,
    character_id: str,
    audio: dict[str, Any],
    emotion: dict[str, Any],
    blendshapes: dict[str, float],
    head_pose: dict[str, float],
    meta: dict[str, Any],
) -> dict[str, Any]:
    clean_blendshapes = {str(k): _round_float(clamp01(v)) for k, v in blendshapes.items()}
    return {
        "type": "expression_frame",
        "version": "1.0",
        "timestamp_ms": int(round(max(0.0, time_sec) * 1000.0)),
        "character_id": character_id,
        "sequence_id": int(sequence_id),
        "audio": audio,
        "emotion": emotion,
        "blendshapes": clean_blendshapes,
        "head_pose": head_pose,
        "meta": meta,
    }
