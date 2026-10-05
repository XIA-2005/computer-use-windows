# snap.ps1 - v2-compatible wrapper -> cu.ps1 snap (writes frame file; size limits follow the cu.ps1 defaults,
# pass -MaxSide/-MaxPixels to cu.ps1 if you really need a bigger image)
param([string]$Out = "", [string]$WindowTitle = "", [int]$Quality = 85, [switch]$B64)
$cu = Join-Path (Split-Path -Parent $MyInvocation.MyCommand.Path) "cu.ps1"
$a = @{ MaxSide = -1; MaxPixels = -1; Quality = $Quality }
if ($Out) { $a.Out = $Out }
if ($WindowTitle) { $a.Title = $WindowTitle }
& $cu snap @a
exit $LASTEXITCODE