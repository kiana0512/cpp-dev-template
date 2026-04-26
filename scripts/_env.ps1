$ErrorActionPreference = "Stop"
[Console]::OutputEncoding = [System.Text.Encoding]::UTF8

$script:ProjectRoot = Split-Path -Parent $PSScriptRoot
$script:ExePath = Join-Path $ProjectRoot "cmake-build-debug\vh_demo_sender.exe"

if (-not (Test-Path $ExePath)) {
    throw "Executable not found: $ExePath"
}

$mingwBin = $null
$jetbrainsRoots = @(
    "C:\Program Files\JetBrains",
    "C:\Program Files (x86)\JetBrains"
)

foreach ($root in $jetbrainsRoots) {
    if (-not (Test-Path $root)) {
        continue
    }

    $clionDirs = Get-ChildItem $root -Directory -ErrorAction SilentlyContinue |
            Where-Object { $_.Name -like "CLion*" } |
            Sort-Object Name -Descending

    foreach ($dir in $clionDirs) {
        $candidate = Join-Path $dir.FullName "bin\mingw\bin"
        if (Test-Path $candidate) {
            $mingwBin = $candidate
            break
        }
    }

    if ($mingwBin) {
        break
    }
}

if ($mingwBin) {
    $env:Path = "$mingwBin;$env:Path"
    Write-Host "Using MinGW DLL path: $mingwBin" -ForegroundColor DarkCyan
} else {
    Write-Host "Warning: CLion bundled MinGW bin not found automatically." -ForegroundColor Yellow
}

if ($env:CONDA_PREFIX) {
    Write-Host "Warning: conda env detected: $($env:CONDA_PREFIX)" -ForegroundColor Yellow
    Write-Host "If you still see 0xC0000135 / 0xC000007B, run 'conda deactivate' first." -ForegroundColor Yellow
}

Set-Location $ProjectRoot