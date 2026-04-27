# IndexTTS2 Windows 排障

## repo / model_dir / config

- `IndexTTS repo` 是源码目录：`third_party/index-tts`，用于 import `indextts.infer_v2`。
- `model_dir` 是权重目录：`models/IndexTTS-2`。
- `config` 是 `models/IndexTTS-2/config.yaml`。
- 真实 TTS 推荐 Python：`third_party/index-tts/.venv/Scripts/python.exe`。

## 模型依赖

主模型：

```powershell
modelscope download --model IndexTeam/IndexTTS-2 --local_dir .\models\IndexTTS-2
```

本地依赖包括 `w2v-bert-2.0`、`MaskGCT`、`campplus`、`BigVGAN`。可运行：

```powershell
.\scripts\download_indextts2_local_deps.ps1
```

## BigVGAN 80-band

IndexTTS2 当前 s2mel 输出 80-channel mel，BigVGAN 必须是 80-band。100-band BigVGAN 会报：

```text
expected input to have 100 channels, but got 80 channels instead
```

验证：

```powershell
(Get-Content .\models\IndexTTS-2\BigVGAN\config.json -Raw | ConvertFrom-Json).num_mels
```

期望：`80`。

## sentencepiece 固定

当前环境必须使用：

```powershell
uv pip install "sentencepiece==0.1.99" --python .\third_party\index-tts\.venv\Scripts\python.exe
```

`sentencepiece==0.2.1` 在当前 Windows/Anaconda-based uv venv 下加载 `bpe.model` 会 0xC0000005 native crash。

验证：

```powershell
.\third_party\index-tts\.venv\Scripts\python.exe -X faulthandler -c "import sentencepiece as spm; p=r'D:\RT\ue5-virtual-human-bridge\models\IndexTTS-2\bpe.model'; sp=spm.SentencePieceProcessor(); print(sp.Load(p), sp.GetPieceSize())"
```

期望：`True 12000`。

## wetext / kaldifst

Windows 下仍可能出现：

```text
DLL load failed while importing _kaldifst
```

当前默认 `Text Normalizer=fallback`，用于跑通 demo。fallback 不等价完整文本规范化，也不是 wetext/kaldifst 的最终修复。后续可在 clean CPython 环境继续修 DLL 依赖。

## Anaconda-based venv 风险

如果 uv venv 基于 Anaconda Python，native DLL 扩展更容易出现 ABI/DLL 搜索路径问题。`sentencepiece 0.2.1` 和 `kaldifst` 都可能在这种环境中表现为 native crash 或 DLL load failed。

## 清理 GUI bin/obj

```powershell
.\scripts\clean_gui.ps1
```

等价于删除 `gui_dotnet/bin` 和 `gui_dotnet/obj`。

## 查看失败 manifest

失败时查看：

```powershell
Get-Content .\output\ai_sessions\gui_latest\manifest_failed.json -Raw
```

重点看 `failed_stage`、`error_message`、`selected_backend`、`text_normalizer`、`sentencepiece_version`、`model_dir`、`config`、`speaker_wav` 和 `diagnostics`。