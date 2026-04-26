param(
    [ValidateSet("stdout", "file", "tcp")]
    [string]$Mode = "stdout",

    [int]$Fps = 24,
    [int]$HoldSec = 3,

    [string]$TargetHost = "127.0.0.1",
    [int]$TargetPort = 7001,

    [string]$OutputFile = "outputs/frames.jsonl"
)

. "$PSScriptRoot\_env.ps1"

if ($Fps -le 0) {
    throw "Fps must be > 0"
}
if ($HoldSec -le 0) {
    throw "HoldSec must be > 0"
}

$count = $Fps * $HoldSec

$args = @(
    "--config", "configs/sample_frame.json",
    "--count",  "$count",
    "--fps",    "$Fps",
    "--mode",   "$Mode"
)

if ($Mode -eq "tcp") {
    $args += @("--host", "$TargetHost", "--port", "$TargetPort")
}
elseif ($Mode -eq "file") {
    $args += @("--output", "$OutputFile")
}

& $ExePath @args

if ($LASTEXITCODE -ne 0) {
    throw "vh_demo_sender failed, exit code: $LASTEXITCODE"
}

Write-Host "Done. sample_frame.json completed." -ForegroundColor Green