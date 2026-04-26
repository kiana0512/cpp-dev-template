from __future__ import annotations

import json
from pathlib import Path
from typing import Any


def write_skeleton_semantic_map(data: dict[str, Any], out_path: str | Path) -> Path:
    path = Path(out_path)
    path.parent.mkdir(parents=True, exist_ok=True)
    with path.open("w", encoding="utf-8") as f:
        json.dump(data, f, ensure_ascii=False, indent=2)
        f.write("\n")
    return path
