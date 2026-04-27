$ErrorActionPreference = "Stop"
$scriptDir = Split-Path -Parent $MyInvocation.MyCommand.Path
$repoRoot = Resolve-Path (Join-Path $scriptDir "..")
Set-Location $repoRoot

Remove-Item -Recurse -Force .\gui_dotnet\bin, .\gui_dotnet\obj -ErrorAction SilentlyContinue
Write-Host "Cleaned gui_dotnet/bin and gui_dotnet/obj."
