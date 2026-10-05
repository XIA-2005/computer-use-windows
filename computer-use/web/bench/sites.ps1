# web/bench/sites.ps1 - real-site scenarios for the cu web layer (dedicated instance only; read-only actions)
#   powershell -NoProfile -ExecutionPolicy Bypass -File web\bench\sites.ps1 [-Browser edge|chrome] [-Only bili,163,...]
param([string]$Browser = 'edge', [string]$Only = '')
$ErrorActionPreference = 'Continue'
[Console]::OutputEncoding = [Text.Encoding]::UTF8; $OutputEncoding = [Text.Encoding]::UTF8
$here = Split-Path -Parent $MyInvocation.MyCommand.Path
$root = Split-Path -Parent (Split-Path -Parent $here)
$cu = Join-Path $root 'win\cu.exe'
$filter = @(); if ($Only) { $filter = @($Only.Split(',')) }   # NB: variable names are case-insensitive; never reuse the param name
function J([string]$line) { try { return ($line | ConvertFrom-Json) } catch { return $null } }
function Step([string]$name, [string[]]$argv, [string[]]$fields = @(), [switch]$WantErr) {
  $sw = [Diagnostics.Stopwatch]::StartNew()
  $out = & $cu web @argv -Browser $Browser 2>&1 | Out-String
  $sw.Stop()
  $line = ($out -split "`r?`n" | Where-Object { $_.Trim() } | Select-Object -Last 1)
  $j = J $line
  $info = ''
  if ($j) {
    if (-not $j.ok) { $info = 'ERR ' + $j.err + ': ' + $j.msg }
    foreach ($f in $fields) { $v = $j.$f; if ($null -ne $v) { if ($v -is [array]) { $v = ($v | ForEach-Object { "$_" }) -join '|' }; $s = "$v"; if ($s.Length -gt 70) { $s = $s.Substring(0, 70) + '…' }; $info += " $f=$s" } }
  } else { $info = 'RAW ' + $line.Substring(0, [math]::Min(120, $line.Length)) }
  $okc = if ($j -and $j.ok) { 'ok ' } elseif ($WantErr) { 'err ' } else { 'FAIL' }
  Write-Host ("{0,5}ms {1} {2,-34} {3}" -f $sw.ElapsedMilliseconds, $okc, $name, $info.Trim())
  return $j
}
function Site([string]$key, [scriptblock]$body) {
  if ($filter.Count -gt 0 -and $filter -notcontains $key) { return }
  Write-Host ("--- [{0}] {1} ---" -f $Browser, $key)
  try { & $body } catch { Write-Host ("  exception: " + $_.Exception.Message) }
}

Site 'bili' {
  Step 'open bilibili' @('open', '-Url', 'https://www.bilibili.com') @('ms', 'url') | Out-Null
  Step 'wait stable' @('wait', '-Stable', '-Timeout', '6000') @('ms') | Out-Null
  Step 'els' @('els') @('count', 'ms') | Out-Null
  Step 'find 热门' @('find', '-Text', '热门') @('tag', 'id', 'text', 'count', 'hit', 'cover', 'alts') | Out-Null
  Step 'click 热门' @('click', '-Text', '热门') @('tag', 'text', 'method', 'hit', 'warn') | Out-Null
  Step 'wait url popular' @('wait', '-Url', 'popular', '-Timeout', '6000') @('ms') | Out-Null
  Step 'tabs' @('tabs') @('count', 'active') | Out-Null
  Step 'text' @('text') @('len', 'title') | Out-Null
  Step 'shot marks' @('shot', '-Marks', '-Out', (Join-Path $root 'state\site-bili.jpg')) @('w', 'h', 'marks', 'ms') | Out-Null
}
Site '163' {
  Step 'open music.163' @('open', '-Url', 'https://music.163.com') @('ms', 'url') | Out-Null
  Step 'wait stable' @('wait', '-Stable', '-Timeout', '6000') @('ms') | Out-Null
  Step 'els (iframe content?)' @('els') @('count', 'ms') | Out-Null
  Step 'find 排行榜 (inside iframe)' @('find', '-Text', '排行榜') @('tag', 'text', 'count', 'hit', 'cover') | Out-Null
  Step 'click 排行榜' @('click', '-Text', '排行榜') @('tag', 'text', 'method', 'hit', 'warn') | Out-Null
  Step 'wait toplist' @('wait', '-Url', 'toplist', '-Timeout', '6000') @('ms') | Out-Null
  Step 'find 飙升榜' @('find', '-Text', '飙升榜') @('tag', 'text', 'count', 'hit') | Out-Null
}
Site 'zhihu' {
  Step 'open zhihu' @('open', '-Url', 'https://www.zhihu.com') @('ms', 'url', 'wall') | Out-Null
  Step 'wait stable' @('wait', '-Stable', '-Timeout', '6000') @('ms') | Out-Null
  $i = Step 'info (login wall expected)' @('info') @('wall', 'title', 'url')
  if ($i -and $i.wall -eq 'login') {
    Step 'els on the sign-in page' @('els') @('count', 'wall') | Out-Null
    Write-Host '  (login wall: the dedicated profile has no zhihu login - wall=login is the expected outcome)'
    return
  }
  Step 'els' @('els') @('count', 'ms') | Out-Null
  Step 'find 热榜' @('find', '-Text', '热榜') @('tag', 'text', 'count', 'hit', 'cover', 'alts') | Out-Null
  Step 'click 热榜' @('click', '-Text', '热榜') @('tag', 'text', 'method', 'hit', 'warn') | Out-Null
  Step 'info' @('info') @('url', 'title', 'wall') | Out-Null
}
Site 'github' {
  Step 'open github repo' @('open', '-Url', 'https://github.com/XIA-2005/qq-ai-bot', '-Timeout', '20000') @('ms', 'url') | Out-Null
  Step 'els' @('els') @('count', 'ms') | Out-Null
  Step 'find src dir (repo page)' @('find', '-Text', 'src') @('tag', 'count', 'alts') | Out-Null
  Step 'find Issues exact' @('find', '-Text', 'Issues', '-Exact') @('tag', 'text', 'count', 'hit', 'alts') | Out-Null
  Step 'click Issues' @('click', '-Text', 'Issues', '-Exact') @('tag', 'text', 'method', 'hit', 'verified', 'gone', 'warn') | Out-Null
  Step 'wait url issues' @('wait', '-Url', '/issues', '-Timeout', '15000') @('ms') | Out-Null
  Step 'hover Code tab' @('hover', '-Text', 'Code', '-Exact') @('tag', 'method') | Out-Null
  Step 'type search /' @('keys', '-Keys', '/') @('keys') | Out-Null
  Step 'val focused' @('val') @('value') | Out-Null
}
Site 'jd' {
  Step 'open jd' @('open', '-Url', 'https://www.jd.com', '-Timeout', '20000') @('ms', 'url', 'wall') | Out-Null
  Step 'wait stable' @('wait', '-Stable', '-Timeout', '6000') @('ms') | Out-Null
  # NB: the old #key id is gone (2026-10-05); the visible search box is the only text input on the homepage
  Step 'type search box' @('type', '-Sel', 'input[type=text]', '-Text', '机械键盘', '-Verify') @('mode', 'verify', 'visible', 'warn') | Out-Null
  Step 'click 搜索' @('click', '-Text', '搜索') @('tag', 'text', 'count', 'method', 'hit', 'verified', 'gone', 'warn', 'alts') | Out-Null
  # logged out, jd sends search to passport.jd.com: either the results or a login wall is a correct outcome
  Step 'info (results or login wall)' @('info') @('url', 'wall') | Out-Null
  Step 'scroll wheel' @('scroll', '-Wheel', '-8') @('sy', 'method') | Out-Null
  Step 'els after scroll' @('els') @('count', 'wall') | Out-Null
}
Site 'wiki' {
  # read-heavy article flow: heading anchor click + long text (default cap vs -Max)
  Step 'open wikipedia article' @('open', '-Url', 'https://en.wikipedia.org/wiki/Google_Chrome', '-Timeout', '25000') @('ms', 'url', 'ready', 'warn', 'wall') | Out-Null
  Step 'wait stable' @('wait', '-Stable', '-Timeout', '8000') @('ms') | Out-Null
  Step 'els in article' @('els', '-Sel', '#mw-content-text', '-Size', '300') @('count', 'ms') | Out-Null
  Step 'find References' @('find', '-Text', 'References') @('tag', 'count', 'hit') | Out-Null
  Step 'click References' @('click', '-Text', 'References', '-Index', '1') @('method', 'verified', 'gone', 'warn') | Out-Null
  Step 'text capped' @('text', '-Max', '5000') @('len', 'chars', 'truncated') | Out-Null
}
Site 'mdn' {
  # docs lookup: scoped els + section text (the everyday "read the reference" workflow)
  Step 'open mdn page' @('open', '-Url', 'https://developer.mozilla.org/en-US/docs/Web/API/Element/click_event', '-Timeout', '25000') @('ms', 'url', 'ready', 'wall') | Out-Null
  Step 'els in #content' @('els', '-Sel', '#content', '-Size', '200') @('count', 'ms') | Out-Null
  Step 'find Syntax' @('find', '-Text', 'Syntax') @('tag', 'count', 'hit') | Out-Null
  Step 'text #content' @('text', '-Sel', '#content', '-Max', '20000') @('len', 'truncated') | Out-Null
}
Site 'bing' {
  Step 'open bing' @('open', '-Url', 'https://www.bing.com') @('ms', 'url') | Out-Null
  Step 'type query + enter' @('type', '-Sel', '#sb_form_q', '-Text', 'chrome devtools protocol', '-Enter', '-Verify') @('mode', 'verify', 'enter') | Out-Null
  Step 'wait results' @('wait', '-Sel', '#b_results', '-Timeout', '10000') @('ms') | Out-Null
  Step 'els results' @('els', '-Sel', '#b_results') @('count', 'ms') | Out-Null
  Step 'find first result link' @('find', '-Sel', '#b_results h2 a') @('tag', 'text', 'hit') | Out-Null
}
Site 'douban' {
  Step 'open douban' @('open', '-Url', 'https://www.douban.com') @('ms', 'url') | Out-Null
  Step 'type search' @('type', '-Sel', 'input[name=q]', '-Text', '三体', '-Verify') @('mode', 'verify', 'visible') | Out-Null
  Step 'click 搜索' @('click', '-Text', '搜索') @('tag', 'text', 'count', 'method', 'hit', 'alts') | Out-Null
  Step 'wait search' @('wait', '-Url', 'search', '-Timeout', '10000') @('ms') | Out-Null
  Step 'text' @('text') @('len', 'title') | Out-Null
}
Site 'v2ex' {
  Step 'open v2ex' @('open', '-Url', 'https://www.v2ex.com', '-Timeout', '20000') @('ms', 'url', 'wall') | Out-Null
  $i = Step 'info (cloudflare challenge?)' @('info') @('wall', 'title')
  if ($i -and $i.wall) {
    # measured 2026-10-05: this challenge does NOT clear by waiting (12s + reload + 12s still blocked), so the
    # right behaviour is to report it: errors below must carry wall + hint so the agent stops and asks the user
    Step 'find on challenge (wall hint)' @('find', '-Text', '技术') @('err', 'wall', 'hint') -WantErr | Out-Null
    Step 'wait -Find on challenge (timeout + wall)' @('wait', '-Find', '技术', '-Timeout', '3000') @('err', 'wall') -WantErr | Out-Null
    Write-Host ("  ({0} wall: not real content - expected in a fresh profile; wall/hint is what the agent needs)" -f $i.wall)
    return
  }
  Step 'els' @('els') @('count', 'ms', 'wall') | Out-Null
  Step 'find 技术' @('find', '-Text', '技术', '-Exact') @('tag', 'text', 'count', 'hit') | Out-Null
  Step 'click 技术' @('click', '-Text', '技术', '-Exact') @('tag', 'method', 'hit', 'warn') | Out-Null
  Step 'wait url tech' @('wait', '-Url', 'tech', '-Timeout', '8000') @('ms') | Out-Null
}
