from pathlib import Path

from ai_runtime.skeleton.skeleton_semantic_analyzer import analyze_skeleton_semantics
from ai_runtime.skeleton.skeleton_tree_loader import load_skeleton_tree


def test_skeleton_semantics_find_miku_core() -> None:
    path = next(Path("configs").glob("skeleton_tree*.json"))
    data = analyze_skeleton_semantics(load_skeleton_tree(path))
    bones = data["semantic_bones"]
    assert bones["neck"]["bone"] == "首"
    assert bones["head"]["bone"] == "頭"
    assert bones["spine"]["bone"] == "上半身"
    assert bones["chest"]["bone"] == "上半身2"
    assert data["drive_skeleton_by_default"] is False
