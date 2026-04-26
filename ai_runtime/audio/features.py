from __future__ import annotations

from dataclasses import dataclass
import math
from typing import Any

try:
    import numpy as np  # type: ignore
except Exception:  # pragma: no cover
    np = None  # type: ignore

from .smoothing import smooth_attack_release, smooth_moving_average


@dataclass
class AudioFrameFeature:
    frame_index: int
    time_sec: float
    rms: float
    rms_norm: float
    energy_norm: float
    is_speech_like: bool


def _percentile(values: list[float], pct: float) -> float:
    if not values:
        return 0.0
    ordered = sorted(values)
    idx = int(round((len(ordered) - 1) * pct / 100.0))
    return ordered[max(0, min(len(ordered) - 1, idx))]


def compute_frame_features(samples: Any, sample_rate: int, fps: float) -> list[AudioFrameFeature]:
    if sample_rate <= 0:
        raise ValueError("sample_rate must be positive")
    if fps <= 0:
        raise ValueError("fps must be positive")
    data = np.asarray(samples, dtype=np.float32) if np is not None else [float(v) for v in samples]
    duration = len(data) / float(sample_rate) if len(data) else 0.0
    frame_count = max(1, int(math.ceil(duration * fps)))
    hop = sample_rate / float(fps)

    rms_values: list[float] = []
    for i in range(frame_count):
        start = int(round(i * hop))
        end = int(round((i + 1) * hop))
        chunk = data[start:min(end, len(data))]
        if len(chunk):
            if np is not None:
                rms = float(np.sqrt(np.mean(np.square(chunk))))
            else:
                rms = math.sqrt(sum(float(x) * float(x) for x in chunk) / len(chunk))
        else:
            rms = 0.0
        rms_values.append(rms)

    nonzero = [v for v in rms_values if v > 1e-6]
    peak = max(nonzero) if nonzero else 1.0
    p90 = float(np.percentile(nonzero, 90)) if (nonzero and np is not None) else _percentile(nonzero, 90)
    denom = max(peak * 0.65, p90, 1e-6)
    norm = [min(1.0, v / denom) for v in rms_values]
    norm = smooth_moving_average(smooth_attack_release(norm, attack=0.72, release=0.22), window=3)

    features: list[AudioFrameFeature] = []
    for i, (rms, rms_norm) in enumerate(zip(rms_values, norm)):
        energy = rms_norm * rms_norm
        features.append(
            AudioFrameFeature(
                frame_index=i,
                time_sec=i / float(fps),
                rms=round(rms, 6),
                rms_norm=round(rms_norm, 6),
                energy_norm=round(energy, 6),
                is_speech_like=rms_norm > 0.08,
            )
        )
    return features
