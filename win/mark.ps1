# mark.ps1 - v2-compatible wrapper -> cu.ps1 mark (also writes <name>-zoom.png, a 4x magnified check sheet)
param([string]$Path = "", [string]$Pts = "", [string]$Out = "", [int]$Size = 16, [int]$Zoom = 24)
$cu = Join-Path (Split-Path -Parent $MyInvocation.MyCommand.Path) "cu.ps1"
$a = @{ Size = $Size; Zoom = $Zoom }
if ($Path) { $a.Path = (Resolve-Path $Path).Path }
if ($Pts) { $a.Pts = $Pts }
if ($Out) { $a.Out = $(if ([IO.Path]::IsPathRooted($Out) -or -not $Path) { $Out } else { Join-Path (Split-Path -Parent $a.Path) $Out }) }
& $cu mark @a
exit $LASTEXITCODE
