from __future__ import annotations

from pathlib import Path

from .base import AlignerBackend, PhonemeSegment


class OptionalForcedAligner(AlignerBackend):
    name = "forced_optional"

    def align(self, text: str, wav_path: Path, duration_sec: float) -> list[PhonemeSegment]:
        try:
            import torchaudio  # noqa: F401
        except Exception as exc:
            raise RuntimeError(
                "Optional forced alignment backend is not installed. "
                "Install and configure torchaudio CTC, WhisperX, or ctc-forced-aligner before using it."
            ) from exc
        raise RuntimeError(
            "Optional forced alignment adapter is reserved for a later stage and is not enabled by default."
        )
