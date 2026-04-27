param(
  [string]$ModelDir = "models\IndexTTS-2",
  [string]$BigVganModel = "nvidia/bigvgan_v2_22khz_80band_256x"
)

$ErrorActionPreference = "Stop"
$scriptDir = Split-Path -Parent $MyInvocation.MyCommand.Path
$repoRoot = Resolve-Path (Join-Path $scriptDir "..")
Set-Location $repoRoot

$modelPath = Join-Path $repoRoot $ModelDir
$bigVganPath = Join-Path $modelPath "BigVGAN"
New-Item -ItemType Directory -Force -Path $modelPath | Out-Null

Write-Host "Project root: $repoRoot"
Write-Host "Model dir: $modelPath"
Write-Host "BigVGAN: $BigVganModel"
Write-Host "Important: IndexTTS2 s2mel outputs 80-channel mel. Do not use 100-band BigVGAN."

$modelscope = Get-Command modelscope -ErrorAction SilentlyContinue
if (-not $modelscope) {
  Write-Host "modelscope command not found. Installing with current python..."
  python -m pip install -U modelscope
}

function Ensure-ModelScopeModel([string]$Name, [string]$LocalDir, [string]$ProbeFile) {
  if (Test-Path -LiteralPath (Join-Path $LocalDir $ProbeFile)) {
    Write-Host "OK      $Name exists; skipping download."
    return
  }
  Write-Host "Downloading $Name..."
  modelscope download --model $Name --local_dir $LocalDir
}

Ensure-ModelScopeModel "IndexTeam/IndexTTS-2" $modelPath "config.yaml"
Ensure-ModelScopeModel "AI-ModelScope/w2v-bert-2.0" (Join-Path $modelPath "w2v-bert-2.0") "preprocessor_config.json"
Ensure-ModelScopeModel "amphion/MaskGCT" (Join-Path $modelPath "MaskGCT") "semantic_codec\model.safetensors"
Ensure-ModelScopeModel $BigVganModel $bigVganPath "config.json"

$checks = @(
  "config.yaml",
  "gpt.pth",
  "s2mel.pth",
  "bpe.model",
  "wav2vec2bert_stats.pt",
  "qwen0.6bemo4-merge\config.json",
  "w2v-bert-2.0\preprocessor_config.json",
  "MaskGCT\semantic_codec\model.safetensors",
  "MaskGCT\t2s_model\model.safetensors",
  "MaskGCT\s2a_model\s2a_model_1layer\model.safetensors",
  "MaskGCT\s2a_model\s2a_model_full\model.safetensors",
  "BigVGAN\config.json"
)

Write-Host ""
Write-Host "Local dependency check:"
$missing = @()
foreach ($rel in $checks) {
  $p = Join-Path $modelPath $rel
  if (Test-Path -LiteralPath $p) {
    Write-Host "OK      $rel"
  }
  else {
    Write-Warning "Missing $rel"
    $missing += $rel
  }
}

$bigVganConfig = Join-Path $bigVganPath "config.json"
if (Test-Path -LiteralPath $bigVganConfig) {
  $cfg = Get-Content -Raw -LiteralPath $bigVganConfig | ConvertFrom-Json
  $numMels = $cfg.num_mels
  Write-Host "BigVGAN num_mels: $numMels"
  if ($numMels -ne 80) {
    throw "BigVGAN must be 80-band. Current num_mels=$numMels would cause 100-channel/80-channel mismatch."
  }
}

if ($missing.Count -gt 0) {
  throw "Some local dependencies are missing. See warnings above."
}

Write-Host "All requested local dependencies are present."
