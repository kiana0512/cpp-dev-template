from __future__ import annotations

from pathlib import Path
from typing import Any

from .jsonl_io import read_jsonl

FORBIDDEN_KEYS = {"bone_rotation", "skeleton_pose", "control_rig", "bone_rotations", "skeleton_motion_data"}
REQUIRED_FIELDS = {"type", "version", "timestamp_ms", "character_id", "sequence_id", "audio", "emotion", "blendshapes", "head_pose", "meta"}


def _walk_forbidden(obj: Any, path: str = "") -> list[str]:
    errors: list[str] = []
    if isinstance(obj, dict):
        for key, value in obj.items():
            next_path = f"{path}.{key}" if path else str(key)
            if key in FORBIDDEN_KEYS:
                errors.append(f"Forbidden skeleton control field present: {next_path}")
            errors.extend(_walk_forbidden(value, next_path))
    elif isinstance(obj, list):
        for i, value in enumerate(obj):
            errors.extend(_walk_forbidden(value, f"{path}[{i}]"))
    return errors


def validate_frame(frame: dict[str, Any], prev_sequence: int | None = None, prev_timestamp: int | None = None) -> list[str]:
    errors: list[str] = []
    missing = REQUIRED_FIELDS - set(frame.keys())
    if missing:
        errors.append(f"Missing required fields: {sorted(missing)}")
    if frame.get("type") != "expression_frame":
        errors.append("type must be expression_frame")

    sequence = int(frame.get("sequence_id", 0) or 0)
    timestamp = int(frame.get("timestamp_ms", 0) or 0)
    if prev_sequence is not None and sequence <= prev_sequence:
        errors.append("sequence_id must be strictly increasing")
    if prev_timestamp is not None and timestamp < prev_timestamp:
        errors.append("timestamp_ms must be non-decreasing")

    blendshapes = frame.get("blendshapes")
    if not isinstance(blendshapes, dict):
        errors.append("blendshapes must be an object")
    else:
        for key, value in blendshapes.items():
            try:
                numeric = float(value)
            except Exception:
                errors.append(f"blendshape {key} must be numeric")
                continue
            if numeric < 0.0 or numeric > 1.0:
                errors.append(f"blendshape {key} must be in [0,1]")

    meta = frame.get("meta") if isinstance(frame.get("meta"), dict) else {}
    if meta.get("morph_only") is not True:
        errors.append("meta.morph_only must be true")
    if meta.get("skeleton_motion") is not False:
        errors.append("meta.skeleton_motion must be false")
    errors.extend(_walk_forbidden(frame))
    return errors


def validate_frames(frames: list[dict[str, Any]]) -> list[str]:
    errors: list[str] = []
    prev_sequence: int | None = None
    prev_timestamp: int | None = None
    for index, frame in enumerate(frames):
        frame_errors = validate_frame(frame, prev_sequence, prev_timestamp)
        errors.extend([f"frame[{index}]: {e}" for e in frame_errors])
        prev_sequence = int(frame.get("sequence_id", 0) or 0)
        prev_timestamp = int(frame.get("timestamp_ms", 0) or 0)
    return errors


def validate_jsonl(path: str | Path) -> list[str]:
    return validate_frames(read_jsonl(path))
