from __future__ import annotations

import argparse
import re
from pathlib import Path


MARKER = "AI_RUNTIME_LOCAL_PATCH_START"


def read_text(path: Path) -> str:
    return path.read_text(encoding="utf-8-sig")


def write_text(path: Path, text: str) -> None:
    path.write_text(text, encoding="utf-8", newline="")


def backup(path: Path) -> None:
    bak = path.with_suffix(path.suffix + ".bak_local_patch")
    if not bak.exists():
        bak.write_text(read_text(path), encoding="utf-8", newline="")


def patch_infer_v2(path: Path) -> list[str]:
    changes: list[str] = []
    text = read_text(path)
    backup(path)
    if "from huggingface_hub import hf_hub_download" in text:
        text = text.replace("from huggingface_hub import hf_hub_download\n", "")
        changes.append("removed remote hub import from infer_v2.py")
    if MARKER not in text:
        helper = r'''

# AI_RUNTIME_LOCAL_PATCH_START
def _ai_runtime_model_dir():
    root = os.environ.get("INDEXTTS_MODEL_DIR")
    if root:
        return root
    return getattr(globals().get("self", None), "model_dir", "checkpoints")


def _ai_runtime_dep(*parts):
    path = os.path.join(_ai_runtime_model_dir(), *parts)
    if not os.path.exists(path):
        raise FileNotFoundError(
            "Local IndexTTS2 dependency missing: "
            + path
            + ". Run scripts/download_indextts2_local_deps.ps1 or verify the local model layout."
        )
    return path


def _ai_runtime_w2v_dir():
    path = os.environ.get("W2V_BERT_LOCAL_DIR") or os.path.join(_ai_runtime_model_dir(), "w2v-bert-2.0")
    if not os.path.isdir(path):
        raise FileNotFoundError("Local dependency missing: " + path)
    return path


def _ai_runtime_mask_file(filename):
    root = os.environ.get("MASKGCT_LOCAL_DIR") or os.path.join(_ai_runtime_model_dir(), "MaskGCT")
    path = os.path.join(root, *filename.replace("\\", "/").split("/"))
    if not os.path.exists(path):
        raise FileNotFoundError("Local MaskGCT dependency missing: " + path)
    return path
# AI_RUNTIME_LOCAL_PATCH_END
'''
        text = text.replace("import torch.nn.functional as F\n", "import torch.nn.functional as F\n" + helper)
        changes.append("inserted local path helper in infer_v2.py")
    text = text.replace(
        'SeamlessM4TFeatureExtractor.from_pretrained("facebook/w2v-bert-2.0")',
        "SeamlessM4TFeatureExtractor.from_pretrained(_ai_runtime_w2v_dir(), local_files_only=True)",
    )
    text = text.replace(
        'semantic_code_ckpt = hf_hub_download("amphion/MaskGCT", filename="semantic_codec/model.safetensors")',
        'semantic_code_ckpt = _ai_runtime_mask_file("semantic_codec/model.safetensors")',
    )
    text = re.sub(
        r'campplus_ckpt_path\s*=\s*hf_hub_download\(\s*"funasr/campplus"\s*,\s*filename\s*=\s*"campplus_cn_common\.bin"\s*\)',
        'campplus_ckpt_path = _ai_runtime_dep("campplus", "campplus_cn_common.bin")',
        text,
        flags=re.S,
    )
    text = text.replace(
        "        bigvgan_name = self.cfg.vocoder.name\n        self.bigvgan = bigvgan.BigVGAN.from_pretrained(bigvgan_name, use_cuda_kernel=self.use_cuda_kernel)",
        "        bigvgan_name = os.environ.get(\"BIGVGAN_LOCAL_DIR\") or os.path.join(self.model_dir, \"BigVGAN\")\n"
        "        if not os.path.isdir(bigvgan_name):\n"
        "            raise FileNotFoundError(\"Local BigVGAN dependency missing: \" + bigvgan_name)\n"
        "        self.bigvgan = bigvgan.BigVGAN.from_pretrained(bigvgan_name, use_cuda_kernel=self.use_cuda_kernel)",
    )
    if text != read_text(path):
        changes.append("rewrote infer_v2.py local dependency calls")
        write_text(path, text)
    return changes


def patch_maskgct_utils(path: Path) -> list[str]:
    changes: list[str] = []
    text = read_text(path)
    backup(path)
    original = text
    text = text.replace("from huggingface_hub import hf_hub_download\n", "")
    if MARKER not in text:
        helper = r'''

# AI_RUNTIME_LOCAL_PATCH_START
def _ai_runtime_w2v_dir():
    import os
    path = os.environ.get("W2V_BERT_LOCAL_DIR")
    if not path:
        root = os.environ.get("INDEXTTS_MODEL_DIR") or "."
        path = os.path.join(root, "w2v-bert-2.0")
    if not os.path.isdir(path):
        raise FileNotFoundError("Local dependency missing: " + path)
    return path
# AI_RUNTIME_LOCAL_PATCH_END
'''
        text = text.replace("import time\n", "import time\n" + helper)
    text = text.replace(
        'Wav2Vec2BertModel.from_pretrained("facebook/w2v-bert-2.0")',
        "Wav2Vec2BertModel.from_pretrained(_ai_runtime_w2v_dir(), local_files_only=True)",
    )
    if text != original:
        changes.append("patched utils/maskgct_utils.py")
        write_text(path, text)
    return changes


def patch_hf_utils(path: Path) -> list[str]:
    if not path.exists():
        return []
    backup(path)
    text = r'''import os


def load_custom_model_from_hf(repo_id, model_filename="pytorch_model.bin", config_filename="config.yml"):
    root = os.environ.get("INDEXTTS_MODEL_DIR") or os.getcwd()
    safe_repo = str(repo_id).replace("/", os.sep)
    base = os.path.join(root, safe_repo)
    model_path = os.path.join(base, model_filename)
    if not os.path.exists(model_path):
        raise FileNotFoundError("Local custom model file missing: " + model_path)
    if config_filename is None:
        return model_path
    config_path = os.path.join(base, config_filename)
    if not os.path.exists(config_path):
        raise FileNotFoundError("Local custom config file missing: " + config_path)
    return model_path, config_path
'''
    write_text(path, text)
    return ["patched s2mel/hf_utils.py"]


def patch_bigvgan(path: Path) -> list[str]:
    if not path.exists():
        return []
    text = read_text(path)
    original = text
    backup(path)
    text = text.replace("from huggingface_hub import PyTorchModelHubMixin, hf_hub_download", "from huggingface_hub import PyTorchModelHubMixin")
    if "_ai_runtime_no_remote_model" not in text:
        marker = "from huggingface_hub import PyTorchModelHubMixin\n"
        text = text.replace(
            marker,
            marker
            + "\n"
            + "def _ai_runtime_no_remote_model(*args, **kwargs):\n"
            + "    raise RuntimeError(\"Remote model loading is disabled. Pass a local model directory.\")\n",
        )
    text = text.replace("hf_hub_download(", "_ai_runtime_no_remote_model(")
    if text != original:
        write_text(path, text)
        return [f"patched {path.name}"]
    return []


def patch_commons(path: Path) -> list[str]:
    if not path.exists():
        return []
    text = read_text(path)
    original = text
    backup(path)
    text = text.replace("from huggingface_hub import hf_hub_download\n\n", "")
    text = re.sub(
        r'path\s*=\s*hf_hub_download\(repo_id="Plachta/JDCnet",\s*filename="bst\.t7"\)',
        'raise FileNotFoundError("Local F0 model missing: " + str(path))',
        text,
    )
    if text != original:
        write_text(path, text)
        return ["patched maskgct facodec commons.py"]
    return []


def main() -> int:
    ap = argparse.ArgumentParser()
    ap.add_argument("--repo", required=True)
    args = ap.parse_args()
    repo = Path(args.repo).resolve()
    indextts = repo / "indextts"
    files = [
        indextts / "infer_v2.py",
        indextts / "utils" / "maskgct_utils.py",
        indextts / "s2mel" / "hf_utils.py",
        indextts / "BigVGAN" / "bigvgan.py",
        indextts / "s2mel" / "modules" / "bigvgan" / "bigvgan.py",
        indextts / "utils" / "maskgct" / "models" / "codec" / "facodec" / "modules" / "commons.py",
    ]
    missing = [str(p) for p in files[:2] if not p.exists()]
    if missing:
        raise FileNotFoundError("Missing required IndexTTS2 source files: " + "; ".join(missing))
    changes: list[str] = []
    changes += patch_infer_v2(files[0])
    changes += patch_maskgct_utils(files[1])
    changes += patch_hf_utils(files[2])
    changes += patch_bigvgan(files[3])
    changes += patch_bigvgan(files[4])
    changes += patch_commons(files[5])
    if not changes:
        print("already patched", flush=True)
    else:
        for change in changes:
            print(change, flush=True)
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
