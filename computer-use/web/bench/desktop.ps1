# desktop.ps1 - desktop-layer speed/accuracy regression bench for cu.exe.
#   Starts web\bench\testwin.ps1 (CU-TEST-WINDOW and CU-TEST-BLANK) in separate processes and drives them
#   through win\cu.exe: snap / blank-retry / find (cold+cached+fuzzy) / snap -Marks / UIA direct click /
#   coordinate fallback / refusal paths (ERR_DISABLED, ERR_UIA_ACT_FAILED) / settle early-return /
#   type -Verify (bg, clipboard, emoji) / -Fg typing.
#   Only the two test windows are touched (the -Fg cases move the cursor and restore it).
#   usage: powershell -NoProfile -ExecutionPolicy Bypass -File web\bench\desktop.ps1 [-Keep]
param([switch]$Keep)
$ErrorActionPreference = "Stop"
$here = Split-Path -Parent $MyInvocation.MyCommand.Path
$root = Split-Path -Parent (Split-Path -Parent $here)          # web\bench -> plugin root
$cu = Join-Path $root "win\cu.exe"
if (-not (Test-Path $cu)) { throw "cu.exe not found: $cu" }

$script:procs = @()
function Start-Win([switch]$Blank) {
  # -File needs its own quotes: Start-Process joins the array without quoting and the repo path has spaces
  $a = @("-NoProfile", "-ExecutionPolicy", "Bypass", "-File", ('"' + (Join-Path $here "testwin.ps1") + '"'))
  if ($Blank) { $a += @("-Blank", "-X", "40", "-Y", "700") }   # away from the normal window: no stacking
  $p = Start-Process powershell -ArgumentList $a -PassThru
  $script:procs += $p
  return $p
}
function Stop-Wins() { foreach ($p in $script:procs) { try { Stop-Process -Id $p.Id -Force -ErrorAction SilentlyContinue } catch { } } }
$script:fails = 0
# build non-ASCII test strings from code points: this file stays pure ASCII (no BOM needed)
function Cps { param([int[]]$c) return (-join ([char[]]$c)) }
# -WantErr: the check EXPECTS ok:false and asserts which err code came back (for refusal paths like ERR_DISABLED)
function Run([string]$name, [string[]]$argv, [scriptblock]$check, [switch]$WantErr) {
  $sw = [Diagnostics.Stopwatch]::StartNew()
  $raw = & $cu @argv 2>&1 | Out-String
  $sw.Stop()
  $ms = $sw.ElapsedMilliseconds
  $j = $null
  try { $j = ConvertFrom-Json -InputObject $raw.Trim() } catch { }
  $verdict = "?"; $note = ""
  if ($null -eq $j) { $verdict = "FAIL"; $note = "unparseable: " + $raw.Trim() }
  else {
    if (-not $WantErr -and $j.ok -eq $false) { $verdict = "FAIL"; $note = "ERR: $($j.err) $($j.msg)" }
    else { $r = & $check $j; $verdict = $r[0]; $note = $r[1] }
  }
  if ($verdict -eq "FAIL") { $script:fails++ }
  $color = if ($verdict -eq "FAIL") { "Red" } else { "Gray" }
  Write-Host ("{0,-38} {1,6} ms  {2,-4}  {3}" -f $name, $ms, $verdict, $note) -ForegroundColor $color
  return $j
}

Write-Host "computer-use desktop bench (cu.exe: $cu)" -ForegroundColor Cyan
# cu.exe emits UTF-8; decode native output as UTF-8 or ConvertFrom-Json chokes on the non-ASCII fields
[Console]::OutputEncoding = New-Object Text.UTF8Encoding($false)
# full-width "STATIC-42" built from code points so this file stays pure ASCII (no BOM needed)
$fwstr = -join ([char[]]@(0xFF33, 0xFF34, 0xFF21, 0xFF34, 0xFF29, 0xFF23, 0xFF0D, 0xFF14, 0xFF12))
$fwq = [Convert]::ToBase64String([Text.Encoding]::UTF8.GetBytes($fwstr))
$win = Start-Win
$blank = Start-Win -Blank
Start-Sleep -Milliseconds 1500
& $cu info -Title "CU-TEST-WINDOW" | Out-Null      # warm-up: daemon start + DLL load excluded from timings

# ---- capture ------------------------------------------------------------------
$fr = Run "snap window" @("snap", "-Title", "CU-TEST-WINDOW") {
  param($j) if ($j.ok -and $j.frame.w -gt 100) { @("ok", "frame $($j.frame.w)x$($j.frame.h) s=$($j.frame.s) $($j.frame.method)") } else { @("FAIL", "no frame: $($j.err)") } }
Run "snap blank window (flat white)" @("snap", "-Title", "CU-TEST-BLANK") {
  param($j) if (-not $j.ok) { @("FAIL", "$($j.err)") }
    elseif ("$($j.frame.method)" -like "screen*") {
      if ("$($j.frame.warn)" -match "ERR_BLANK") { @("FAIL", "screen capture of a flat window must not warn/retry") }
      else { @("ok", "$($j.frame.w)x$($j.frame.h) via screen, no warn, no retry") }
    }
    elseif ("$($j.frame.warn)" -match "ERR_BLANK") { @("ok", "print path (occluded): warn is expected, 3x150ms cap") }
    else { @("ok", "print path") } } | Out-Null
Run "snap -Marks (UIA elements)" @("snap", "-Title", "CU-TEST-WINDOW", "-Marks") {
  param($j) if ($j.ok -and $j.elements.Count -gt 0) { @("ok", "$($j.elements.Count) elements, uia=$($j.uia.status)") } else { @("FAIL", "uia=$($j.uia.status) count=$($j.elements.Count)") } } | Out-Null

# ---- OCR: cold, cached, fuzzy, changed ---------------------------------------
# -Method print: the test window is deliberately DPI-unaware, so a screen grab of it is the blurred
# upscaled rendering while PrintWindow renders it crisply - pin the path so OCR is deterministic
Run "find cold (static text)" @("find", "-Title", "CU-TEST-WINDOW", "-Find", "STATIC-42", "-Method", "print") {
  param($j) if ($j.count -ge 1) { @("ok", "match=$($j.hits[0].match) pass=$($j.pass)") } else { @("FAIL", "not found") } } | Out-Null
Run "find cached (same pixels, 2-slot)" @("find", "-Title", "CU-TEST-WINDOW", "-Find", "STATIC-42", "-Method", "print") {
  param($j) if ($j.cached) { @("ok", "cached=true") } else { @("FAIL", "cached=$($j.cached)") } } | Out-Null
Run "find full-width query (norm)" @("find", "-Title", "CU-TEST-WINDOW", "-FindB64", $fwq, "-Method", "print") {
  param($j) if ($j.count -ge 1) { @("ok", "match=$($j.hits[0].match)") } else { @("FAIL", "not found") } } | Out-Null
Run "find alternatives a|b" @("find", "-Title", "CU-TEST-WINDOW", "-Find", "NOPE-1|STATIC-42", "-Method", "print") {
  param($j) if ($j.count -ge 1) { @("ok", "count=$($j.count)") } else { @("FAIL", "not found") } } | Out-Null

# ---- UIA direct action + coordinate fallback + no-op settle -------------------
$m = Run "snap -Marks for element ids" @("snap", "-Title", "CU-TEST-WINDOW", "-Marks") {
  param($j) if ($j.ok -and $j.elements.Count -gt 0) { @("ok", "$($j.elements.Count) elements") } else { @("FAIL", "no elements") } }
$btnId = $null; $editId = $null
if ($m -and $m.elements) {
  # the title-bar minimize/maximize/close buttons are Buttons too: pick the largest enabled one (the Save button)
  $btnArea = 0
  foreach ($e in $m.elements) {
    $disabled = ($e.Count -ge 8 -and [int]$e[7] -eq 0)
    if ($e[1] -eq "Button" -and -not $disabled) { $a2 = [int]$e[5] * [int]$e[6]; if ($a2 -gt $btnArea) { $btnArea = $a2; $btnId = [int]$e[0] } }
    if (-not $editId -and $e[1] -eq "Edit") { $editId = [int]$e[0] }
  }
}
if ($btnId) {
  Run "click -Id (UIA invoke)" @("click", "-Title", "CU-TEST-WINDOW", "-Id", "$btnId", "-Settle", "600") {
    param($j) if ($j.ok -and $j.method -eq "uia" -and $j.changed -gt 0) { @("ok", "via=$($j.via) changed=$($j.changed)") } else { @("FAIL", "method=$($j.method) via=$($j.via) changed=$($j.changed)") } } | Out-Null
  Run "find SAVED-1 (no stale OCR cache)" @("find", "-Title", "CU-TEST-WINDOW", "-Find", "SAVED-1", "-Method", "print") {
    param($j) if ($j.count -ge 1) { @("ok", "cached=$($j.cached) match=$($j.hits[0].match)") } else { @("FAIL", "stale cache / OCR did not see the change") } } | Out-Null
  Run "click -Id -Method coord (fallback)" @("click", "-Title", "CU-TEST-WINDOW", "-Id", "$btnId", "-Method", "coord", "-Settle", "600") {
    param($j) if ($j.ok -and $j.method -eq "coord" -and $j.changed -gt 0) { @("ok", "changed=$($j.changed)") } else { @("FAIL", "method=$($j.method) changed=$($j.changed)") } } | Out-Null
} else { Write-Host "!! button element not found" -ForegroundColor Red; $script:fails++ }

# ---- refusal paths (expected errors) ------------------------------------------
if ($editId) {
  # an Edit has no Invoke/Toggle/Select/Expand pattern: -Method uia must REFUSE (no silent coord fallback)
  Run "click -Id -Method uia (no pattern) refused" @("click", "-Title", "CU-TEST-WINDOW", "-Id", "$editId", "-Method", "uia") {
    param($j) if ($j.err -eq "ERR_UIA_ACT_FAILED") { @("ok", "$($j.msg)") } else { @("FAIL", "err=$($j.err), want ERR_UIA_ACT_FAILED") } } -WantErr | Out-Null
}
$disId = $null
if ($m -and $m.elements) {
  foreach ($e in $m.elements) {
    if (-not $disId -and $e[1] -eq "Button" -and $e.Count -ge 8 -and [int]$e[7] -eq 0) { $disId = [int]$e[0] }
  }
}
if ($disId) {
  Run "click -Id disabled -> ERR_DISABLED" @("click", "-Title", "CU-TEST-WINDOW", "-Id", "$disId") {
    param($j) if ($j.err -eq "ERR_DISABLED") { @("ok", "refused instead of blind-clicking") } else { @("FAIL", "err=$($j.err), want ERR_DISABLED") } } -WantErr | Out-Null
} else { Write-Host "!! disabled button not found in element list" -ForegroundColor Red; $script:fails++ }
$fw = 0; $fh = 0
if ($fr -and $fr.ok) { $fw = [int]$fr.frame.w; $fh = [int]$fr.frame.h }
if ($fw -gt 100) {
  Run "no-op click (settle early return)" @("click", "-Title", "CU-TEST-WINDOW", "-X", "$($fw - 60)", "-Y", "$($fh - 60)") {
    param($j) if ($j.ok -and $j.settle_ms -lt 450 -and $j.no_change) { @("ok", "settle_ms=$($j.settle_ms) no_change=true") } else { @("FAIL", "settle_ms=$($j.settle_ms) no_change=$($j.no_change)") } } | Out-Null
}
Run "find -Region (frame region OCR)" @("find", "-Title", "CU-TEST-WINDOW", "-Find", "SAVED", "-Method", "print", "-Region", "0,0,$([int]($fw/2)),$([int]($fh/2))") {
  param($j) if ($j.count -ge 1) { @("ok", "match=$($j.hits[0].match)") } else { @("FAIL", "not found in region") } } | Out-Null

# ---- typing: background, clipboard, emoji, foreground -------------------------
if ($editId) {
  $b64 = [Convert]::ToBase64String([Text.Encoding]::UTF8.GetBytes((Cps 0x4F60, 0x597D) + " world"))
  Run "type -Id -Verify (bg)" @("type", "-Title", "CU-TEST-WINDOW", "-Id", "$editId", "-TextB64", $b64, "-Verify") {
    param($j) if ($j.ok -and $j.verify.contains) { @("ok", "verify.contains=true") } else { @("FAIL", "$($j.verify | ConvertTo-Json -Compress)") } } | Out-Null
  $b64c = [Convert]::ToBase64String([Text.Encoding]::UTF8.GetBytes("clip " + (Cps 0x6D4B, 0x8BD5)))
  Run "type -Method clip -Verify (P/Invoke)" @("type", "-Title", "CU-TEST-WINDOW", "-Id", "$editId", "-TextB64", $b64c, "-Method", "clip", "-Verify") {
    param($j) if ($j.ok -and $j.verify.contains) { @("ok", "verify.contains=true") } else { @("FAIL", "$($j.verify | ConvertTo-Json -Compress)") } } | Out-Null
  $b64e = [Convert]::ToBase64String([Text.Encoding]::UTF8.GetBytes("emoji " + (Cps 0xD83D, 0xDE42) + " done"))
  Run "type emoji -Verify (bg WM_CHAR)" @("type", "-Title", "CU-TEST-WINDOW", "-Id", "$editId", "-TextB64", $b64e, "-Verify") {
    param($j) if ($j.ok -and $j.verify.contains) { @("ok", "verify.contains=true") } else { @("FAIL", "$($j.verify | ConvertTo-Json -Compress)") } } | Out-Null
  Run "activate (fg)" @("activate", "-Title", "CU-TEST-WINDOW") {
    param($j) if ($j.ok) { @("ok", "") } else { @("FAIL", "$($j.err)") } } | Out-Null
  Run "type emoji -Verify (-Fg SendInput)" @("type", "-Title", "CU-TEST-WINDOW", "-Id", "$editId", "-TextB64", $b64e, "-Verify", "-Fg") {
    param($j) if ($j.ok -and $j.verify.contains) { @("ok", "verify.contains=true") } else { @("FAIL", "$($j.verify | ConvertTo-Json -Compress)") } } | Out-Null
}

Write-Host ""
if ($script:fails -eq 0) { Write-Host "ALL CHECKS PASSED" -ForegroundColor Green } else { Write-Host ("$script:fails CHECK(S) FAILED") -ForegroundColor Red }
if (-not $Keep) { Stop-Wins } else { Write-Host "(test windows kept)" -ForegroundColor Yellow }
exit $script:fails