param(
  [string]$ModelDir = "models\IndexTTS-2"
)

$ErrorActionPreference = "Stop"
$scriptDir = Split-Path -Parent $MyInvocation.MyCommand.Path
$repoRoot = Resolve-Path (Join-Path $scriptDir "..")
Set-Location $repoRoot

$modelPath = Join-Path $repoRoot $ModelDir
New-Item -ItemType Directory -Force -Path $modelPath | Out-Null

Write-Host "Project root: $repoRoot"
Write-Host "Model dir: $modelPath"

$modelscope = Get-Command modelscope -ErrorAction SilentlyContinue
if (-not $modelscope) {
  Write-Host "modelscope command not found. Installing with current python..."
  python -m pip install -U modelscope
}

if (-not (Test-Path -LiteralPath (Join-Path $modelPath "config.yaml"))) {
  Write-Host "Downloading IndexTTS-2 main model..."
  modelscope download --model IndexTeam/IndexTTS-2 --local_dir $modelPath
}
else {
  Write-Host "Main model config exists; skipping main model download."
}

Write-Host "Downloading w2v-bert-2.0 local dependency..."
modelscope download --model AI-ModelScope/w2v-bert-2.0 --local_dir (Join-Path $modelPath "w2v-bert-2.0")

Write-Host "Downloading MaskGCT local dependency..."
modelscope download --model amphion/MaskGCT --local_dir (Join-Path $modelPath "MaskGCT")

$checks = @(
  "config.yaml",
  "w2v-bert-2.0\preprocessor_config.json",
  "MaskGCT\semantic_codec\model.safetensors",
  "MaskGCT\t2s_model\model.safetensors",
  "MaskGCT\s2a_model\s2a_model_1layer\model.safetensors",
  "MaskGCT\s2a_model\s2a_model_full\model.safetensors"
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

if ($missing.Count -gt 0) {
  throw "Some local dependencies are missing. See warnings above."
}

Write-Host "All requested local dependencies are present."
