from __future__ import annotations

from dataclasses import dataclass, field
from pathlib import Path
from typing import Any


@dataclass
class LocalModelReport:
    index_tts2_ok: bool
    w2v_bert_ok: bool
    maskgct_ok: bool
    campplus_ok: bool
    bigvgan_ok: bool
    can_run_offline: bool
    looks_local_complete: bool
    remote_references: list[str] = field(default_factory=list)
    missing_local_files: list[str] = field(default_factory=list)
    warnings: list[str] = field(default_factory=list)
    recommendations: list[str] = field(default_factory=list)

    def to_dict(self) -> dict[str, Any]:
        return {
            "index_tts2_ok": self.index_tts2_ok,
            "w2v_bert_ok": self.w2v_bert_ok,
            "maskgct_ok": self.maskgct_ok,
            "campplus_ok": self.campplus_ok,
            "bigvgan_ok": self.bigvgan_ok,
            "can_run_offline": self.can_run_offline,
            "looks_local_complete": self.looks_local_complete,
            "remote_references": self.remote_references,
            "missing_local_files": self.missing_local_files,
            "warnings": self.warnings,
            "recommendations": self.recommendations,
        }


def _read_text(path: Path) -> str:
    return path.read_text(encoding="utf-8-sig")


def analyze_indextts2_local_paths(config_path: Path, model_dir: Path) -> LocalModelReport:
    remote_refs: list[str] = []
    missing: list[str] = []
    warnings: list[str] = []
    recs: list[str] = []

    if not config_path.exists():
        missing.append("config.yaml")
        warnings.append(f"Config file missing: {config_path}")
        recs.append("Ensure models/IndexTTS-2/config.yaml exists.")
        return LocalModelReport(False, False, False, False, False, False, False, remote_refs, missing, warnings, recs)

    config_text = _read_text(config_path)
    lower = config_text.lower()
    patterns = [
        "huggingface.co",
        "hf.co",
        "facebook/",
        "amphion/MaskGCT",
        'from_pretrained("facebook/',
        "from_pretrained('facebook/",
    ]
    for p in patterns:
        if p in lower:
            remote_refs.append(p)

    main_required = [
        "config.yaml",
        "gpt.pth",
        "s2mel.pth",
        "bpe.model",
        "wav2vec2bert_stats.pt",
        "qwen0.6bemo4-merge",
        "qwen0.6bemo4-merge/config.json",
    ]
    main_missing: list[str] = []
    for rel in main_required:
        if not (model_dir / rel).exists():
            missing.append(rel)
            main_missing.append(rel)

    qwen_dir = model_dir / "qwen0.6bemo4-merge"
    model_weight_ok = (qwen_dir / "model.safetensors").exists() or (qwen_dir / "pytorch_model.bin").exists()
    if not model_weight_ok:
        missing.append("qwen0.6bemo4-merge/(model.safetensors or pytorch_model.bin)")
        main_missing.append("qwen0.6bemo4-merge/(model.safetensors or pytorch_model.bin)")
    tokenizer_ok = (qwen_dir / "tokenizer.json").exists() or (qwen_dir / "tokenizer.model").exists()
    if not tokenizer_ok:
        missing.append("qwen0.6bemo4-merge/(tokenizer.json or tokenizer.model)")
        main_missing.append("qwen0.6bemo4-merge/(tokenizer.json or tokenizer.model)")

    w2v_missing: list[str] = []
    w2v_dir = model_dir / "w2v-bert-2.0"
    for rel in ["w2v-bert-2.0", "w2v-bert-2.0/preprocessor_config.json"]:
        if not (model_dir / rel).exists():
            missing.append(rel)
            w2v_missing.append(rel)

    mask_missing: list[str] = []
    mask_required = [
        "MaskGCT/semantic_codec/model.safetensors",
        "MaskGCT/t2s_model/model.safetensors",
        "MaskGCT/s2a_model/s2a_model_1layer/model.safetensors",
        "MaskGCT/s2a_model/s2a_model_full/model.safetensors",
    ]
    for rel in mask_required:
        if not (model_dir / rel).exists():
            missing.append(rel)
            mask_missing.append(rel)

    campplus_missing: list[str] = []
    for rel in ["campplus", "campplus/campplus_cn_common.bin"]:
        if not (model_dir / rel).exists():
            missing.append(rel)
            campplus_missing.append(rel)

    bigvgan_missing: list[str] = []
    bigvgan_config = model_dir / "BigVGAN" / "config.json"
    if not bigvgan_config.exists():
        missing.append("BigVGAN/config.json")
        bigvgan_missing.append("BigVGAN/config.json")
    else:
        try:
            import json

            num_mels = json.loads(bigvgan_config.read_text(encoding="utf-8")).get("num_mels")
            if num_mels != 80:
                missing.append(f"BigVGAN/config.json num_mels={num_mels}, expected 80")
                bigvgan_missing.append("BigVGAN/config.json num_mels must be 80")
        except Exception as exc:
            warnings.append(f"Could not verify BigVGAN num_mels: {exc}")
            bigvgan_missing.append("BigVGAN/config.json unreadable")

    index_ok = len(main_missing) == 0
    w2v_ok = len(w2v_missing) == 0 and w2v_dir.exists()
    mask_ok = len(mask_missing) == 0
    campplus_ok = len(campplus_missing) == 0
    bigvgan_ok = len(bigvgan_missing) == 0
    looks_complete = len(missing) == 0
    can_run_offline = index_ok and w2v_ok and mask_ok and campplus_ok and bigvgan_ok and not remote_refs
    if remote_refs:
        warnings.append("Config may still contain remote HF references.")
        recs.append("Prefer local absolute paths for qwen and wav2vec files.")
    if missing:
        recs.append("Re-download model files or verify local model layout integrity.")
    if not w2v_ok:
        recs.append("Local dependency missing: w2v-bert-2.0. Run: modelscope download --model AI-ModelScope/w2v-bert-2.0 --local_dir .\\models\\IndexTTS-2\\w2v-bert-2.0")
    if not mask_ok:
        recs.append("Local dependency missing: MaskGCT. Run: modelscope download --model amphion/MaskGCT --local_dir .\\models\\IndexTTS-2\\MaskGCT")
    if not campplus_ok:
        recs.append("Local dependency missing: campplus. Verify models\\IndexTTS-2\\campplus\\campplus_cn_common.bin.")
    if not bigvgan_ok:
        recs.append("BigVGAN must be an 80-band model. Verify models\\IndexTTS-2\\BigVGAN\\config.json has num_mels=80.")

    return LocalModelReport(
        index_tts2_ok=index_ok,
        w2v_bert_ok=w2v_ok,
        maskgct_ok=mask_ok,
        campplus_ok=campplus_ok,
        bigvgan_ok=bigvgan_ok,
        can_run_offline=can_run_offline,
        looks_local_complete=looks_complete,
        remote_references=sorted(set(remote_refs)),
        missing_local_files=missing,
        warnings=warnings,
        recommendations=recs,
    )
