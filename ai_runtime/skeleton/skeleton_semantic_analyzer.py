from __future__ import annotations

import argparse
import json
from pathlib import Path
from typing import Any

from .skeleton_semantic_map_writer import write_skeleton_semantic_map
from .skeleton_tree_loader import SkeletonTree, load_skeleton_tree


KEYWORDS: dict[str, list[str]] = {
    "root": ["root", "arm"],
    "global_root": ["全ての親", "全親", "global", "parent"],
    "center": ["センター", "center"],
    "groove": ["グルーブ", "groove"],
    "waist": ["腰", "pelvis", "hips", "waist"],
    "pelvis": ["腰", "pelvis", "hips", "waist"],
    "spine": ["上半身", "spine"],
    "upper_body": ["上半身", "spine"],
    "chest": ["上半身2", "chest"],
    "upper_body2": ["上半身2", "chest"],
    "neck": ["首", "neck"],
    "head": ["頭", "head"],
    "left_eye": ["目_l", "目左", "left_eye", "eye_l", "eye.left"],
    "right_eye": ["目_r", "目右", "right_eye", "eye_r", "eye.right"],
    "tongue_1": ["舌１", "舌1", "tongue1", "tongue_1"],
    "tongue_2": ["舌２", "舌2", "tongue2", "tongue_2"],
    "tongue_3": ["舌３", "舌3", "tongue3", "tongue_3"],
    "left_shoulder": ["肩_l", "左肩", "shoulder_l", "left_shoulder"],
    "right_shoulder": ["肩_r", "右肩", "shoulder_r", "right_shoulder"],
    "left_arm": ["腕_l", "左腕", "arm_l", "left_arm"],
    "right_arm": ["腕_r", "右腕", "arm_r", "right_arm"],
    "left_elbow": ["ひじ_l", "肘_l", "左ひじ", "左肘", "elbow_l", "left_elbow"],
    "right_elbow": ["ひじ_r", "肘_r", "右ひじ", "右肘", "elbow_r", "right_elbow"],
    "left_wrist": ["手首_l", "左手首", "wrist_l", "left_wrist"],
    "right_wrist": ["手首_r", "右手首", "wrist_r", "right_wrist"],
}

EXACT_HINTS: dict[str, list[str]] = {
    "root": ["miku条纹袜_arm"],
    "global_root": ["全ての親"],
    "center": ["センター"],
    "groove": ["グルーブ"],
    "waist": ["腰"],
    "pelvis": ["腰"],
    "spine": ["上半身"],
    "upper_body": ["上半身"],
    "chest": ["上半身2"],
    "upper_body2": ["上半身2"],
    "neck": ["首"],
    "head": ["頭"],
    "right_eye": ["目_R"],
    "left_eye": ["目_L"],
    "tongue_1": ["舌１"],
    "tongue_2": ["舌２"],
    "tongue_3": ["舌３"],
}


def _bone_summary(bone: dict[str, Any]) -> dict[str, Any]:
    return {
        "bone": bone.get("name"),
        "index": bone.get("index"),
        "path": bone.get("path"),
    }


def _score_candidate(slot: str, bone: dict[str, Any]) -> tuple[float, str]:
    name = str(bone.get("name") or "")
    path = str(bone.get("path") or "")
    name_lower = name.lower()
    path_lower = path.lower()
    for exact in EXACT_HINTS.get(slot, []):
        if name == exact:
            return 1.0, f"exact name match: {exact}"
    for kw in KEYWORDS.get(slot, []):
        kw_lower = kw.lower()
        if name_lower == kw_lower:
            return 0.92, f"name keyword match: {kw}"
        if kw_lower in name_lower:
            return 0.82, f"name contains keyword: {kw}"
        if kw_lower in path_lower:
            return 0.68, f"path contains keyword: {kw}"
    return 0.0, "candidate from broad search"


def _find_slot(tree: SkeletonTree, slot: str) -> tuple[dict[str, Any] | None, list[dict[str, Any]], str]:
    candidates: list[dict[str, Any]] = []
    seen: set[int] = set()
    for exact in EXACT_HINTS.get(slot, []):
        bone = tree.get_bone_by_name(exact)
        if bone and int(bone["index"]) not in seen:
            candidates.append(bone)
            seen.add(int(bone["index"]))
    for bone in tree.find_bones_by_keywords(KEYWORDS.get(slot, [])):
        index = int(bone.get("index", -1))
        if index not in seen:
            candidates.append(bone)
            seen.add(index)

    scored = []
    for bone in candidates:
        confidence, reason = _score_candidate(slot, bone)
        scored.append((confidence, reason, bone))
    scored.sort(key=lambda item: (-item[0], int(item[2].get("depth", 9999)), int(item[2].get("index", 999999))))
    if not scored:
        return None, [], f"No candidate matched keywords: {KEYWORDS.get(slot, [])}"
    confidence, reason, selected = scored[0]
    return selected, [item[2] for item in scored[:8]], reason


def analyze_skeleton_semantics(tree: SkeletonTree, character: str = "yyb_miku") -> dict[str, Any]:
    warnings: list[str] = []
    semantic_bones: dict[str, Any] = {}
    for slot in KEYWORDS:
        selected, candidates, reason = _find_slot(tree, slot)
        if selected:
            confidence, reason = _score_candidate(slot, selected)
            semantic_bones[slot] = {
                "bone": selected.get("name"),
                "index": selected.get("index"),
                "path": selected.get("path"),
                "confidence": round(confidence, 3),
                "candidates": [_bone_summary(b) for b in candidates],
                "reason": reason,
            }
        else:
            warning = f"No semantic bone candidate found for {slot}"
            warnings.append(warning)
            semantic_bones[slot] = {
                "bone": None,
                "index": None,
                "path": None,
                "confidence": 0.0,
                "candidates": [],
                "reason": reason,
                "warning": warning,
            }

    return {
        "version": "yyb-miku-skeleton-semantic-map-v1",
        "character": character,
        "source_skeleton_tree": str(tree.path.as_posix()),
        "source_actor": tree.source_actor,
        "source_component": tree.source_component,
        "source_skinned_asset": tree.source_skinned_asset,
        "bone_count": tree.bone_count,
        "root_names": tree.root_names,
        "purpose": "reference_only_for_future_v2m",
        "drive_skeleton_by_default": False,
        "semantic_bones": semantic_bones,
        "warnings": warnings,
        "note": [
            "This file is only for later V2M / Control Rig / AnimBP work.",
            "Current AI runtime still drives Morph Target only.",
        ],
    }


def main() -> int:
    parser = argparse.ArgumentParser(description="Analyze exported UE skeleton tree semantics.")
    parser.add_argument("--skeleton-tree", required=True)
    parser.add_argument("--out", required=True)
    parser.add_argument("--character", default="yyb_miku")
    args = parser.parse_args()

    tree = load_skeleton_tree(Path(args.skeleton_tree))
    result = analyze_skeleton_semantics(tree, character=args.character)
    out = write_skeleton_semantic_map(result, args.out)
    print(json.dumps({"out": str(out), "bone_count": tree.bone_count}, ensure_ascii=False))
    for key in ["root", "waist", "spine", "chest", "neck", "head", "right_eye", "left_eye", "tongue_1", "tongue_2", "tongue_3"]:
        item = result["semantic_bones"].get(key, {})
        print(f"{key} = {item.get('bone')} ({item.get('confidence')})")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
