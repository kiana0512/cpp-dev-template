param(
    [string]$SkeletonTree = "",
    [string]$Out = "configs/skeleton_semantic_map_yyb_miku.json"
)

$ErrorActionPreference = "Stop"
$ScriptDir = Split-Path -Parent $MyInvocation.MyCommand.Path
$RepoRoot = Resolve-Path (Join-Path $ScriptDir "..")
Set-Location $RepoRoot

if ([string]::IsNullOrWhiteSpace($SkeletonTree)) {
    $candidate = Get-ChildItem -Path "configs" -Filter "skeleton_tree*.json" -File -ErrorAction SilentlyContinue | Select-Object -First 1
    if (-not $candidate) {
        throw "No configs/skeleton_tree*.json file found."
    }
    $SkeletonTree = $candidate.FullName
}

if (-not (Test-Path -LiteralPath $SkeletonTree)) {
    throw "Skeleton tree file not found: $SkeletonTree"
}

& python -m ai_runtime.skeleton.skeleton_semantic_analyzer --skeleton-tree $SkeletonTree --out $Out
if ($LASTEXITCODE -ne 0) {
    exit $LASTEXITCODE
}

Write-Host ""
Write-Host "Skeleton semantic map written to: $Out"
Write-Host "Reference only: this script does not modify UE and does not drive bones."
