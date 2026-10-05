# snap.ps1 - v2-compatible wrapper -> cu.ps1 snap (full resolution, writes frame file)
param([string]$Out = "", [string]$WindowTitle = "", [int]$Quality = 85, [switch]$B64)
$cu = Join-Path (Split-Path -Parent $MyInvocation.MyCommand.Path) "cu.ps1"
$a = @{ MaxSide = 0; MaxPixels = 0; Quality = $Quality }
if ($Out) { $a.Out = $Out }
if ($WindowTitle) { $a.Title = $WindowTitle }
& $cu snap @a
exit $LASTEXITCODE
