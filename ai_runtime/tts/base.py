from __future__ import annotations

from abc import ABC, abstractmethod
from pathlib import Path
from typing import Optional


class TtsBackend(ABC):
    name = "base"

    @abstractmethod
    def synthesize(
        self,
        text: str,
        out_wav: Path,
        speaker_wav: Optional[Path] = None,
        emotion: str = "neutral",
        emotion_prompt: Optional[str] = None,
        target_duration_sec: Optional[float] = None,
        language: str = "auto",
    ) -> Path:
        ...
