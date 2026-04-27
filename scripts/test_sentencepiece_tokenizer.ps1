param(
    [string]$PythonExe = ".\third_party\index-tts\.venv\Scripts\python.exe",
    [string]$BpeModel = ".\models\IndexTTS-2\bpe.model"
)

$ErrorActionPreference = "Stop"
$scriptDir = Split-Path -Parent $MyInvocation.MyCommand.Path
$repoRoot = Resolve-Path (Join-Path $scriptDir "..")
Set-Location $repoRoot

& $PythonExe -X faulthandler -c "import sentencepiece as spm; p=r'$((Resolve-Path $BpeModel).Path)'; sp=spm.SentencePieceProcessor(); print(sp.Load(p), sp.GetPieceSize())"
exit $LASTEXITCODE
