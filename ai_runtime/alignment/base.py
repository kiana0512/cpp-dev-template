from __future__ import annotations

from abc import ABC, abstractmethod
from dataclasses import dataclass
from pathlib import Path


@dataclass
class PhonemeSegment:
    start_sec: float
    end_sec: float
    phoneme: str
    confidence: float
    source: str


class AlignerBackend(ABC):
    name = "base"

    @abstractmethod
    def align(self, text: str, wav_path: Path, duration_sec: float) -> list[PhonemeSegment]:
        ...
