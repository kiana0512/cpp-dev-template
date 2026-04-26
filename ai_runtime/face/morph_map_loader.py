from __future__ import annotations

import json
from pathlib import Path
from typing import Any


class MorphMapLoader:
    def __init__(self, path: str | Path) -> None:
        self.path = Path(path)
        with self.path.open("r", encoding="utf-8-sig") as f:
            data = json.load(f)
        if not isinstance(data, dict):
            raise ValueError(f"Morph map must be a JSON object: {self.path}")
        self.data: dict[str, Any] = data
        self.default_morph_mappings: dict[str, str] = {
            str(k): str(v) for k, v in (data.get("default_morph_mappings") or {}).items()
        }
        self.default_phoneme_mappings: dict[str, str] = {
            str(k): str(v) for k, v in (data.get("default_phoneme_mappings") or {}).items()
        }

    def get_supported_morph_keys(self) -> list[str]:
        return [k for k, v in self.default_morph_mappings.items() if v]

    def get_supported_phoneme_keys(self) -> list[str]:
        return [k for k, v in self.default_phoneme_mappings.items() if v]

    def is_supported_protocol_key(self, key: str) -> bool:
        return bool(self.protocol_to_ue_name(key))

    def protocol_to_ue_name(self, key: str) -> str:
        if key in self.default_morph_mappings:
            return self.default_morph_mappings.get(key, "")
        if key in self.default_phoneme_mappings:
            return self.default_phoneme_mappings.get(key, "")
        return ""

    def get_all_protocol_keys(self) -> list[str]:
        keys: list[str] = []
        for k in self.default_morph_mappings:
            if self.default_morph_mappings[k]:
                keys.append(k)
        for k in self.default_phoneme_mappings:
            if self.default_phoneme_mappings[k]:
                keys.append(k)
        return keys
