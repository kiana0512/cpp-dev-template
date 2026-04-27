param(
    [string]$ProjectRoot = "",
    [string]$PythonVersion = "3.10"
)

$ErrorActionPreference = "Stop"

if ([string]::IsNullOrWhiteSpace($ProjectRoot)) {
    $ProjectRoot = (Resolve-Path (Join-Path $PSScriptRoot "..")).Path
}

$IndexRepo = Join-Path $ProjectRoot "third_party\index-tts"
$VenvDir = Join-Path $IndexRepo ".venv-cpython"
$PythonExe = Join-Path $VenvDir "Scripts\python.exe"

Write-Host "Available Python launchers:"
py -0p

$probe = & py "-$PythonVersion" -c "import sys; print(sys.executable); print(sys.version); print(sys.base_prefix)" 2>&1
if ($LASTEXITCODE -ne 0) {
    throw "Official CPython $PythonVersion was not found by py launcher. Install CPython $PythonVersion or rerun with -PythonVersion 3.11."
}
if (($probe -join "`n") -match "anaconda|miniconda|conda") {
    throw "py -$PythonVersion points to a conda-based Python. Install official CPython and retry."
}

Write-Host "Creating clean CPython venv: $VenvDir"
& py "-$PythonVersion" -m venv $VenvDir
if ($LASTEXITCODE -ne 0) {
    throw "Failed to create venv."
}

& $PythonExe -m pip install -U pip uv
if ($LASTEXITCODE -ne 0) {
    throw "Failed to install pip/uv in clean venv."
}

Push-Location $IndexRepo
try {
    $env:UV_PROJECT_ENVIRONMENT = ".venv-cpython"
    if (Test-Path -LiteralPath "pyproject.toml") {
        & $PythonExe -m uv pip install -e .
    } else {
        & $PythonExe -m uv pip install -r requirements.txt
    }
    if ($LASTEXITCODE -ne 0) {
        throw "Failed to install IndexTTS2 into clean CPython venv."
    }
}
finally {
    Pop-Location
    Remove-Item Env:\UV_PROJECT_ENVIRONMENT -ErrorAction SilentlyContinue
}

Write-Host "Clean CPython IndexTTS2 Python:"
Write-Host $PythonExe
