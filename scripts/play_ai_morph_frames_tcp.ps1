param(
    [Parameter(Mandatory = $true)]
    [string]$Frames,
    [Alias("Host")]
    [string]$HostName = "127.0.0.1",
    [int]$Port = 7001,
    [double]$Fps = 30,
    [switch]$UseTimestamp
)

$ErrorActionPreference = "Stop"
$ScriptDir = Split-Path -Parent $MyInvocation.MyCommand.Path
$RepoRoot = Resolve-Path (Join-Path $ScriptDir "..")
Set-Location $RepoRoot

if (-not (Test-Path -LiteralPath $Frames)) {
    throw "Frames file not found: $Frames"
}

$argsList = @(
    "-m", "ai_runtime.playback.tcp_jsonl_player",
    "--frames", $Frames,
    "--host", $HostName,
    "--port", "$Port",
    "--fps", "$Fps"
)
if ($UseTimestamp) {
    $argsList += "--use-timestamp"
}

& python @argsList
if ($LASTEXITCODE -ne 0) {
    Write-Host "[AI][ERROR] Playback failed. Check UE is running, ExpressionReceiverComponent StartListening is active, Listen Port is 7001, and Windows firewall allows the connection."
}
exit $LASTEXITCODE
