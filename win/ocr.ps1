# ocr.ps1 - v2-compatible wrapper -> cu.ps1 ocr  (-Path <image>; without -Path OCRs the current frame at native resolution)
param([string]$Path = "")
$cu = Join-Path (Split-Path -Parent $MyInvocation.MyCommand.Path) "cu.ps1"
if ($Path) { & $cu ocr -Path $Path } else { & $cu ocr }
exit $LASTEXITCODE
