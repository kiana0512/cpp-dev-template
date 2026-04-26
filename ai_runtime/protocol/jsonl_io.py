from __future__ import annotations

import json
from pathlib import Path
from typing import Any


def write_jsonl(frames: list[dict[str, Any]], path: str | Path) -> Path:
    p = Path(path)
    p.parent.mkdir(parents=True, exist_ok=True)
    with p.open("w", encoding="utf-8", newline="\n") as f:
        for frame in frames:
            f.write(json.dumps(frame, ensure_ascii=False, separators=(",", ":")))
            f.write("\n")
    return p


def read_jsonl(path: str | Path) -> list[dict[str, Any]]:
    p = Path(path)
    frames: list[dict[str, Any]] = []
    with p.open("r", encoding="utf-8") as f:
        for line_no, line in enumerate(f, start=1):
            stripped = line.strip()
            if not stripped:
                continue
            try:
                obj = json.loads(stripped)
            except json.JSONDecodeError as exc:
                raise ValueError(f"Invalid JSONL at {p}:{line_no}: {exc}") from exc
            frames.append(obj)
    return frames
