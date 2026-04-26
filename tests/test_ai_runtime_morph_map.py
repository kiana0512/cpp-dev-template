from pathlib import Path

from ai_runtime.face.morph_map_loader import MorphMapLoader


def test_morph_map_loads_supported_keys() -> None:
    loader = MorphMapLoader(Path("configs/blendshape_map_yyb_miku.json"))
    for key in ["jawOpen", "mouthSmile", "eyeBlinkLeft"]:
        assert loader.is_supported_protocol_key(key)
    for key in ["a", "i", "u", "e", "o"]:
        assert loader.is_supported_protocol_key(key)
    assert "noseSneerLeft" not in loader.get_all_protocol_keys()
    assert loader.protocol_to_ue_name("noseSneerLeft") == ""
