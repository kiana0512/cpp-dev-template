# UE5 Virtual Human Bridge

UE5 virtual human bridge，当前通过 TCP JSON line 驱动 Morph Target，并接入 IndexTTS2 生成语音和 morph-only 表情时间线。

## 当前能力

- Manual sender：手动发送单帧 `expression_frame`。
- Presets sender：从 `configs/presets.json` 发送预设帧。
- AI Runtime dryrun：无模型生成测试 wav 和 `frames.jsonl`。
- Existing wav：跳过 TTS，用已有 wav 生成 morph-only timeline。
- Real IndexTTS2：推荐 raw backend 为 `indextts2_local_cli`。
- TCP playback to UE：通过 `127.0.0.1:7001` 播放 JSON line 到 VHReceiver。

## 当前限制

- Windows 下 `wetext/kaldifst` 仍可能 `_kaldifst DLL load failed`，默认 `Text Normalizer=fallback` 只是 demo 跑通方案，不等价完整文本规范化。
- 当前输出为 `meta.morph_only=true`、`meta.skeleton_motion=false`，不做骨骼动画。
- WebSocket 未接入，当前只保留 TCP JSON line 协议。

## 快速启动 GUI

```powershell
cd D:\RT\ue5-virtual-human-bridge
dotnet run --project .\gui_dotnet\VhSenderGui.csproj -c Debug
```

构建 GUI：

```powershell
dotnet build .\gui_dotnet\VhSenderGui.csproj -c Release
```

## 固定依赖

sentencepiece 必须固定为 `0.1.99`：

```powershell
uv pip install "sentencepiece==0.1.99" --python .\third_party\index-tts\.venv\Scripts\python.exe
```

测试 `bpe.model`：

```powershell
.\third_party\index-tts\.venv\Scripts\python.exe -X faulthandler -c "import sentencepiece as spm; p=r'D:\RT\ue5-virtual-human-bridge\models\IndexTTS-2\bpe.model'; sp=spm.SentencePieceProcessor(); print(sp.Load(p), sp.GetPieceSize())"
```

期望输出：`True 12000`。`sentencepiece==0.2.1` 在当前 Windows/Anaconda-based uv venv 下加载 `bpe.model` 会 native crash。

## AI Runtime 命令

Dryrun：

```powershell
.\scripts\run_ai_morph_tts.ps1 -Text "你好，我是虚拟人助手" -Emotion happy -DryRun -VerboseLog
```

Existing wav：

```powershell
.\scripts\run_ai_morph_tts.ps1 -SkipTts -Wav .\wav\鹿乃-心做し.wav -Text "你好，我是虚拟人助手" -Emotion happy -VerboseLog
```

Real IndexTTS2：

```powershell
.\scripts\run_ai_morph_tts.ps1 `
  -Text "你好，我是初音未来虚拟人助手，很高兴见到你。" `
  -Emotion happy `
  -TtsBackend indextts2_local_cli `
  -PythonExe "D:\RT\ue5-virtual-human-bridge\third_party\index-tts\.venv\Scripts\python.exe" `
  -IndexTtsRepo "D:\RT\ue5-virtual-human-bridge\third_party\index-tts" `
  -IndexTtsModelDir "D:\RT\ue5-virtual-human-bridge\models\IndexTTS-2" `
  -IndexTtsConfig "D:\RT\ue5-virtual-human-bridge\models\IndexTTS-2\config.yaml" `
  -DefaultSpeakerWav "D:\RT\ue5-virtual-human-bridge\wav\鹿乃-心做し.wav" `
  -EmotionPrompt "开心、自然、语速适中" `
  -VerboseLog
```

播放到 UE：

```powershell
.\scripts\play_ai_morph_frames_tcp.ps1 `
  -Frames .\output\ai_sessions\gui_latest\frames.jsonl `
  -Host 127.0.0.1 `
  -Port 7001 `
  -UseTimestamp
```

## 目录结构

```text
configs/                  presets、sample_frame、YYB Miku morph/skeleton 参考配置
ai_runtime/               TTS、音频分析、morph-only timeline 生成
gui_dotnet/               .NET 8 WPF GUI
scripts/                  GUI、AI runtime、播放、模型环境脚本
docs/                     GUI、AI runtime、UE 集成和 Windows 排障文档
include/ src/ tests/      C++ sender/protocol/test 代码
third_party/index-tts/    已跟踪的 IndexTTS2 本地 patch 源码
models/                   本地模型权重，忽略，不提交
output/                   运行输出，忽略，不提交
```

更多说明见 `docs/gui_dotnet.md`、`docs/ai_morph_runtime.md`、`docs/ue_integration.md` 和 `docs/troubleshooting_indextts2_windows.md`。
