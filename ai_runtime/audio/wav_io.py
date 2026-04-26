from __future__ import annotations

import wave
from array import array
from pathlib import Path
from typing import Any

try:
    import numpy as np  # type: ignore
except Exception:  # pragma: no cover
    np = None  # type: ignore


def load_wav_mono(path: Path) -> tuple[int, Any]:
    try:
        with wave.open(str(path), "rb") as wf:
            channels = wf.getnchannels()
            sample_width = wf.getsampwidth()
            sample_rate = wf.getframerate()
            frames = wf.getnframes()
            raw = wf.readframes(frames)
    except wave.Error as exc:
        raise ValueError(f"Unsupported or invalid WAV file: {path}: {exc}") from exc

    if sample_width != 2:
        raise ValueError(f"Only int16 WAV is supported by the default runtime: {path}")
    if channels < 1:
        raise ValueError(f"WAV has no channels: {path}")

    if np is not None:
        data = np.frombuffer(raw, dtype="<i2").astype(np.float32) / 32768.0
        if channels > 1:
            data = data.reshape(-1, channels).mean(axis=1)
        return sample_rate, np.clip(data.astype(np.float32), -1.0, 1.0)

    ints = array("h")
    ints.frombytes(raw)
    values = [max(-1.0, min(1.0, sample / 32768.0)) for sample in ints]
    if channels > 1:
        mono = []
        for i in range(0, len(values), channels):
            chunk = values[i : i + channels]
            if chunk:
                mono.append(sum(chunk) / len(chunk))
        values = mono
    return sample_rate, values


def write_wav_mono(path: Path, sample_rate: int, samples: Any) -> None:
    path.parent.mkdir(parents=True, exist_ok=True)
    if np is not None:
        mono = np.asarray(samples, dtype=np.float32)
        if mono.ndim != 1:
            raise ValueError("write_wav_mono expects a 1-D mono sample array")
        pcm_bytes = (np.clip(mono, -1.0, 1.0) * 32767.0).astype("<i2").tobytes()
    else:
        pcm = array("h", [int(max(-1.0, min(1.0, float(v))) * 32767.0) for v in samples])
        pcm_bytes = pcm.tobytes()
    with wave.open(str(path), "wb") as wf:
        wf.setnchannels(1)
        wf.setsampwidth(2)
        wf.setframerate(int(sample_rate))
        wf.writeframes(pcm_bytes)
