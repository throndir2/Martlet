<#
.SYNOPSIS
Runs voicebench (Martlet's voice benchmarks) with its own Python environment. Arguments go straight to voicebench.

.EXAMPLE
scripts\voice-bench\Invoke-VoiceBench.ps1 clips fetch
scripts\voice-bench\Invoke-VoiceBench.ps1 stt --engines parakeet-v3 fw-large-v3-turbo
scripts\voice-bench\Invoke-VoiceBench.ps1 pipeline --think ollama:gemma4:e4b --stt parakeet-v3
#>
$ErrorActionPreference = 'Stop'
$home_ = if ($env:MARTLET_BENCH_HOME) { $env:MARTLET_BENCH_HOME } else { Join-Path $env:LOCALAPPDATA 'MartletBench' }
$python = Join-Path $home_ 'venv\Scripts\python.exe'
if (-not (Test-Path $python)) { throw "voicebench isn't installed: run $PSScriptRoot\Install-VoiceBench.ps1 first." }
$env:PYTHONIOENCODING = 'utf-8'
$env:HF_HUB_DISABLE_SYMLINKS_WARNING = '1'
# Keep Hugging Face downloads (Whisper, GGUF models) in the bench folder, so deleting it removes them too.
if (-not $env:HF_HOME) { $env:HF_HOME = Join-Path $home_ 'hf' }
Push-Location $PSScriptRoot
try {
    & $python -m voicebench @args
    exit $LASTEXITCODE
}
finally {
    Pop-Location
}
