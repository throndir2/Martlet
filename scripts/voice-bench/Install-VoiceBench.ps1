<#
.SYNOPSIS
Installs voicebench (Martlet's voice benchmarks) into its own folder, outside the repository and Martlet's data.

.DESCRIPTION
Creates a Python 3.12 virtual environment in $env:MARTLET_BENCH_HOME (default %LOCALAPPDATA%\MartletBench) with the
speech-to-text engines (sherpa-onnx, faster-whisper) and, with -Torch, PyTorch + transformers for Whisper large. -LlamaCpp
downloads a pinned llama.cpp CUDA build for the models that hear (Qwen2.5-Omni, Qwen3-Omni, Voxtral). Nothing is installed
system-wide; delete the folder to remove it all. Models download on first use (or with: voicebench models --fetch NAME).

.EXAMPLE
scripts\voice-bench\Install-VoiceBench.ps1 -Torch -LlamaCpp
#>
[CmdletBinding()]
param(
    [switch]$Torch,
    [switch]$LlamaCpp,
    [string]$LlamaCppBuild = 'b11379',
    # PyTorch runners for models llama.cpp can't give audio: phi-4-multimodal, minicpm-o-2.6 (each its own environment).
    [ValidateSet('phi-4-multimodal', 'minicpm-o-2.6')]
    [string[]]$HfModels = @()
)
$ErrorActionPreference = 'Stop'
$home_ = if ($env:MARTLET_BENCH_HOME) { $env:MARTLET_BENCH_HOME } else { Join-Path $env:LOCALAPPDATA 'MartletBench' }
$venv = Join-Path $home_ 'venv'
$python = Join-Path $venv 'Scripts\python.exe'
New-Item -ItemType Directory -Force $home_ | Out-Null

if (-not (Test-Path $python)) {
    Write-Host "Creating the bench environment in $venv"
    & py -3.12 -m venv $venv
    if ($LASTEXITCODE) { throw 'Python 3.12 is needed (py -3.12). Install it from python.org first.' }
}
& $python -m pip install --upgrade pip --quiet
& $python -m pip install -r (Join-Path $PSScriptRoot 'requirements.txt')
if ($LASTEXITCODE) { throw 'Installing the core packages failed.' }

if ($Torch) {
    & $python -m pip install torch --index-url https://download.pytorch.org/whl/cu128
    & $python -m pip install transformers accelerate
    if ($LASTEXITCODE) { throw 'Installing PyTorch failed.' }
}

if ($LlamaCpp) {
    $tools = Join-Path $home_ 'tools'
    $target = Join-Path $tools "llama.cpp-$LlamaCppBuild"
    if (-not (Test-Path (Join-Path $target 'llama-server.exe'))) {
        New-Item -ItemType Directory -Force $target | Out-Null
        $base = "https://github.com/ggml-org/llama.cpp/releases/download/$LlamaCppBuild"
        foreach ($name in "llama-$LlamaCppBuild-bin-win-cuda-12.4-x64.zip", 'cudart-llama-bin-win-cuda-12.4-x64.zip') {
            $zip = Join-Path $tools $name
            Write-Host "Downloading $name"
            curl.exe -fsSL -o $zip "$base/$name"
            if ($LASTEXITCODE) { throw "Downloading $name failed." }
            Expand-Archive -Force $zip $target
            Remove-Item $zip
        }
    }
    Write-Host "llama.cpp: $target"
}

$venvNames = @{ 'phi-4-multimodal' = 'venv-phi4mm'; 'minicpm-o-2.6' = 'venv-minicpmo' }
foreach ($model in $HfModels) {
    $modelVenv = Join-Path $home_ $venvNames[$model]
    $modelPython = Join-Path $modelVenv 'Scripts\python.exe'
    if (-not (Test-Path $modelPython)) {
        Write-Host "Creating the $model environment in $modelVenv"
        & py -3.12 -m venv $modelVenv
    }
    & $modelPython -m pip install --upgrade pip --quiet
    # torch first from PyTorch's CUDA index (pip reuses the wheel cached for the main environment), then the model's pins.
    & $modelPython -m pip install torch torchaudio torchvision --index-url https://download.pytorch.org/whl/cu128
    & $modelPython -m pip install -r (Join-Path $PSScriptRoot "requirements-$model.txt")
    if ($LASTEXITCODE) { throw "Installing $model's packages failed." }
    Write-Host "${model}: $modelVenv (the weights download on its first run)"
}

& $python -c "import sys; sys.path.insert(0, r'$PSScriptRoot'); from voicebench.__main__ import main; main(['env'])"
