# info.ps1 - v2-compatible wrapper -> cu.ps1 info
param([string]$Title = "", [switch]$All)
$cu = Join-Path (Split-Path -Parent $MyInvocation.MyCommand.Path) "cu.ps1"
$a = @{}; if ($Title) { $a.Title = $Title }; if ($All) { $a.All = $true }
& $cu info @a
exit $LASTEXITCODE
