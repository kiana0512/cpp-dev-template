from __future__ import annotations

import json
from dataclasses import dataclass
from pathlib import Path
from typing import Any


@dataclass
class SkeletonTree:
    path: Path
    raw: dict[str, Any]

    def __post_init__(self) -> None:
        self.bones: list[dict[str, Any]] = list(self.raw.get("bones") or [])
        self.bone_count: int = int(self.raw.get("bone_count") or len(self.bones))
        self.root_names: list[str] = list(self.raw.get("root_names") or [])
        self.tree: Any = self.raw.get("tree")
        self.source_actor: str = str(self.raw.get("source_actor") or "")
        self.source_component: str = str(self.raw.get("source_component") or "")
        self.source_skinned_asset: str = str(self.raw.get("source_skinned_asset") or "")
        self._by_name = {str(b.get("name")): b for b in self.bones if b.get("name") is not None}
        self._by_index = {int(b.get("index")): b for b in self.bones if b.get("index") is not None}

    def get_bone_by_name(self, name: str) -> dict[str, Any] | None:
        return self._by_name.get(name)

    def get_bone_by_index(self, index: int) -> dict[str, Any] | None:
        return self._by_index.get(index)

    def find_bones_by_keywords(self, keywords: list[str]) -> list[dict[str, Any]]:
        lowered = [k.lower() for k in keywords]
        found: list[dict[str, Any]] = []
        for bone in self.bones:
            haystack = f"{bone.get('name', '')} {bone.get('path', '')}".lower()
            if any(k in haystack for k in lowered):
                found.append(bone)
        return found

    def find_descendants(self, bone_name: str) -> list[dict[str, Any]]:
        root = self.get_bone_by_name(bone_name)
        if not root:
            return []
        result: list[dict[str, Any]] = []
        stack = list(root.get("children_indices") or [])
        while stack:
            index = int(stack.pop())
            bone = self.get_bone_by_index(index)
            if not bone:
                continue
            result.append(bone)
            stack.extend(list(bone.get("children_indices") or []))
        return result

    def find_ancestors(self, bone_name: str) -> list[dict[str, Any]]:
        bone = self.get_bone_by_name(bone_name)
        result: list[dict[str, Any]] = []
        while bone and int(bone.get("parent_index", -1)) >= 0:
            parent = self.get_bone_by_index(int(bone["parent_index"]))
            if not parent:
                break
            result.append(parent)
            bone = parent
        return result

    def get_path(self, bone_name: str) -> str | None:
        bone = self.get_bone_by_name(bone_name)
        return str(bone.get("path")) if bone and bone.get("path") is not None else None


def load_skeleton_tree(path: str | Path) -> SkeletonTree:
    p = Path(path)
    with p.open("r", encoding="utf-8-sig") as f:
        raw = json.load(f)
    if not isinstance(raw, dict):
        raise ValueError(f"Skeleton tree must be a JSON object: {p}")
    if "bones" not in raw or not isinstance(raw["bones"], list):
        raise ValueError(f"Skeleton tree is missing a bones list: {p}")
    return SkeletonTree(path=p, raw=raw)
