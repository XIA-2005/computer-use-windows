# web/bench/bench.ps1 - timing + correctness check for the cu web layer (dedicated browser instance only)
#   powershell -NoProfile -ExecutionPolicy Bypass -File web\bench\bench.ps1 [-N 5] [-Browser edge]
param([int]$N = 5, [string]$Browser = 'edge')
$ErrorActionPreference = 'Continue'
[Console]::OutputEncoding = [Text.Encoding]::UTF8; $OutputEncoding = [Text.Encoding]::UTF8
$here = Split-Path -Parent $MyInvocation.MyCommand.Path
$root = Split-Path -Parent (Split-Path -Parent $here)
$cu = Join-Path $root 'win\cu.exe'
$page = 'file:///' + ((Join-Path $here 'page.html') -replace '\\', '/')
$challenge = 'file:///' + ((Join-Path $here 'challenge.html') -replace '\\', '/')
$login = 'file:///' + ((Join-Path $here 'login.html') -replace '\\', '/')
$results = New-Object System.Collections.ArrayList
function Run([string]$label, [string[]]$argv, [int]$n = $N, [scriptblock]$check = $null) {
  $times = @(); $last = $null; $okc = 0
  for ($i = 0; $i -lt $n; $i++) {
    $sw = [Diagnostics.Stopwatch]::StartNew()
    $out = & $cu @argv 2>&1 | Out-String
    $sw.Stop(); $times += $sw.ElapsedMilliseconds
    $line = ($out -split "`r?`n" | Where-Object { $_.Trim() } | Select-Object -Last 1)
    $last = $line
    try { $j = $line | ConvertFrom-Json } catch { $j = $null }
    if ($j -and $j.ok) { $okc++ }
  }
  $sorted = $times | Sort-Object
  $med = $sorted[[int][math]::Floor(($sorted.Count - 1) / 2)]
  $chk = ''
  if ($check) { try { $chk = & $check $j } catch { $chk = 'check-error: ' + $_.Exception.Message } }
  [void]$results.Add([pscustomobject]@{ test = $label; n = $n; ok = "$okc/$n"; min_ms = $sorted[0]; med_ms = $med; max_ms = $sorted[-1]; check = $chk; last = ($last.Substring(0, [math]::Min(110, $last.Length))) })
}
function Ev([string]$js) { $o = & $cu web eval -Browser $Browser -Js $js 2>&1 | Out-String; try { return (($o -split "`r?`n" | Where-Object { $_.Trim() } | Select-Object -Last 1) | ConvertFrom-Json).value } catch { return $o } }

Write-Host "page: $page"
& $cu web open -Browser $Browser -Url $page | Out-Null
Run 'frame (pipe+dispatch only)' @('frame') $N
Run 'web info' @('web', 'info', '-Browser', $Browser) $N
Run 'web text' @('web', 'text', '-Browser', $Browser) $N
Run 'web open (file page)' @('web', 'open', '-Browser', $Browser, '-Url', $page) 3
Run 'click -Text nested button' @('web', 'click', '-Browser', $Browser, '-Text', '提交订单') $N { param($j) 'last=' + (Ev 'window.__last') + ' events=' + (Ev 'JSON.stringify(window.__events.slice(-4))') }
Run 'click -Text duplicate (hidden first)' @('web', 'click', '-Browser', $Browser, '-Text', '重复项') 1 { param($j) 'last=' + (Ev 'window.__last') }
Run 'click -Text card link substring' @('web', 'click', '-Browser', $Browser, '-Text', 'Learn') 1 { param($j) 'tag=' + $j.tag + ' last=' + (Ev 'window.__last') }
Run 'click -Text aria-label icon' @('web', 'click', '-Browser', $Browser, '-Text', '搜索') 1 { param($j) 'last=' + (Ev 'window.__last') }
Run 'click -Text plain div' @('web', 'click', '-Browser', $Browser, '-Text', '纯 div 按钮') 1 { param($j) 'last=' + (Ev 'window.__last') }
Run 'click -Text mousedown menu' @('web', 'click', '-Browser', $Browser, '-Text', '打开菜单') 1 { param($j) 'menu=' + (Ev "document.getElementById('menu').className") }
Run 'click -Text bottom (scroll)' @('web', 'click', '-Browser', $Browser, '-Text', '页面底部按钮') 1 { param($j) 'last=' + (Ev 'window.__last') }
Run 'type -Sel #q -Verify' @('web', 'type', '-Browser', $Browser, '-Sel', '#q', '-Text', 'hello world', '-Verify') $N { param($j) 'qv=' + (Ev "document.getElementById('qv').textContent") + ' keyup=' + (Ev 'window.__keyup||0') }
Run 'type contenteditable' @('web', 'type', '-Browser', $Browser, '-Sel', '#editor', '-Text', '富文本内容') 1 { param($j) 'ed=' + (Ev "document.getElementById('editor').textContent") + ' inputType=' + (Ev 'window.__edInput') }
Run 'type select by label' @('web', 'type', '-Browser', $Browser, '-Sel', '#sl', '-Text', '选项B') 1 { param($j) 'sl=' + (Ev "document.getElementById('sl').value") }
Run 'click late trigger' @('web', 'click', '-Browser', $Browser, '-Sel', '#lateBtn') 1
Run 'wait -Sel (appears ~350ms)' @('web', 'wait', '-Browser', $Browser, '-Sel', '#late', '-Timeout', '5000') 1
Run 'shot viewport' @('web', 'shot', '-Browser', $Browser, '-Out', (Join-Path $root 'state\bench-shot.jpg')) 3 { param($j) 'w=' + $j.w + ' h=' + $j.h + ' scale=' + $j.scale }
Run 'shot full' @('web', 'shot', '-Browser', $Browser, '-Full', '-Out', (Join-Path $root 'state\bench-full.jpg')) 2 { param($j) 'w=' + $j.w + ' h=' + $j.h + ' scale=' + $j.scale }
Run 'keys -Sel #q ctrl+a delete' @('web', 'keys', '-Browser', $Browser, '-Sel', '#q', '-Keys', 'ctrl+a delete') 1 { param($j) 'q=[' + (Ev "document.getElementById('q').value") + ']' }
Run 'find -Text (locate only)' @('web', 'find', '-Browser', $Browser, '-Text', '重复项') $N { param($j) 'id=' + $j.id + ' count=' + $j.count + ' hit=' + $j.hit }
Run 'hover -Text' @('web', 'hover', '-Browser', $Browser, '-Text', '悬停我') 1 { param($j) 'hvs=' + (Ev "document.getElementById('hvs').textContent") }
Run 'els (numbered elements)' @('web', 'els', '-Browser', $Browser) $N { param($j) 'count=' + $j.count }
Run 'click -Id 1' @('web', 'click', '-Browser', $Browser, '-Id', '1') 1 { param($j) 'tag=' + $j.tag + ' text=' + $j.text + ' last=' + (Ev 'window.__last') }
Run 'type -Method keys #ta' @('web', 'type', '-Browser', $Browser, '-Sel', '#ta', '-Method', 'keys', '-Text', 'typed via insertText', '-Verify') 1 { param($j) 'verify=' + $j.verify + ' mode=' + $j.mode }
Run 'scroll -Wheel -5' @('web', 'scroll', '-Browser', $Browser, '-Wheel', '-5') 1 { param($j) 'sy=' + $j.sy }
Run 'scroll -Text (into view)' @('web', 'scroll', '-Browser', $Browser, '-Text', '页面底部按钮') 1 { param($j) 'sy=' + $j.sy }
Run 'shot -Marks' @('web', 'shot', '-Browser', $Browser, '-Marks', '-Out', (Join-Path $root 'state\bench-marks.jpg')) 2 { param($j) 'marks=' + $j.marks + ' w=' + $j.w + ' h=' + $j.h }
Run 'wait -Stable' @('web', 'wait', '-Browser', $Browser, '-Stable', '-Timeout', '4000') 1
Run 'click -Text covered (overlay)' @('web', 'click', '-Browser', $Browser, '-Text', 'cu web bench page', '-Exact') 1 { param($j) 'method=' + $j.method + ' hit=' + $j.hit + ' warn=' + $j.warn }
# Enter robustness: a textarea form whose Enter is not handled (measured on Bing) must not lose the query.
# Expected: cu detects the swallowed Enter, strips the CRLF and clicks the form's submit button.
Run 'type -Enter swallowed -> submit-click' @('web', 'type', '-Browser', $Browser, '-Sel', '#lateq', '-Text', 'enter fallback', '-Enter') 1 { param($j) 'enterVia=' + $j.enterVia + ' via=' + $j.enterFallback + ' note=' + $j.enterNote + ' warn=' + $j.warn }
Run 'wait url lateq (submitted)' @('web', 'wait', '-Browser', $Browser, '-Url', 'lateq=', '-Timeout', '6000') 1
Run 'info after submit (no stray CRLF)' @('web', 'info', '-Browser', $Browser) 1 { param($j) if (("$($j.url)" -like '*lateq=enter*') -and ("$($j.url)" -notlike '*%0D*') -and ("$($j.url)" -notlike '*%0A*')) { 'url ok: ' + ($j.url -replace '^.*lateq=', 'lateq=') } else { 'BAD url=' + $j.url } }
# Wall detection: a local Cloudflare-like interstitial must be reported, never treated as real content.
Run 'open challenge page (wall tag)' @('web', 'open', '-Browser', $Browser, '-Url', $challenge) 1 { param($j) 'wall=' + $j.wall }
Run 'els on challenge (wall tag)' @('web', 'els', '-Browser', $Browser) 1 { param($j) 'count=' + $j.count + ' wall=' + $j.wall }
Run 'find on challenge (wall tag)' @('web', 'find', '-Browser', $Browser, '-Text', '正在安全验证不存在') 1 { param($j) 'expected error: err=' + $j.err + ' wall=' + $j.wall }
Run 'text on challenge (wall tag)' @('web', 'text', '-Browser', $Browser) 1 { param($j) 'wall=' + $j.wall + ' len=' + $j.len }
# Login wall: a sign-in URL with a password field must be tagged so the agent stops instead of guessing selectors.
Run 'open login page (wall=login)' @('web', 'open', '-Browser', $Browser, '-Url', $login) 1 { param($j) 'wall=' + $j.wall }
Run 'els on login page (wall=login)' @('web', 'els', '-Browser', $Browser) 1 { param($j) 'count=' + $j.count + ' wall=' + $j.wall }
# A site-initiated cross-document navigation (like JD's redirect to passport.jd.com): the next command runs in a
# document no cu helper has touched yet - it must auto-inject and work (measured failure: info -> ReferenceError).
Run 'open page then click cross-doc link' @('web', 'open', '-Browser', $Browser, '-Url', $page) 1
Run 'click cross-document link' @('web', 'click', '-Browser', $Browser, '-Sel', '#crossdoc') 1 { param($j) 'method=' + $j.method + ' gone=' + $j.gone }
Run 'info first in new document (lib auto-inject)' @('web', 'info', '-Browser', $Browser) 1 { param($j) 'wall=' + $j.wall + ' url=' + ("$($j.url)" -replace '^.*/', '') }
$results | Format-Table -AutoSize -Wrap | Out-String -Width 260 | Write-Host
