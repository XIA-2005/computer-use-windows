# type.ps1 - v2-compatible wrapper -> cu.ps1 type
#   -Method clip|fg  -Mode char   (new: -Method auto is the default in cu.ps1: EM_REPLACESEL / WM_CHAR to the focused control)
param([string]$WindowTitle = "", [string]$Text = "", [string]$Method = "auto", [string]$Mode = "", [int]$DelayMs = 0, [switch]$KeepClipboard)
$cu = Join-Path (Split-Path -Parent $MyInvocation.MyCommand.Path) "cu.ps1"
if (-not $WindowTitle) { Write-Output '{"ok":false,"err":"ERR_NO_WINDOW"}'; exit 2 }
$b64 = [Convert]::ToBase64String([Text.Encoding]::UTF8.GetBytes($Text))
$a = @{ Title = $WindowTitle; TextB64 = $b64 }
if ($Mode -eq "char") { $a.Method = "char" }
elseif ($Method -eq "fg") { $a.Fg = $true }
else { $a.Method = $Method }
& $cu type @a
exit $LASTEXITCODE
