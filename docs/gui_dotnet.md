# WPF GUI 使用说明

## 启动

```powershell
cd D:\RT\ue5-virtual-human-bridge
dotnet run --project .\gui_dotnet\VhSenderGui.csproj -c Debug
```

Release 构建：

```powershell
dotnet build .\gui_dotnet\VhSenderGui.csproj -c Release
```

## 区域说明

`Manual Sender` 用于手动发单帧或预设帧到 UE。保持 `Host=127.0.0.1`、`Port=7001`，UE 端 VHReceiver 监听后点击 `Connect`，再发送 preset 或当前表单。

`AI Runtime` 用于文本/音频到 morph-only timeline：

- Basic：`Text`、`Emotion`、`Backend`、`Speaker WAV`、`Existing WAV`、`Emotion Prompt` 和常用按钮。
- Advanced：`Python path`、`IndexTTS repo`、`Model dir`、`Config`、`raw backend`、`Text Normalizer`、`Offline local model only`。
- AI Status：`Status`、`Stage`、`Progress`、`Elapsed`、`Last wav`、`Frames`、`Manifest`、`Backend`、`Setup`、`Python`、`Text normalizer`、`Last error`。

## 推荐配置

- `Backend = real IndexTTS2`
- `Raw backend = indextts2_local_cli`
- `Text normalizer = fallback`
- `Python = D:\RT\ue5-virtual-human-bridge\third_party\index-tts\.venv\Scripts\python.exe`
- `IndexTTS repo = D:\RT\ue5-virtual-human-bridge\third_party\index-tts`
- `Model dir = D:\RT\ue5-virtual-human-bridge\models\IndexTTS-2`
- `Config = D:\RT\ue5-virtual-human-bridge\models\IndexTTS-2\config.yaml`

不要把 `auto` 当默认 backend。`indextts2_local_api` 和 `indextts_legacy` 只作为高级排障入口，不是当前稳定 GUI 路径。

## Check Setup

点击 `Check Setup` 会检查 Python、IndexTTS repo、模型目录、`config.yaml`、主模型、`w2v-bert-2.0`、`MaskGCT`、`campplus`、80-band `BigVGAN`、Speaker WAV，以及当前 `Text Normalizer`。

`Text Normalizer=fallback` 时会跳过 `wetext/kaldifst` import 检查。Windows 下 `_kaldifst DLL load failed` 尚未彻底解决，fallback 只是当前 demo 运行方案。

## Generate

`Generate` 会调用 `ai_runtime/app.py`，输出到 `output/ai_sessions/gui_latest/`：

- `generated.wav`
- `frames.jsonl`
- `manifest.json`
- 失败时写 `manifest_failed.json`

当前生成的帧仍是 morph-only：`meta.morph_only=true`、`meta.skeleton_motion=false`。

## Generate + Play

`Generate + Play` 先生成 `frames.jsonl`，再通过 GUI 当前 TCP 连接发送到 UE。UE 默认端口为 `7001`。

`Play Last` 会播放最近一次生成的 `frames.jsonl`。`Stop` 会停止生成或播放，并尝试结束 Python 子进程树。

## 常见错误

- Python 找不到：选择 `third_party\index-tts\.venv\Scripts\python.exe`。
- `indextts.infer_v2` import 失败：检查 `IndexTTS repo` 和 uv 环境。
- `sentencepiece` native crash：固定 `sentencepiece==0.1.99`，不要使用 `0.2.1`。
- `expected input to have 100 channels, but got 80`：BigVGAN 用错了 100-band 版本，必须换成 80-band。
- `_kaldifst DLL load failed`：保持 `Text Normalizer=fallback`，后续再修 clean CPython + WeText/KaldiFST。
- `generated.wav` 缺失：看 `AI Process Output` 和 `manifest_failed.json`。
- UE 无表情：检查 VHReceiver `TargetMesh`、Morph Name Overrides、`Clear Morphs Each Frame` 和端口。