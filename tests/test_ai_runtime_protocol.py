from pathlib import Path

from ai_runtime.face.morph_timeline_generator import MorphTimelineGenerator
from ai_runtime.protocol.jsonl_io import read_jsonl, write_jsonl
from ai_runtime.protocol.validator import validate_jsonl
from ai_runtime.tts.dryrun_tts import DryRunTts


def test_jsonl_protocol_roundtrip(tmp_path: Path) -> None:
    wav = tmp_path / "generated.wav"
    frames_path = tmp_path / "frames.jsonl"
    DryRunTts().synthesize("hello", wav)
    frames = MorphTimelineGenerator("configs/blendshape_map_yyb_miku.json").generate(
        text="hello",
        wav_path=wav,
        emotion="neutral",
        fps=30,
        character_id="yyb_miku",
        tts_backend="dryrun",
        skeleton_tree_available=True,
    )
    write_jsonl(frames, frames_path)
    loaded = read_jsonl(frames_path)
    assert validate_jsonl(frames_path) == []
    assert loaded[0]["meta"]["generated_by"] == "ai_runtime"
    assert loaded[0]["meta"]["skeleton_tree_available"] is True
    assert loaded[0]["meta"]["skeleton_motion"] is False
