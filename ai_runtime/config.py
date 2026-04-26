from __future__ import annotations

import json
from pathlib import Path
from typing import Any

from .paths import resolve_project_path


def load_runtime_config(path: str | Path | None = None) -> dict[str, Any]:
    config_path = resolve_project_path(path) if path else None
    if not config_path or not config_path.exists():
        return {}
    with config_path.open("r", encoding="utf-8-sig") as f:
        data = json.load(f)
    if not isinstance(data, dict):
        raise ValueError(f"Runtime config must be a JSON object: {config_path}")
    return data


def nested_get(data: dict[str, Any], *keys: str, default: Any = None) -> Any:
    current: Any = data
    for key in keys:
        if not isinstance(current, dict) or key not in current:
            return default
        current = current[key]
    return current
