param(
    [string]$Text = "hello virtual human assistant",
    [ValidateSet("neutral", "happy", "sad", "angry", "surprised")]
    [string]$Emotion = "happy",
    [string]$SpeakerWav = "",
    [string]$EmotionPrompt = "",
    [string]$OutDir = "",
    [double]$Fps = 30,
    [string]$CharacterId = "yyb_miku",
    [ValidateSet("auto", "indextts2", "indextts2_local_cli", "indextts2_local_api", "indextts_legacy", "dryrun")]
    [string]$TtsBackend = "indextts2_local_cli",
    [ValidateSet("auto", "fallback", "wetext")]
    [string]$TextNormalizer = "fallback",
    [switch]$DryRun,
    [switch]$SkipTts,
    [string]$Wav = "",
    [string]$MorphMap = "configs/blendshape_map_yyb_miku.json",
    [string]$SkeletonTree = "",
    [string]$Config = "configs/ai_runtime.example.json",
    [string]$PythonExe = "",
    [string]$IndexTtsRepo = "third_party/index-tts",
    [string]$IndexTtsModelDir = "models/IndexTTS-2",
    [string]$IndexTtsConfig = "models/IndexTTS-2/config.yaml",
    [string]$IndexTtsInferScript = "",
    [string]$DefaultSpeakerWav = "",
    [switch]$UseFp16,
    [switch]$UseCudaKernel,
    [switch]$UseDeepSpeed,
    [switch]$UseRandom,
    [switch]$FallbackToDryrun,
    [switch]$VerboseLog,
    [string]$LogJson = "",
    [switch]$CheckModelOnly,
    [switch]$CheckSetup
)

$ErrorActionPreference = "Stop"
$ScriptDir = Split-Path -Parent $MyInvocation.MyCommand.Path
$RepoRoot = Resolve-Path (Join-Path $ScriptDir "..")
Set-Location $RepoRoot

if ([string]::IsNullOrWhiteSpace($PythonExe)) {
    $venvPy = Join-Path $RepoRoot "third_party\index-tts\.venv\Scripts\python.exe"
    if (Test-Path -LiteralPath $venvPy) {
        $PythonExe = $venvPy
    } else {
        $PythonExe = "python"
    }
}

if ([string]::IsNullOrWhiteSpace($OutDir)) {
    $stamp = Get-Date -Format "yyyyMMdd_HHmmss"
    $OutDir = Join-Path "output/ai_sessions" "demo_$stamp"
}

if ([string]::IsNullOrWhiteSpace($SkeletonTree)) {
    $candidate = Get-ChildItem -Path "configs" -Filter "skeleton_tree*.json" -File -ErrorAction SilentlyContinue | Select-Object -First 1
    if ($candidate) {
        $SkeletonTree = $candidate.FullName
    }
}

Write-Host "[AI][INFO] RepoRoot          = $RepoRoot"
Write-Host "[AI][INFO] PythonExe         = $PythonExe"
Write-Host "[AI][INFO] TTS backend       = $TtsBackend"
Write-Host "[AI][INFO] Text normalizer   = $TextNormalizer"
Write-Host "[AI][INFO] Model dir         = $IndexTtsModelDir"
Write-Host "[AI][INFO] Config            = $IndexTtsConfig"
Write-Host "[AI][INFO] Config exists     = $(Test-Path -LiteralPath $IndexTtsConfig)"
Write-Host "[AI][INFO] IndexTTS repo     = $IndexTtsRepo"
Write-Host "[AI][INFO] Speaker WAV       = $DefaultSpeakerWav"
Write-Host "[AI][INFO] OutDir            = $OutDir"

if ($TtsBackend -ne "dryrun" -and -not $DefaultSpeakerWav -and -not $SpeakerWav -and -not $DryRun -and -not $SkipTts) {
    Write-Host "[AI][ERROR] Real IndexTTS2 requires -DefaultSpeakerWav or -SpeakerWav."
    exit 2
}
if ($SkipTts -and -not $Wav) {
    Write-Host "[AI][ERROR] -SkipTts requires -Wav."
    exit 2
}

if ($CheckSetup) {
    Write-Host "[AI][INFO] CheckSetup: model/config"
    if (-not (Test-Path -LiteralPath $IndexTtsModelDir)) { Write-Host "[AI][ERROR] Model dir missing: $IndexTtsModelDir" }
    if (-not (Test-Path -LiteralPath $IndexTtsConfig)) { Write-Host "[AI][ERROR] config.yaml missing: $IndexTtsConfig" }
    if ($IndexTtsRepo -and (Test-Path -LiteralPath $IndexTtsRepo)) {
        $env:PYTHONPATH = (Resolve-Path $IndexTtsRepo).Path + [IO.Path]::PathSeparator + $env:PYTHONPATH
        Write-Host "[AI][INFO] PYTHONPATH prepended with repo: $IndexTtsRepo"
    } elseif ($IndexTtsRepo) {
        Write-Host "[AI][WARN] Repo missing: $IndexTtsRepo"
    }
    if ($TtsBackend -ne "dryrun") {
        Write-Host "[AI][INFO] CheckSetup: python import indextts.infer_v2"
        & $PythonExe -c "import sys; print(sys.executable); from indextts.infer_v2 import IndexTTS2; print('IMPORT_OK')"
        if ($LASTEXITCODE -ne 0) {
            Write-Host "[AI][ERROR] IMPORT_FAILED. Install index-tts repo with pip install -e . or choose the correct Python."
            exit $LASTEXITCODE
        }
    }
    if (($TtsBackend -ne "dryrun") -and (-not $DefaultSpeakerWav) -and (-not $SpeakerWav)) {
        Write-Host "[AI][ERROR] Real IndexTTS2 requires Speaker WAV."
        exit 2
    }
    Write-Host "[AI][INFO] CheckSetup OK"
    exit 0
}

$argsList = @(
    "ai_runtime/app.py",
    "--config", $Config,
    "--text", $Text,
    "--emotion", $Emotion,
    "--out-dir", $OutDir,
    "--fps", "$Fps",
    "--character-id", $CharacterId,
    "--morph-map", $MorphMap,
    "--tts-backend", $TtsBackend,
    "--text-normalizer", $TextNormalizer,
    "--indextts-model-dir", $IndexTtsModelDir,
    "--indextts-config", $IndexTtsConfig
)

if ($SkeletonTree) { $argsList += @("--skeleton-tree", $SkeletonTree) }
if ($SpeakerWav) { $argsList += @("--speaker-wav", $SpeakerWav) }
if ($EmotionPrompt) { $argsList += @("--emotion-prompt", $EmotionPrompt) }
if ($DryRun) { $argsList += "--dry-run" }
if ($SkipTts) { $argsList += "--skip-tts" }
if ($Wav) { $argsList += @("--wav", $Wav) }
if ($IndexTtsRepo) { $argsList += @("--indextts-repo", $IndexTtsRepo) }
if ($IndexTtsInferScript) { $argsList += @("--indextts-infer-script", $IndexTtsInferScript) }
if ($DefaultSpeakerWav) { $argsList += @("--default-speaker-wav", $DefaultSpeakerWav) }
if ($UseFp16) { $argsList += "--use-fp16" }
if ($UseCudaKernel) { $argsList += "--use-cuda-kernel" }
if ($UseDeepSpeed) { $argsList += "--use-deepspeed" }
if ($UseRandom) { $argsList += "--use-random" }
if ($FallbackToDryrun) { $argsList += "--fallback-to-dryrun" }
if ($VerboseLog) { $argsList += @("--verbose", "--print-progress") }
if ($LogJson) { $argsList += @("--log-json", $LogJson) }
if ($CheckModelOnly) { $argsList += "--check-model-only" }

& $PythonExe @argsList
$exit = $LASTEXITCODE
if ($exit -ne 0) {
    Write-Host "[AI][ERROR] ai_runtime/app.py exited with $exit"
    if (-not $DryRun -and -not $CheckModelOnly) {
        Write-Host "[AI][WARN] Real TTS failed. First verify the chain with: .\scripts\run_ai_morph_tts.ps1 -Text `"hello`" -Emotion happy -DryRun"
    }
    exit $exit
}

if (-not $CheckModelOnly) {
    Write-Host ""
    Write-Host "Generated:"
    Write-Host "  wav      = $(Join-Path $OutDir 'generated.wav')"
    Write-Host "  frames   = $(Join-Path $OutDir 'frames.jsonl')"
    Write-Host "  manifest = $(Join-Path $OutDir 'manifest.json')"
    Write-Host ""
    Write-Host "Next:"
    Write-Host "  .\scripts\play_ai_morph_frames_tcp.ps1 -Frames $(Join-Path $OutDir 'frames.jsonl') -Host 127.0.0.1 -Port 7001 -UseTimestamp"
}
