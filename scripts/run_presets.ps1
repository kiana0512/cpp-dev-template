param(
    [ValidateSet("stdout", "file", "tcp")]
    [string]$Mode = "stdout",

    [int]$Fps = 24,
    [int]$HoldSec = 3,

    [string]$TargetHost = "127.0.0.1",
    [int]$TargetPort = 7001,

    [string]$OutputFile = "outputs/frames.jsonl",

    [string]$CharacterId = "miku_yyb_001",

    [UInt64]$StartSeq = 1,

    # preset-json 单连接模式下：两个 preset 块之间的间隔（sender 内 sleep，不断 TCP）
    [int]$PresetGapMs = 100,

    # 旧版「每个 preset 单独进程」模式用的间隔（仅当 SingleConnection=$false）
    [int]$GapMs = 500,

    [int]$MaxRetries = 2,
    [int]$RetryDelayMs = 600,

    # $true（默认）：一次 TCP 连接用 --presets-json 发完全部 preset（推荐，避免多次 Accept/FIN 放大 UE 端问题）
    [bool]$SingleConnection = $true
)

. "$PSScriptRoot\_env.ps1"

if ($Fps -le 0) { throw "Fps must be > 0" }
if ($HoldSec -le 0) { throw "HoldSec must be > 0" }

$presetFile = Join-Path $ProjectRoot "configs\presets.json"
if (-not (Test-Path $presetFile)) {
    throw "Preset file not found: $presetFile"
}

$data    = Get-Content $presetFile -Raw -Encoding UTF8 | ConvertFrom-Json
$presets = $data.presets

if ($null -eq $presets -or $presets.Count -eq 0) {
    throw "No presets found in presets.json"
}

function Get-ExitCodeDescription([int]$Code) {
    switch ($Code) {
        0 { return "Success" }
        1 { return "Fatal exception in sender" }
        2 { return "Frame validation failed" }
        3 { return "Unknown transport mode" }
        4 { return "TCP connect failed (receiver not ready / nothing listening on port)" }
        5 { return "TCP send failed (receiver closed connection?)" }
        default { return "Unknown ($Code)" }
    }
}

Write-Host ""
Write-Host "Mode              : $Mode" -ForegroundColor Cyan
Write-Host "FPS               : $Fps" -ForegroundColor Cyan
Write-Host "HoldSec           : $HoldSec" -ForegroundColor Cyan
Write-Host "PresetCount       : $($presets.Count)" -ForegroundColor Cyan
Write-Host "SingleConnection  : $SingleConnection" -ForegroundColor Cyan
if ($Mode -eq "tcp") {
    Write-Host "Target            : ${TargetHost}:$TargetPort" -ForegroundColor Cyan
    if ($SingleConnection) {
        Write-Host "PresetGapMs       : $PresetGapMs (inside sender, TCP kept open)" -ForegroundColor Cyan
    } else {
        Write-Host "GapMs (multi-proc): $GapMs" -ForegroundColor Cyan
    }
}
Write-Host ""

# ---------------------------------------------------------------------------
# 推荐路径：单 TCP 连接 + --presets-json（源码：src/main.cpp runPresetsJsonFile）
# ---------------------------------------------------------------------------
if ($Mode -eq "tcp" -and $SingleConnection) {
    $senderArgs = @(
        "--presets-json", $presetFile,
        "--fps",          "$Fps",
        "--hold-sec",     "$HoldSec",
        "--preset-gap-ms", "$PresetGapMs",
        "--seq",          "$StartSeq",
        "--character",    "$CharacterId",
        "--mode",         "tcp",
        "--host",         "$TargetHost",
        "--port",         "$TargetPort"
    )

    Write-Host "Running SINGLE-CONNECTION sender (all presets, one connect):" -ForegroundColor Green
    Write-Host "  $ExePath $($senderArgs -join ' ')" -ForegroundColor DarkGray

    & $ExePath @senderArgs
    $exitCode = $LASTEXITCODE

    if ($exitCode -ne 0) {
        $desc = Get-ExitCodeDescription $exitCode
        Write-Host "[FAIL] exit=$exitCode ($desc)" -ForegroundColor Red
        if ($exitCode -eq 4) {
            Write-Host "  WSA 10061 = connection refused: UE 未在 ${TargetHost}:$TargetPort 监听。" -ForegroundColor Yellow
            Write-Host "  常见原因: PIE 已停、CloseAll 已关掉 ListenSocket、或端口被占用。" -ForegroundColor Yellow
            Write-Host "  查 UE 日志是否出现: CloseAll / EndPlay / StopListening / ListenSocket=NULL" -ForegroundColor Yellow
        }
        throw "vh_demo_sender failed (exit=$exitCode $desc)"
    }

    Write-Host "Done. Single-connection presets completed." -ForegroundColor Green
    return
}

# ---------------------------------------------------------------------------
# 兼容：每个 preset 单独进程（多连接）
# ---------------------------------------------------------------------------
function Get-HeadPoseValue {
    param($Preset, [string]$Name, [double]$DefaultValue = 0.0)
    if ($null -eq $Preset.head_pose) { return [string]$DefaultValue }
    $prop = $Preset.head_pose.PSObject.Properties[$Name]
    if ($null -eq $prop) { return [string]$DefaultValue }
    return [string]$prop.Value
}

$countPerPreset = $Fps * $HoldSec
$seq            = $StartSeq
$presetIndex    = 0

foreach ($p in $presets) {
    $presetIndex++
    $pitch = Get-HeadPoseValue -Preset $p -Name "pitch"
    $yaw   = Get-HeadPoseValue -Preset $p -Name "yaw"
    $roll  = Get-HeadPoseValue -Preset $p -Name "roll"

    Write-Host "==========================================" -ForegroundColor Yellow
    Write-Host "Preset [$presetIndex/$($presets.Count)]  : $($p.name)"
    Write-Host "SeqStart : $seq"
    Write-Host "==========================================" -ForegroundColor Yellow

    $senderArgs = @(
        "--text",       "$($p.text)",
        "--emotion",    "$($p.emotion)",
        "--phoneme",    "$($p.phoneme)",
        "--rms",        "$($p.rms)",
        "--confidence", "$($p.confidence)",
        "--character",  "$CharacterId",
        "--seq",        "$seq",
        "--count",      "$countPerPreset",
        "--fps",        "$Fps",
        "--pitch",      "$pitch",
        "--yaw",        "$yaw",
        "--roll",       "$roll",
        "--mode",       "$Mode"
    )

    if ($Mode -eq "tcp") {
        $senderArgs += @("--host", "$TargetHost", "--port", "$TargetPort")
    }
    elseif ($Mode -eq "file") {
        $senderArgs += @("--output", "$OutputFile")
    }

    $exitCode  = -1
    $succeeded = $false

    for ($retry = 0; $retry -le $MaxRetries; $retry++) {
        if ($retry -gt 0) {
            $delayMs = $RetryDelayMs * $retry
            Write-Host "  [Retry $retry/$MaxRetries] Waiting ${delayMs} ms..." -ForegroundColor DarkYellow
            Start-Sleep -Milliseconds $delayMs
        }

        & $ExePath @senderArgs
        $exitCode = $LASTEXITCODE

        if ($exitCode -eq 0) {
            $succeeded = $true
            break
        }

        Write-Host "  [WARN] Preset '$($p.name)' exit=$exitCode ($(Get-ExitCodeDescription $exitCode))" -ForegroundColor DarkYellow
        if ($exitCode -eq 3) { break }
    }

    if (-not $succeeded) {
        throw "vh_demo_sender failed at preset '$($p.name)' (exit=$exitCode)"
    }

    $seq += [UInt64]$countPerPreset

    if ($presetIndex -lt $presets.Count) {
        Write-Host "  [gap] $GapMs ms before next process..." -ForegroundColor DarkCyan
        Start-Sleep -Milliseconds $GapMs
    }
}

Write-Host ""
Write-Host "Done. All $($presets.Count) presets completed (multi-process mode)." -ForegroundColor Green
