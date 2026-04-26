param(
    [string]$TargetHost      = "127.0.0.1",
    [int]$TargetPort         = 7001,
    [int]$Fps                = 24,
    [int]$HoldSec            = 3,
    [int]$PresetGapMs        = 100,
    [int]$GapMs              = 500,
    [bool]$SingleConnection  = $true,
    [int]$MaxRetries         = 2,
    [int]$RetryDelayMs       = 600,
    [string]$CharacterId    = "miku_yyb_001",
    [UInt64]$StartSeq        = 1
)

& "$PSScriptRoot\run_presets.ps1" `
    -Mode              tcp `
    -TargetHost        $TargetHost `
    -TargetPort        $TargetPort `
    -Fps               $Fps `
    -HoldSec           $HoldSec `
    -PresetGapMs       $PresetGapMs `
    -GapMs             $GapMs `
    -SingleConnection  $SingleConnection `
    -MaxRetries         $MaxRetries `
    -RetryDelayMs       $RetryDelayMs `
    -CharacterId       $CharacterId `
    -StartSeq           $StartSeq
