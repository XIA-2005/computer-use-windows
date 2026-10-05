# act-bg.ps1 - v2-compatible wrapper -> cu.ps1 click (background). X/Y = coords on the last snap image.
param([string]$WindowTitle = "", [int]$X = 0, [int]$Y = 0)
$cu = Join-Path (Split-Path -Parent $MyInvocation.MyCommand.Path) "cu.ps1"
if (-not $WindowTitle) { Write-Output '{"ok":false,"err":"ERR_NO_WINDOW"}'; exit 2 }
& $cu click -Title $WindowTitle -X $X -Y $Y
exit $LASTEXITCODE
