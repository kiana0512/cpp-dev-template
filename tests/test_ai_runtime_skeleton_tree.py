from pathlib import Path

from ai_runtime.skeleton.skeleton_tree_loader import load_skeleton_tree


def test_skeleton_tree_loads_key_bones() -> None:
    path = next(Path("configs").glob("skeleton_tree*.json"))
    tree = load_skeleton_tree(path)
    assert tree.bone_count > 0
    assert tree.root_names
    for name in ["首", "頭", "上半身", "上半身2", "目_R", "目_L"]:
        bone = tree.get_bone_by_name(name)
        assert bone is not None
        assert "path" in bone
        assert "children_indices" in bone
    head = tree.get_bone_by_name("頭")
    assert head is not None
    assert isinstance(head.get("parent_index"), int)
    assert tree.get_path("頭")
