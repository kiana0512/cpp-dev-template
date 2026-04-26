from pathlib import Path

from ai_runtime.face.morph_timeline_generator import MorphTimelineGenerator
from ai_runtime.protocol.validator import validate_frames
from ai_runtime.tts.dryrun_tts import DryRunTts


def test_dryrun_timeline_is_valid(tmp_path: Path) -> None:
    wav = tmp_path / "generated.wav"
    DryRunTts().synthesize("你好，我是虚拟人助手", wav)
    generator = MorphTimelineGenerator("configs/blendshape_map_yyb_miku.json")
    frames = generator.generate(
        text="你好，我是虚拟人助手",
        wav_path=wav,
        emotion="happy",
        fps=30,
        character_id="yyb_miku",
        tts_backend="dryrun",
        skeleton_tree_available=True,
    )
    assert len(frames) > 30
    assert validate_frames(frames) == []
    assert all(0.0 <= float(v) <= 1.0 for f in frames for v in f["blendshapes"].values())
    assert all(f["meta"]["morph_only"] is True for f in frames)
    assert all(f["meta"]["skeleton_motion"] is False for f in frames)
    forbidden = ["bone_rotation", "skeleton_pose", "control_rig"]
    assert not any(k in f for f in frames for k in forbidden)
