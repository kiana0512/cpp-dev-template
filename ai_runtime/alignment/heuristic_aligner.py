from __future__ import annotations

from pathlib import Path

from .base import AlignerBackend, PhonemeSegment
from .pinyin_viseme import text_to_viseme_tokens


class HeuristicAligner(AlignerBackend):
    name = "heuristic"

    def align(self, text: str, wav_path: Path, duration_sec: float) -> list[PhonemeSegment]:
        duration = max(0.001, float(duration_sec))
        tokens = text_to_viseme_tokens(text)
        if not tokens:
            tokens = ["pause"]

        speech_tokens = max(1, len(tokens))
        step = duration / speech_tokens
        segments: list[PhonemeSegment] = []
        cursor = 0.0
        for i, token in enumerate(tokens):
            end = duration if i == len(tokens) - 1 else min(duration, cursor + step)
            segments.append(
                PhonemeSegment(
                    start_sec=round(cursor, 6),
                    end_sec=round(max(cursor, end), 6),
                    phoneme=token,
                    confidence=0.55 if token != "pause" else 0.75,
                    source=self.name,
                )
            )
            cursor = end

        if segments[0].start_sec > 0:
            segments.insert(0, PhonemeSegment(0.0, segments[0].start_sec, "pause", 0.5, self.name))
        if segments[-1].end_sec < duration:
            segments.append(PhonemeSegment(segments[-1].end_sec, duration, "pause", 0.5, self.name))
        return segments
