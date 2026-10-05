# act.ps1 - v2-compatible wrapper -> cu.ps1 click -Fg (foreground; needs user permission, see SKILL.md)
param([string]$WindowTitle = "", [int]$X = 0, [int]$Y = 0)
$cu = Join-Path (Split-Path -Parent $MyInvocation.MyCommand.Path) "cu.ps1"
if ($WindowTitle) { & $cu click -Title $WindowTitle -X $X -Y $Y -Fg } else { & $cu click -X $X -Y $Y -Fg }
exit $LASTEXITCODE
