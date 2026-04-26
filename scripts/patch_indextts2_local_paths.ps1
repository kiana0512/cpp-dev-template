param(
  [string]$IndexTtsRepo = "third_party\index-tts"
)

$ErrorActionPreference = "Stop"
$scriptDir = Split-Path -Parent $MyInvocation.MyCommand.Path
$repoRoot = Resolve-Path (Join-Path $scriptDir "..")
Set-Location $repoRoot

$repoPath = Resolve-Path -LiteralPath $IndexTtsRepo -ErrorAction SilentlyContinue
if (-not $repoPath) {
  throw "IndexTTS repo not found: $IndexTtsRepo"
}

$python = Join-Path $repoRoot "third_party\index-tts\.venv\Scripts\python.exe"
if (-not (Test-Path -LiteralPath $python)) {
  $python = "python"
}

Write-Host "Project root: $repoRoot"
Write-Host "IndexTTS repo: $repoPath"
Write-Host "Python: $python"
Write-Host "Patching local-only IndexTTS2 paths..."

& $python -u (Join-Path $repoRoot "ai_runtime\tts\patch_indextts2_local_paths.py") --repo $repoPath
if ($LASTEXITCODE -ne 0) {
  throw "Patch failed with exit code $LASTEXITCODE"
}

Write-Host ""
Write-Host "Remote reference scan after patch:"
$matches = Get-ChildItem -LiteralPath (Join-Path $repoPath "indextts") -Recurse -Filter *.py |
  Select-String -Pattern "facebook/w2v-bert-2.0|amphion/MaskGCT|hf_hub_download|snapshot_download|cas-bridge.xethub.hf.co" |
  Where-Object { $_.Line.TrimStart() -notlike "#*" }

if ($matches) {
  $matches | ForEach-Object { Write-Warning ("{0}:{1}: {2}" -f $_.Path, $_.LineNumber, $_.Line.Trim()) }
  throw "Remote references still found. Inspect the warnings above."
}

Write-Host "Remote refs OK."
