from __future__ import annotations

from pathlib import Path
from typing import Any

from ai_runtime.alignment.base import AlignerBackend, PhonemeSegment
from ai_runtime.alignment.heuristic_aligner import HeuristicAligner
from ai_runtime.audio.features import compute_frame_features
from ai_runtime.audio.wav_io import load_wav_mono
from ai_runtime.protocol.expression_frame_schema import (
    make_audio_state,
    make_emotion_state,
    make_expression_frame,
    make_head_pose,
    make_meta,
)

from .blink_generator import generate_blinks
from .emotion_profiles import emotion_to_morphs
from .morph_map_loader import MorphMapLoader
from .viseme_mapper import map_viseme_to_morphs


def _merge_blendshapes(*parts: dict[str, float]) -> dict[str, float]:
    merged: dict[str, float] = {}
    for part in parts:
        for key, value in part.items():
            merged[key] = max(float(value), merged.get(key, 0.0))
    return merged


def _segment_for_time(segments: list[PhonemeSegment], time_sec: float) -> PhonemeSegment:
    for segment in segments:
        if segment.start_sec <= time_sec < segment.end_sec:
            return segment
    return segments[-1] if segments else PhonemeSegment(0.0, 0.0, "pause", 0.0, "none")


class MorphTimelineGenerator:
    def __init__(
        self,
        morph_map_path: str | Path,
        alignment_backend: AlignerBackend | None = None,
    ) -> None:
        self.loader = MorphMapLoader(morph_map_path)
        self.alignment_backend = alignment_backend or HeuristicAligner()

    def generate(
        self,
        text: str,
        wav_path: str | Path,
        emotion: str,
        fps: float,
        character_id: str,
        emotion_strength: float = 0.7,
        blink_enabled: bool = True,
        tts_backend: str = "unknown",
        skeleton_tree_available: bool = False,
        meta_extra: dict[str, Any] | None = None,
    ) -> list[dict[str, Any]]:
        wav = Path(wav_path)
        sample_rate, samples = load_wav_mono(wav)
        duration = len(samples) / float(sample_rate) if sample_rate else 0.0
        features = compute_frame_features(samples, sample_rate, fps)
        segments = self.alignment_backend.align(text, wav, duration)
        emotion_base = emotion_to_morphs(emotion, self.loader, emotion_strength)
        blinks = generate_blinks(len(features), fps, self.loader) if blink_enabled else [dict() for _ in features]

        frames: list[dict[str, Any]] = []
        for feature in features:
            segment = _segment_for_time(segments, feature.time_sec)
            viseme = map_viseme_to_morphs(segment.phoneme, feature.rms_norm, self.loader)
            blendshapes = _merge_blendshapes(viseme, emotion_base, blinks[feature.frame_index])
            meta = make_meta(
                text=text,
                frame_index=feature.frame_index,
                time_sec=feature.time_sec,
                tts_backend=tts_backend,
                alignment_backend=self.alignment_backend.name,
                skeleton_tree_available=skeleton_tree_available,
                extra=meta_extra,
            )
            frame = make_expression_frame(
                sequence_id=feature.frame_index + 1,
                time_sec=feature.time_sec,
                character_id=character_id,
                audio=make_audio_state(feature.is_speech_like, feature.rms_norm, segment.phoneme),
                emotion=make_emotion_state(emotion, emotion_strength),
                blendshapes=blendshapes,
                head_pose=make_head_pose(0.0, 0.0, 0.0),
                meta=meta,
            )
            frames.append(frame)
        return frames
