from __future__ import annotations

import math
import wave
from array import array
from pathlib import Path
from typing import Optional

try:
    import numpy as np  # type: ignore
except Exception:  # pragma: no cover - exercised in minimal Python envs
    np = None  # type: ignore

from .base import TtsBackend


class DryRunTts(TtsBackend):
    name = "dryrun"

    def __init__(self, sample_rate: int = 24000) -> None:
        self.sample_rate = sample_rate

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
        duration = float(target_duration_sec or min(5.0, max(3.0, 2.5 + len(text) * 0.035)))
        sample_count = max(1, int(duration * self.sample_rate))

        if np is not None:
            t = np.arange(sample_count, dtype=np.float32) / float(self.sample_rate)
            syllable_rate = 4.2
            pulse = 0.5 + 0.5 * np.sin(2.0 * math.pi * syllable_rate * t)
            pulse = np.power(np.clip(pulse, 0.0, 1.0), 2.6)
            slow = 0.65 + 0.35 * np.sin(2.0 * math.pi * 0.55 * t + 0.4)
            envelope = np.clip(0.08 + 0.82 * pulse * slow, 0.0, 1.0)
            gap = (np.sin(2.0 * math.pi * 1.15 * t + 1.1) > -0.82).astype(np.float32)
            envelope *= 0.25 + 0.75 * gap
            carrier = (
                0.45 * np.sin(2.0 * math.pi * 185.0 * t)
                + 0.28 * np.sin(2.0 * math.pi * 370.0 * t + 0.3)
                + 0.12 * np.sin(2.0 * math.pi * 555.0 * t + 0.8)
            )
            samples = np.clip(carrier * envelope * 0.42, -1.0, 1.0).astype(np.float32)
            pcm_bytes = (samples * 32767.0).astype("<i2").tobytes()
        else:
            pcm = array("h")
            for n in range(sample_count):
                t = n / float(self.sample_rate)
                pulse = max(0.0, min(1.0, 0.5 + 0.5 * math.sin(2.0 * math.pi * 4.2 * t))) ** 2.6
                slow = 0.65 + 0.35 * math.sin(2.0 * math.pi * 0.55 * t + 0.4)
                gap = 1.0 if math.sin(2.0 * math.pi * 1.15 * t + 1.1) > -0.82 else 0.0
                envelope = max(0.0, min(1.0, 0.08 + 0.82 * pulse * slow)) * (0.25 + 0.75 * gap)
                carrier = (
                    0.45 * math.sin(2.0 * math.pi * 185.0 * t)
                    + 0.28 * math.sin(2.0 * math.pi * 370.0 * t + 0.3)
                    + 0.12 * math.sin(2.0 * math.pi * 555.0 * t + 0.8)
                )
                pcm.append(int(max(-1.0, min(1.0, carrier * envelope * 0.42)) * 32767.0))
            pcm_bytes = pcm.tobytes()

        out_wav.parent.mkdir(parents=True, exist_ok=True)
        with wave.open(str(out_wav), "wb") as wf:
            wf.setnchannels(1)
            wf.setsampwidth(2)
            wf.setframerate(self.sample_rate)
            wf.writeframes(pcm_bytes)
        return out_wav
