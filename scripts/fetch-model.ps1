<#
.SYNOPSIS
    Downloads the monocular depth model used by Relight.

.DESCRIPTION
    The model is ~47 MB and is not committed to the repository. It is pulled from a pinned
    Hugging Face revision so every checkout gets byte-identical weights.
#>
[CmdletBinding()]
param(
    [string]$Revision = '4472b7362082ad9968fee890ca0f1e5aca36b93d'
)

$ErrorActionPreference = 'Stop'

$destination = [System.IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..\src\Relight\Assets\Models'))
$modelPath = Join-Path $destination 'depth-anything-v2-small-fp16.onnx'

if (Test-Path $modelPath) {
    Write-Host "Model already present: $modelPath"
    exit 0
}

New-Item -ItemType Directory -Force -Path $destination | Out-Null

$url = "https://huggingface.co/onnx-community/depth-anything-v2-small/resolve/$Revision/onnx/model_fp16.onnx"
Write-Host "Downloading depth model from $url"
curl.exe -fsSL -o $modelPath $url
if ($LASTEXITCODE -ne 0) {
    throw "Download failed with exit code $LASTEXITCODE."
}

$sizeMb = [math]::Round((Get-Item $modelPath).Length / 1MB, 1)
Write-Host "Saved $modelPath ($sizeMb MB)"
