# cu.ps1 - Windows Computer-Use: one entry point for every primitive (see ../SKILL.md)
#   powershell -NoProfile -ExecutionPolicy Bypass -File cu.ps1 <cmd> [options]
#   cmds: info | snap | zoom | click | double | rclick | mclick | move | down | up | drag | scroll |
#         key | type | mark | ocr | find | wait | activate | frame | els | do | serve |
#         web <sub> ...   (browser over CDP: start stop status open find els click hover scroll type keys text val eval shot wait tabs tab ...)
#   Fast path: win\cu.exe <same args>  (resident daemon over a named pipe, ~30-60 ms per call)
#   Every command prints ONE line of JSON. ok:false => exit code 1.
param(
  [Parameter(Position=0)][string]$Cmd = "help",
  [Parameter(Position=1)][string]$Sub = "",
  [string]$Title, [string]$Proc, [string]$Hwnd,
  [double]$X, [double]$Y, [double]$X2, [double]$Y2,
  [switch]$Screen,
  [string]$Frame, [string]$Out, [string]$Path,
  [int]$MaxSide = -1, [int]$MaxPixels = -1, [double]$Scale = 0, [int]$Quality = 85,
  [string]$Region, [int]$Grid = 0, [int]$R = 120, [int]$ZoomSize = 900,
  [string]$Method = "auto", [switch]$Fg, [switch]$KeepCursor,
  [string]$Button = "left", [string]$Mods = "",
  [string]$Keys, [string]$Text, [string]$TextB64, [string]$TextFile, [switch]$Enter,
  [string]$Find, [string]$FindB64, [int]$Index = 1,
  [int]$Repeat = 1, [int]$Wheel = -3,
  [switch]$Snap, [int]$Settle = 1000, [switch]$Force, [switch]$Restore, [switch]$All,
  [string]$Pts, [int]$Size = 16, [int]$Zoom = 24,
  [int]$Ms = 0, [switch]$Stable, [int]$Timeout = 8000,
  [string]$Steps, [string]$StepsFile,
  [int]$Id = 0, [string]$Name, [string]$NameB64, [switch]$Marks, [switch]$Uia, [switch]$Text2, [int]$UiaTimeout = 1500,
  [switch]$NoSnapTo, [switch]$Verify, [int]$Quiet = 400, [string]$Pipe, [int]$Idle = 0,
  [string]$Sel, [string]$Js, [string]$Url, [string]$Browser = "edge",
  [switch]$NewTab, [switch]$Full, [switch]$Ready, [switch]$Append, [switch]$Exact
)
$ErrorActionPreference = "Stop"
[Console]::OutputEncoding = New-Object System.Text.UTF8Encoding($false)
$script:here = Split-Path -Parent $MyInvocation.MyCommand.Path
$script:state = if ($env:CU_STATE) { $env:CU_STATE } else { Join-Path (Split-Path -Parent $script:here) "state" }
$script:lastFrame = Join-Path $script:state "last.frame.json"
$script:cfg = @{ MaxSide = 1568; MaxPixels = 1150000; Cap = 1.0 }   # defaults sized for typical multimodal input limits

# ------------------------------------------------------------------ load core (compiled once, cached by source hash)
function Import-Core {
  if ('CU.Core' -as [type]) { return }
  $srcs = @((Join-Path $script:here "cu.cs"), (Join-Path $script:here "uia.cs"), (Join-Path $script:here "web.cs"))
  $joined = (($srcs | ForEach-Object { (Get-FileHash $_ -Algorithm MD5).Hash }) -join '|')
  $hash = ([BitConverter]::ToString([Security.Cryptography.MD5]::Create().ComputeHash([Text.Encoding]::UTF8.GetBytes($joined)))).Replace('-', '').ToLowerInvariant().Substring(0, 12)
  $bin = Join-Path $script:here "bin"
  $dll = Join-Path $bin "cu-$hash.dll"
  if (-not (Test-Path $dll)) {
    New-Item -ItemType Directory -Force -Path $bin | Out-Null
    Get-ChildItem $bin -Filter "cu-*.dll" -ErrorAction SilentlyContinue | ForEach-Object { Remove-Item $_.FullName -ErrorAction SilentlyContinue }
    $fw = Join-Path $env:WINDIR "Microsoft.NET\Framework64\v4.0.30319"; if (-not (Test-Path $fw)) { $fw = Join-Path $env:WINDIR "Microsoft.NET\Framework\v4.0.30319" }
    $wpf = Join-Path $fw "WPF"
    $refs = @("System.Drawing.dll", "System.Web.Extensions.dll", "System.Management.dll", (Join-Path $wpf "UIAutomationClient.dll"), (Join-Path $wpf "UIAutomationTypes.dll"), (Join-Path $wpf "WindowsBase.dll"))
    $csc = Join-Path $fw "csc.exe"
    $tmp = "$dll.$PID.tmp"
    if (Test-Path $csc) {
      $o = & $csc /nologo /target:library /optimize "/out:$tmp" ($refs | ForEach-Object { "/r:$_" }) $srcs 2>&1
      if ($LASTEXITCODE -ne 0) { throw ("csc failed: " + (($o | Select-Object -First 5) -join ' ')) }
    } else {
      Add-Type -Path $srcs -ReferencedAssemblies $refs -OutputAssembly $tmp -OutputType Library
    }
    try { Move-Item $tmp $dll -Force -ErrorAction Stop } catch { if (-not (Test-Path $dll)) { throw } ; Remove-Item $tmp -ErrorAction SilentlyContinue }
  }
  if (-not ('CU.Core' -as [type])) { Add-Type -Path $dll }
  [CU.Core]::Init()
}

# ------------------------------------------------------------------ helpers
function Q($s) { return [CU.J]::Q([string]$s) }
function Err($code, $msg) { return [CU.J]::Err($code, $msg) }
function Merge($json, $frag) { if (-not $frag) { return $json }; return $json.Substring(0, $json.Length - 1) + "," + $frag + "}" }
function P($a, $k, $d) { if ($a.ContainsKey($k) -and $null -ne $a[$k] -and "$($a[$k])" -ne "") { return $a[$k] }; return $d }
function Has($a, $k) { return $a.ContainsKey($k) -and $null -ne $a[$k] -and "$($a[$k])" -ne "" }
function IsErr($j) { return $j.StartsWith('{"ok":false') }
function Fail($code, $msg) { throw [System.Exception]::new("CUERR|" + $code + "|" + $msg) }

function Get-FramePath($a) {
  $f = P $a 'Frame' $script:lastFrame
  if ($f -match '\.(png|jpe?g|bmp)$') { $f = [IO.Path]::ChangeExtension($f, ".frame.json") }
  return $f
}
function Get-Frame($a) { return [CU.Frame]::Load((Get-FramePath $a)) }

function Resolve-Window($a) {
  if (Has $a 'Hwnd') {
    $s = "$($a.Hwnd)"
    if ($s -match '^0x') { return [Convert]::ToInt64($s.Substring(2), 16) }
    return [int64]$s
  }
  if ((Has $a 'Title') -or (Has $a 'Proc')) {
    $ti = P $a 'Title' ""; $pr = P $a 'Proc' ""
    # the same -Title/-Proc a moment ago: re-check that window (3 API calls) instead of enumerating every window (~50 ms)
    $wc = $script:winCache
    if ($wc -and $wc.key -eq ($ti + '|' + $pr) -and ([Environment]::TickCount - $wc.ts) -lt 3000 -and [CU.Core]::StillMatches($wc.hwnd, $ti, $pr)) {
      $script:findInfo = $wc.info; $wc.ts = [Environment]::TickCount; return $wc.hwnd
    }
    $h = [IntPtr]::Zero
    $m = [CU.Core]::Find($ti, $pr, [ref]$h)
    if ($h -eq [IntPtr]::Zero) { Fail "ERR_NO_WINDOW" ("no visible window matches title='" + $ti + "' proc='" + $pr + "'") }
    $script:findInfo = $m
    $script:winCache = @{ key = ($ti + '|' + $pr); hwnd = $h.ToInt64(); ts = [Environment]::TickCount; info = $m }
    return $h.ToInt64()
  }
  return 0
}

function Get-Text($a, $plain, $b64, $file) {
  if (Has $a $b64) { return [Text.Encoding]::UTF8.GetString([Convert]::FromBase64String($a[$b64])) }
  if ($file -and (Has $a $file)) { return [IO.File]::ReadAllText($a[$file], [Text.Encoding]::UTF8) }
  if (Has $a $plain) { return [string]$a[$plain] }
  return $null
}

function ModBits($s) {
  $b = 0
  foreach ($m in ("$s" -split '[,+ ]')) { switch ($m.Trim().ToLower()) { "ctrl" { $b = $b -bor 1 } "control" { $b = $b -bor 1 } "shift" { $b = $b -bor 2 } } }
  return $b
}

# Resolve the action context: target window + physical screen point(s)
function Get-Target($a, [bool]$needPoint, [bool]$needPoint2) {
  $ctx = @{ hw = [int64](Resolve-Window $a); sx = 0; sy = 0; sx2 = 0; sy2 = 0; frame = $null; note = "" }
  $screen = [bool](P $a 'Screen' $false)
  if ($needPoint -and (Has $a 'Id') -and [int]$a.Id -gt 0) {
    $el = Get-CachedEl $a ([int]$a.Id)
    $a['X'] = $el.Cx; $a['Y'] = $el.Cy; $screen = $true
    $ctx.note = ',"el":' + (ElJson $el); $ctx.el = $el
  } elseif ($needPoint -and ((Has $a 'Name') -or (Has $a 'NameB64'))) {
    $r = Find-ByName $a
    $a['X'] = $r.x; $a['Y'] = $r.y; $screen = $r.screen
    $ctx.note = $r.note; $ctx.el = $r.el
  } elseif ($needPoint -and ((Has $a 'Find') -or (Has $a 'FindB64'))) {
    $hit = Find-Text $a
    $a['X'] = $hit.cx; $a['Y'] = $hit.cy; $screen = ($script:ocrSpace -eq 'screen')
    $ctx.note = ',"found":' + (Q $hit.text)
  }
  if (-not $screen -and ($needPoint -or $needPoint2 -or $ctx.hw -eq 0)) {
    $fr = Get-Frame $a
    if ($null -eq $fr) {
      if ($needPoint) { Fail "ERR_NO_FRAME" "no frame found - run 'snap' first, or pass -Screen for physical screen coordinates" }
    } else {
      $ctx.frame = $fr
      if ($ctx.hw -ne 0 -and $fr.hwnd -ne 0 -and $fr.hwnd -ne $ctx.hw) { Fail "ERR_FRAME_MISMATCH" "last frame belongs to another window - snap this window first" }
      $chk = $fr.Check([bool](P $a 'Force' $false) -or -not ($needPoint -or $needPoint2))
      if ($chk) { Fail $chk "window moved/resized/closed since the frame was taken - snap again (or -Force)" }
      if ($ctx.hw -eq 0) { $ctx.hw = $fr.hwnd }
    }
  }
  if ($needPoint) {
    if (-not (Has $a 'X') -or -not (Has $a 'Y')) { Fail "ERR_ARGS" "need -X -Y (or -Find text)" }
    if ($screen) { $ctx.sx = [int]$a.X; $ctx.sy = [int]$a.Y }
    else {
      if (-not $ctx.frame.Inside([double]$a.X, [double]$a.Y)) { Fail "ERR_OUTSIDE_IMAGE" ("point " + $a.X + "," + $a.Y + " is outside the " + $ctx.frame.w + "x" + $ctx.frame.h + " frame image") }
      $p = $ctx.frame.ToScreen([double]$a.X, [double]$a.Y); $ctx.sx = $p[0]; $ctx.sy = $p[1]
      if (-not [bool](P $a 'NoSnapTo' $false) -and -not $ctx.note) { Invoke-SnapTo $a $ctx }
    }
  }
  if ($needPoint2) {
    if (-not (Has $a 'X2') -or -not (Has $a 'Y2')) { Fail "ERR_ARGS" "need -X2 -Y2" }
    if ($screen) { $ctx.sx2 = [int]$a.X2; $ctx.sy2 = [int]$a.Y2 }
    else {
      if (-not $ctx.frame.Inside([double]$a.X2, [double]$a.Y2)) { Fail "ERR_OUTSIDE_IMAGE" "end point is outside the frame image" }
      $p = $ctx.frame.ToScreen([double]$a.X2, [double]$a.Y2); $ctx.sx2 = $p[0]; $ctx.sy2 = $p[1]
    }
  }
  if ($ctx.hw -eq 0 -and $needPoint) { $ctx.hw = [CU.Core]::RootAt($ctx.sx, $ctx.sy) }
  if ($ctx.hw -eq 0) { Fail "ERR_NO_WINDOW" "no target window - pass -Title/-Proc/-Hwnd or snap a window first" }
  return $ctx
}

# ------------------------------------------------------------------ UIA elements (numbered marks, -Id, -Name, snap-to-control)
function UiaCachePath($framePath) { return [IO.Path]::ChangeExtension($framePath, ".uia.tsv") }
function ElJson($e) { return '{"id":' + $e.id + ',"type":' + (Q $e.type) + ',"name":' + (Q $e.name) + '}' }
function Get-CachedEls($a, $fr) {
  $fp = Get-FramePath $a
  $hdr = $null
  $l = [CU.Uia]::LoadCache((UiaCachePath $fp), [ref]$hdr)
  if ($null -eq $l -or $null -eq $fr) { return $null }
  if ($hdr[0] -ne $fr.hwnd -or $hdr[1] -ne $fr.ts) { return $null }      # cache belongs to an older frame
  return , $l
}
function Get-CachedEl($a, [int]$id) {
  $fr = Get-Frame $a
  if ($null -eq $fr) { Fail "ERR_NO_FRAME" "-Id needs a frame taken with 'snap -Marks'" }
  $chk = $fr.Check([bool](P $a 'Force' $false)); if ($chk) { Fail $chk "window moved/resized/closed since the marks were taken - snap -Marks again" }
  $l = Get-CachedEls $a $fr
  if ($null -eq $l) { Fail "ERR_NO_MARKS" "no element list for the current frame - run 'snap -Marks' first" }
  $el = [CU.Uia]::ById($l, $id)
  if ($null -eq $el) { Fail "ERR_NO_ELEMENT" ("no element #" + $id + " in the current marks (" + $l.Count + " elements)") }
  return $el
}
# -Name: live UIA lookup in the target window, falls back to OCR text search when UIA has nothing
function Find-ByName($a) {
  $q = Get-Text $a 'Name' 'NameB64' $null
  $hw = [int64](Resolve-Window $a)
  $fr = Get-Frame $a
  if ($hw -eq 0 -and $null -ne $fr) { $hw = $fr.hwnd }
  if ($hw -eq 0) { Fail "ERR_NO_WINDOW" "-Name needs -Title/-Proc/-Hwnd or a frame" }
  $l = [CU.Uia]::Collect($hw, [int](P $a 'UiaTimeout' 1500), $true, 1500)
  $st = [CU.Uia]::LastStatus
  $hits = [CU.Uia]::ByName($l, $q)
  $i = [int](P $a 'Index' 1)
  if ($hits.Count -ge $i) {
    $e = $hits[$i - 1]
    return @{ x = $e.Cx; y = $e.Cy; screen = $true; el = $e; note = ',"via":"uia","el":' + (ElJson $e) + ',"uia_ms":' + [CU.Uia]::LastMs }
  }
  $b = @{}; foreach ($k in @($a.get_Keys())) { $b[$k] = $a[$k] }
  $b['Find'] = $q; $b.Remove('FindB64')
  try { $hit = Find-Text $b }
  catch { Fail "ERR_TEXT_NOT_FOUND" ("'" + $q + "' not found: UIA " + $st + " (" + $l.Count + " elements), OCR no match") }
  return @{ x = $hit.cx; y = $hit.cy; screen = ($script:ocrSpace -eq 'screen'); el = $null; note = ',"via":"ocr","uia":' + (Q ($st + "/" + $l.Count)) + ',"found":' + (Q $hit.text) }
}
# frame-coordinate click: if an element list exists for this frame, keep points inside an element,
# pull near-misses (within ~6 image px) onto the nearest control's centre
function Invoke-SnapTo($a, $ctx) {
  $l = Get-CachedEls $a $ctx.frame
  if ($null -eq $l -or $l.Count -eq 0) { return }
  $tol = [int][Math]::Ceiling(6.0 / $ctx.frame.s)
  $inside = $false
  $e = [CU.Uia]::SnapTarget($l, $ctx.sx, $ctx.sy, $tol, [ref]$inside)
  if ($null -eq $e) { return }
  if ($inside) { $ctx.note = ',"on":' + (ElJson $e); return }
  $ctx.sx = $e.Cx; $ctx.sy = $e.Cy
  $ctx.note = ',"snapped_to":' + (ElJson $e)
}
function Invoke-Marks($a, $snapJson) {
  $fr = [CU.Frame]::Parse($snapJson)
  $fp = [IO.Path]::ChangeExtension($fr.img, ".frame.json")
  $l = [CU.Uia]::Collect([int64]$fr.hwnd, [int](P $a 'UiaTimeout' 1500), [bool](P $a 'Text2' $false), 400)
  $st = [CU.Uia]::LastStatus; $ms = [CU.Uia]::LastMs
  [CU.Uia]::SaveCache((UiaCachePath $fp), $fr, $l)
  [CU.Uia]::SaveCache((UiaCachePath $script:lastFrame), $fr, $l)
  $frag = '"uia":{"status":' + (Q $st) + ',"ms":' + $ms + ',"count":' + $l.Count + '}'
  if ($l.Count -gt 0) {
    if ([bool](P $a 'Marks' $false)) {
      $mi = [IO.Path]::Combine([IO.Path]::GetDirectoryName($fr.img), [IO.Path]::GetFileNameWithoutExtension($fr.img) + "-marks" + [IO.Path]::GetExtension($fr.img))
      $r = [CU.Uia]::Som($fr.img, $mi, [CU.Uia]::Boxes($l, $fr))
      if ($r -eq "OK") { $frag += ',"marks_img":' + (Q $mi) }
    }
    $frag += ',"elements":' + [CU.Uia]::ListJson($l, $fr)
  }
  return Merge $snapJson $frag
}

# ------------------------------------------------------------------ snap
function Invoke-Snap($a, [int64]$hw, [bool]$updateLast) {
  $out = P $a 'Out' (Join-Path $script:state "cur.jpg")
  if (-not [IO.Path]::IsPathRooted($out)) { $out = Join-Path (Get-Location) $out }
  $hasR = $false; $rl = 0; $rt = 0; $rr = 0; $rb = 0
  if (Has $a 'Region') {
    $v = @("$($a.Region)" -split '[, ]+' | Where-Object { $_ -ne "" } | ForEach-Object { [double]$_ })
    if ($v.Count -ne 4) { Fail "ERR_ARGS" "-Region wants x,y,w,h" }
    if ([bool](P $a 'Screen' $false)) { $rl = [int]$v[0]; $rt = [int]$v[1]; $rr = [int]($v[0] + $v[2]); $rb = [int]($v[1] + $v[3]) }
    else {
      $fr = Get-Frame $a
      if ($null -eq $fr) { Fail "ERR_NO_FRAME" "-Region is in frame image coords; snap first or add -Screen" }
      $chk = $fr.Check([bool](P $a 'Force' $false)); if ($chk) { Fail $chk "frame is stale - snap again" }
      if ($hw -eq 0) { $hw = $fr.hwnd }
      $q = $fr.RegionToScreen($v[0], $v[1], $v[2], $v[3]); $rl = $q[0]; $rt = $q[1]; $rr = $q[2]; $rb = $q[3]
    }
    $hasR = $true
  }
  $ms = [int](P $a 'MaxSide' -1); if ($ms -lt 0) { $ms = $script:cfg.MaxSide }
  $mp = [int](P $a 'MaxPixels' -1); if ($mp -lt 0) { $mp = $script:cfg.MaxPixels }
  $lf = if ($updateLast) { $script:lastFrame } else { "" }
  $j = [CU.Core]::Snap($hw, $hasR, $rl, $rt, $rr, $rb, $ms, $mp, [double](P $a 'Scale' 0), $script:cfg.Cap,
                        [int](P $a 'Grid' 0), $out, [int](P $a 'Quality' 85), (P $a 'Method' "auto"), [bool](P $a 'Restore' $false), $lf)
  if (IsErr $j) { return $j }
  $r = '{"ok":true,"frame":' + $j + $(if ($script:findInfo) { "," + $script:findInfo } else { "" }) + '}'
  if ($updateLast) {
    if ([bool](P $a 'Marks' $false) -or [bool](P $a 'Uia' $false)) { $r = Invoke-Marks $a $r }
    else { Remove-Item (UiaCachePath $script:lastFrame) -ErrorAction SilentlyContinue }
  }
  return $r
}

function Invoke-Zoom($a) {
  $fr = Get-Frame $a
  if ($null -eq $fr) { Fail "ERR_NO_FRAME" "zoom needs a frame - snap first" }
  if (-not (Has $a 'X') -or -not (Has $a 'Y')) { Fail "ERR_ARGS" "zoom needs -X -Y (centre, in frame image coords)" }
  $chk = $fr.Check([bool](P $a 'Force' $false)); if ($chk) { Fail $chk "frame is stale - snap again" }
  $rad = [double](P $a 'R' 120)
  $q = $fr.RegionToScreen([double]$a.X - $rad, [double]$a.Y - $rad, 2 * $rad, 2 * $rad)
  $pw = [Math]::Max(1, $q[2] - $q[0]); $ph = [Math]::Max(1, $q[3] - $q[1])
  $sc = [Math]::Min(6.0, [Math]::Max(1.0, [double](P $a 'ZoomSize' 900) / [Math]::Max($pw, $ph)))
  $b = @{}; foreach ($k in @($a.get_Keys())) { $b[$k] = $a[$k] }
  $b['Region'] = "$($q[0]),$($q[1]),$pw,$ph"; $b['Screen'] = $true; $b['Scale'] = $sc
  if (-not (Has $a 'Out')) { $b['Out'] = Join-Path $script:state "zoom.png" }
  if (-not (Has $a 'Grid')) { $b['Grid'] = 50 }
  return Invoke-Snap $b ([int64]$fr.hwnd) $true
}

# ------------------------------------------------------------------ OCR (Windows.Media.Ocr)
function Initialize-Ocr {
  if ($script:ocrReady) { return }
  Add-Type -AssemblyName System.Runtime.WindowsRuntime
  $null = [Windows.Storage.StorageFile,Windows.Storage,ContentType=WindowsRuntime]
  $null = [Windows.Media.Ocr.OcrEngine,Windows.Foundation,ContentType=WindowsRuntime]
  $null = [Windows.Graphics.Imaging.BitmapDecoder,Windows.Graphics,ContentType=WindowsRuntime]
  $null = [Windows.Graphics.Imaging.SoftwareBitmap,Windows.Graphics,ContentType=WindowsRuntime]
  $null = [Windows.Graphics.Imaging.BitmapPixelFormat,Windows.Graphics,ContentType=WindowsRuntime]
  $null = [Windows.Graphics.Imaging.BitmapAlphaMode,Windows.Graphics,ContentType=WindowsRuntime]
  $script:asTask = ([System.WindowsRuntimeSystemExtensions].GetMethods() | Where-Object { $_.Name -eq 'AsTask' -and $_.GetParameters().Count -eq 1 -and $_.GetParameters()[0].ParameterType.Name -eq 'IAsyncOperation`1' })[0]
  $script:ocrEngine = [Windows.Media.Ocr.OcrEngine]::TryCreateFromUserProfileLanguages()
  if (-not $script:ocrEngine) { Fail "ERR_NO_OCR" "no Windows OCR language pack installed" }
  $script:ocrReady = $true
}
function Await($t, $rt) { $m = $script:asTask.MakeGenericMethod($rt); $nt = $m.Invoke($null, @($t)); $nt.Wait(-1) | Out-Null; return $nt.Result }

# returns @{ lines = [ @{text; words=[@{t;x;y;w;h}]} ]; map = scriptblock(x,y)->(fx,fy) ; k = factor }
function Get-Ocr($a) {
  Initialize-Ocr
  $mapK = 1.0; $fr = $null; $cap = $null; $res = $null; $pf = 1.0
  if (Has $a 'Path') {
    $img = (Resolve-Path $a.Path).Path
    if ($script:ocrPrep) {
      $pp = Join-Path $script:state "_ocr_prep.png"
      $pf = 2.0
      $r = [CU.Core]::Prep($img, $pp, $pf, [int](15 * $pf)); if ($r -ne "OK") { Fail "ERR_OCR" $r }
      $img = $pp
    }
    $file = Await ([Windows.Storage.StorageFile]::GetFileFromPathAsync($img)) ([Windows.Storage.StorageFile])
    $stream = Await ($file.OpenAsync([Windows.Storage.FileAccessMode]::Read)) ([Windows.Storage.Streams.IRandomAccessStream])
    $dec = Await ([Windows.Graphics.Imaging.BitmapDecoder]::CreateAsync($stream)) ([Windows.Graphics.Imaging.BitmapDecoder])
    $bmp = Await ($dec.GetSoftwareBitmapAsync()) ([Windows.Graphics.Imaging.SoftwareBitmap])
    $res = Await ($script:ocrEngine.RecognizeAsync($bmp)) ([Windows.Media.Ocr.OcrResult])
    $stream.Dispose()
  }
  else {
    # fresh full-resolution capture of the same area as the reference frame (better OCR than the downscaled image),
    # kept in memory: no PNG encode/decode round trip (was 150-400 ms per pass)
    $fr = Get-Frame $a
    $hw = [int64](Resolve-Window $a)
    if ($hw -eq 0 -and $null -ne $fr) { $hw = $fr.hwnd }
    $hasR = $false; $rl = 0; $rt = 0; $rr = 0; $rb = 0; $sc = 1.0
    if ($null -ne $fr -and ($hw -eq $fr.hwnd)) {
      $chk = $fr.Check([bool](P $a 'Force' $false)); if ($chk) { Fail $chk "frame is stale - snap again" }
      $w = [int][Math]::Ceiling($fr.w / $fr.s); $h = [int][Math]::Ceiling($fr.h / $fr.s)
      $rl = [int]$fr.ox; $rt = [int]$fr.oy; $rr = $rl + $w; $rb = $rt + $h; $hasR = $true
      if (Has $a 'Region') {   # OCR only part of the frame (frame image coords x,y,w,h): faster + fewer false hits
        $v = @("$($a.Region)" -split '[, ]+' | Where-Object { $_ -ne "" } | ForEach-Object { [double]$_ })
        if ($v.Count -ne 4) { Fail "ERR_ARGS" "-Region wants x,y,w,h" }
        $q = $fr.RegionToScreen($v[0], $v[1], $v[2], $v[3]); $rl = $q[0]; $rt = $q[1]; $rr = $q[2]; $rb = $q[3]; $w = $rr - $rl; $h = $rb - $rt
      }
      if ($w * $h -gt 9000000) { $sc = [Math]::Sqrt(9000000.0 / ($w * $h)) }
    } else { $fr = $null }
    [int]$ox = 0; [int]$oy = 0; [double]$cs = 1.0; [string]$used = ""; [string]$cerr = ""
    $bm = [CU.Core]::SnapMem($hw, $hasR, $rl, $rt, $rr, $rb, $sc, (P $a 'Method' 'auto'), $false, [ref]$ox, [ref]$oy, [ref]$cs, [ref]$used, [ref]$cerr)
    if ($null -eq $bm) { Fail "ERR_CAPTURE" ($cerr + " (" + $used + ")") }
    try {
      $cap = @{ ox = $ox; oy = $oy; s = $cs; w = $bm.Width; h = $bm.Height }
      if ($script:ocrPrep) {
        # 2x upscale for tiny regions (a 200%-DPI capture already has large glyphs) + adaptive binarisation
        $mx = [Math]::Max([double]$bm.Width, [double]$bm.Height)
        $pf = if ($mx -le 800) { 2.0 } else { 1.0 }
        $pb = [CU.Core]::PrepMem($bm, $pf, [int](15 * $pf)); $bm.Dispose(); $bm = $pb
      }
      $bytes = [CU.Core]::Bgra($bm); $bw = $bm.Width; $bh = $bm.Height
    } finally { if ($bm) { $bm.Dispose() } }
    # same pixels as the previous pass (wait -Find polling, repeated find on one screen) -> reuse the OCR result
    $key = "$hw|$rl,$rt,$rr,$rb|$($script:ocrPrep)|${bw}x$bh|" + [CU.Core]::QuickHash($bytes)
    if ($script:ocrCache -and $script:ocrCache.key -eq $key) { $res = $script:ocrCache.res; $script:ocrCached = $true }
    else {
      $buf = [System.Runtime.InteropServices.WindowsRuntime.WindowsRuntimeBufferExtensions]::AsBuffer($bytes)
      $sb = [Windows.Graphics.Imaging.SoftwareBitmap]::CreateCopyFromBuffer($buf, [Windows.Graphics.Imaging.BitmapPixelFormat]::Bgra8, $bw, $bh, [Windows.Graphics.Imaging.BitmapAlphaMode]::Ignore)
      try { $res = Await ($script:ocrEngine.RecognizeAsync($sb)) ([Windows.Media.Ocr.OcrResult]) } finally { $sb.Dispose() }
      $script:ocrCache = @{ key = $key; res = $res }; $script:ocrCached = $false
    }
  }
  # map OCR pixel -> reference frame image coords
  if ($null -ne $cap -and $null -ne $fr) { $k = $fr.s / $cap.s / $pf; $offx = ($cap.ox - $fr.ox) * $fr.s; $offy = ($cap.oy - $fr.oy) * $fr.s }
  elseif ($null -ne $cap) { $k = 1.0 / $cap.s / $pf; $offx = $cap.ox; $offy = $cap.oy; $script:ocrSpace = "screen" }
  else { $k = 1.0 / $pf; $offx = 0; $offy = 0; $script:ocrSpace = "image" }
  if ($null -ne $fr) { $script:ocrSpace = "frame" }
  $lines = @()
  foreach ($ln in $res.Lines) {
    $ws = @()
    foreach ($wd in $ln.Words) {
      $rc = $wd.BoundingRect
      $ws += , @{ t = $wd.Text; x = $offx + $rc.X * $k; y = $offy + $rc.Y * $k; w = $rc.Width * $k; h = $rc.Height * $k }
    }
    if ($ws.Count) { $lines += , @{ text = $ln.Text; words = $ws } }
  }
  return $lines
}
function Box($ws) {
  $l = 1e9; $t = 1e9; $r = -1e9; $b = -1e9
  foreach ($w in $ws) { $l = [Math]::Min($l, $w.x); $t = [Math]::Min($t, $w.y); $r = [Math]::Max($r, $w.x + $w.w); $b = [Math]::Max($b, $w.y + $w.h) }
  return @{ x = [int]$l; y = [int]$t; w = [int]($r - $l); h = [int]($b - $t); cx = [int](($l + $r) / 2); cy = [int](($t + $b) / 2) }
}
function Norm($s) { return ($s -replace '\s', '').ToLowerInvariant() }

# all matches of the query (whitespace-insensitive: Windows OCR puts spaces between CJK characters)
function Search-Text($lines, $query) {
  $q = Norm $query
  $hits = @()
  foreach ($ln in $lines) {
    $cat = ""; $own = @()
    for ($i = 0; $i -lt $ln.words.Count; $i++) { $n = Norm $ln.words[$i].t; $cat += $n; for ($c = 0; $c -lt $n.Length; $c++) { $own += $i } }
    $pos = $cat.IndexOf($q)
    while ($q.Length -gt 0 -and $pos -ge 0) {
      $w0 = $own[$pos]; $w1 = $own[$pos + $q.Length - 1]
      $bx = Box ($ln.words[$w0..$w1])
      $bx.text = $ln.text
      $hits += , $bx
      $pos = $cat.IndexOf($q, $pos + 1)
    }
  }
  return $hits
}
# normal OCR pass; only if nothing was found, a pre-processed pass (2x + adaptive binarisation, handles
# white-on-colour buttons, dark mode, small text)
function Search-Hits($a, $q) {
  $script:ocrPrep = $false; $script:ocrPass = "plain"
  $hits = @(Search-Text (Get-Ocr $a) $q)
  if ($hits.Count -eq 0) {
    $b = @{}; foreach ($k in @($a.get_Keys())) { $b[$k] = $a[$k] }
    $script:ocrPrep = $true; $script:ocrPass = "prep"
    try { $hits = @(Search-Text (Get-Ocr $b) $q) } finally { $script:ocrPrep = $false }
  }
  return $hits
}
function Find-Text($a) {
  $q = Get-Text $a 'Find' 'FindB64' $null
  if (-not $q) { Fail "ERR_ARGS" "need -Find or -FindB64" }
  $hits = @(Search-Hits $a $q)
  $i = [int](P $a 'Index' 1)
  if ($hits.Count -lt $i) { Fail "ERR_TEXT_NOT_FOUND" ("'" + $q + "' not found on screen (OCR); found " + $hits.Count + " match(es)") }
  return $hits[$i - 1]
}
function HitJson($h) { return '{"text":' + (Q $h.text) + ',"x":' + $h.x + ',"y":' + $h.y + ',"w":' + $h.w + ',"h":' + $h.h + ',"cx":' + $h.cx + ',"cy":' + $h.cy + '}' }

# ------------------------------------------------------------------ post-action: settle + optional after-snap
function Complete-Action($a, $ctx, $json, $before) {
  if (IsErr $json) { if ($null -ne $before) { Close-Before $before }; return $json }
  if ($ctx.note) { $json = Merge $json ($ctx.note.TrimStart(',')) }
  if ($null -ne $before) {
    $json = Merge $json ([CU.Core]::Settle($ctx.hw, $before.full, $before.roi, $before.sx, $before.sy, [int](P $a 'Settle' 1000), [int](P $a 'Quiet' 400)))
    Close-Before $before
  }
  if ($null -ne $script:verifyText) {
    $st = ""; $pid0 = 0; if ($null -ne $ctx.frame) { $pid0 = $ctx.frame.pid }
    $v = [CU.Uia]::FocusedText($pid0, 800, [ref]$st)
    $frag = '"verify":{"source":' + (Q $st)
    if ($null -ne $v) {
      $has = (Norm $v).Contains((Norm $script:verifyText))
      $shown = if ($v.Length -gt 200) { "..." + $v.Substring($v.Length - 200) } else { $v }
      $frag += ',"contains":' + $(if ($has) { 'true' } else { 'false' }) + ',"value":' + (Q $shown)
    }
    $json = Merge $json ($frag + '}')
    $script:verifyText = $null
  }
  if ([bool](P $a 'Snap' $false)) {
    $b = @{}; foreach ($k in @('Out', 'MaxSide', 'MaxPixels', 'Quality', 'Grid', 'Method', 'Marks', 'Uia', 'Text2', 'UiaTimeout')) { if (Has $a $k) { $b[$k] = $a[$k] } }
    $s = Invoke-Snap $b ([int64]$ctx.hw) $true
    if (IsErr $s) { $json = Merge $json ('"after_err":' + $s) } else { $json = Merge $json ('"after":' + $s) }
  }
  return $json
}
function Close-Before($b) { if ($b.full) { $b.full.Dispose() }; if ($b.roi) { $b.roi.Dispose() } }
function Get-Before($a, $ctx) {
  if ([int](P $a 'Settle' 1000) -le 0) { return $null }
  $full = [CU.Core]::Thumb([int64]$ctx.hw)
  if ($null -eq $full) { return $null }
  $roi = $null
  if ($ctx.sx -ne 0 -or $ctx.sy -ne 0) { $roi = [CU.Core]::RoiThumb([int64]$ctx.hw, $ctx.sx, $ctx.sy) }
  return @{ full = $full; roi = $roi; sx = $ctx.sx; sy = $ctx.sy }
}

# ------------------------------------------------------------------ dispatcher
function Invoke-Cu($a) {
  $cmd = ("" + (P $a 'Cmd' 'help')).ToLowerInvariant()
  $script:findInfo = $null
  switch -Regex ($cmd) {
    '^info$' { return [CU.Core]::InfoJson([bool](P $a 'All' $false), (P $a 'Title' "")) }
    '^snap$' { return Invoke-Snap $a ([int64](Resolve-Window $a)) $true }
    '^(els|elements)$' {
      $fr = Get-Frame $a
      $hw = [int64](Resolve-Window $a); if ($hw -eq 0 -and $null -ne $fr) { $hw = $fr.hwnd }
      if ($hw -eq 0) { Fail "ERR_NO_WINDOW" "els needs -Title/-Proc/-Hwnd or a frame" }
      $l = [CU.Uia]::Collect($hw, [int](P $a 'UiaTimeout' 1500), [bool](P $a 'Text2' $false), 400)
      $out = '{"ok":true,"status":' + (Q ([CU.Uia]::LastStatus)) + ',"ms":' + [CU.Uia]::LastMs + ',"count":' + $l.Count
      if ($null -ne $fr -and $fr.hwnd -eq $hw -and -not $fr.Check($false)) { $out += ',"space":"frame","elements":' + [CU.Uia]::ListJson($l, $fr) }
      else {
        $items = @(); foreach ($e in $l) { $items += ('[' + $e.id + ',' + (Q $e.type) + ',' + (Q $e.name) + ',' + $e.Cx + ',' + $e.Cy + ']') }
        $out += ',"space":"screen","elements":[' + ($items -join ',') + ']'
      }
      return $out + '}'
    }
    '^zoom$' { return Invoke-Zoom $a }
    '^frame$' { $f = Get-FramePath $a; if (Test-Path $f) { return '{"ok":true,"frame":' + [IO.File]::ReadAllText($f) + '}' }; return (Err "ERR_NO_FRAME" $f) }
    '^activate$' { return [CU.Core]::Activate([int64](Get-Target $a $false $false).hw) }

    '^(click|double|dbl|rclick|mclick|move|down|up|drag|scroll|hscroll)$' {
      $act = switch ($cmd) { 'dbl' { 'double' } 'rclick' { 'click' } 'mclick' { 'click' } default { $cmd } }
      $btn = switch ($cmd) { 'rclick' { 'right' } 'mclick' { 'middle' } default { P $a 'Button' 'left' } }
      $ctx = Get-Target $a $true ($act -eq 'drag')
      $before = Get-Before $a $ctx
      $mods = ModBits (P $a 'Mods' "")
      if ([bool](P $a 'Fg' $false)) {
        $j = [CU.Core]::FgMouse($ctx.hw, $ctx.sx, $ctx.sy, $btn, $act, $mods, $ctx.sx2, $ctx.sy2, [int](P $a 'Wheel' -3), -not [bool](P $a 'KeepCursor' $false))
      } else {
        $j = [CU.Core]::BgMouse($ctx.hw, $ctx.sx, $ctx.sy, $btn, $act, $mods, $ctx.sx2, $ctx.sy2, [int](P $a 'Wheel' -3))
      }
      return Complete-Action $a $ctx $j $before
    }

    '^key$' {
      $ks = P $a 'Keys' ""
      if (-not $ks) { Fail "ERR_ARGS" "need -Keys, e.g. -Keys ctrl+s  or  -Keys 'ctrl+a delete'" }
      $ctx = Get-Target $a $false $false
      $before = Get-Before $a $ctx
      $res = @()
      foreach ($k in ($ks -split '\s+' | Where-Object { $_ })) {
        if ([bool](P $a 'Fg' $false)) { $j = [CU.Core]::FgKey($ctx.hw, $k, [int](P $a 'Repeat' 1)) }
        else { $j = [CU.Core]::BgKey($ctx.hw, $k, 0, [int](P $a 'Repeat' 1)) }
        if (IsErr $j) { return $j }
        $res += $j
      }
      $j = if ($res.Count -eq 1) { $res[0] } else { '{"ok":true,"seq":[' + ($res -join ',') + ']}' }
      return Complete-Action $a $ctx $j $before
    }

    '^type$' {
      $t = Get-Text $a 'Text' 'TextB64' 'TextFile'
      if ($null -eq $t) { Fail "ERR_ARGS" "need -Text / -TextB64 (UTF-8 base64, safest for non-ASCII) / -TextFile" }
      $havePt = (Has $a 'X') -or (Has $a 'Find') -or (Has $a 'FindB64')
      $ctx = Get-Target $a $havePt $false
      $before = Get-Before $a $ctx
      $fg = [bool](P $a 'Fg' $false)
      $m = ("" + (P $a 'Method' 'auto')).ToLowerInvariant()
      $tgt = 0
      if ($havePt) {   # focus the field first
        if ($fg) { $c = [CU.Core]::FgMouse($ctx.hw, $ctx.sx, $ctx.sy, 'left', 'click', 0, 0, 0, 0, -not [bool](P $a 'KeepCursor' $false)) }
        else { $c = [CU.Core]::BgMouse($ctx.hw, $ctx.sx, $ctx.sy, 'left', 'click', 0, 0, 0, 0); $tgt = [CU.Core]::ChildAt($ctx.hw, $ctx.sx, $ctx.sy) }
        if (IsErr $c) { return $c }
        Start-Sleep -Milliseconds 60
      }
      $useClip = ($m -eq 'clip' -or $m -eq 'paste') -or ($fg -and $m -eq 'auto' -and $t.Length -gt 400)
      if ($useClip) {
        $old = $null; try { $old = Get-Clipboard -Raw -ErrorAction Stop } catch { }
        try { Set-Clipboard -Value $t -ErrorAction Stop } catch { return (Err "ERR_CLIPBOARD" $_.Exception.Message) }
        if ($fg) { $j = [CU.Core]::FgPaste($ctx.hw) } else { $j = [CU.Core]::BgText($ctx.hw, $t, 'paste', $tgt) }
        Start-Sleep -Milliseconds 350
        try { if ($null -ne $old) { Set-Clipboard -Value $old } } catch { }
      } elseif ($fg) { $j = [CU.Core]::FgText($ctx.hw, $t) }
      else { $j = [CU.Core]::BgText($ctx.hw, $t, $m, $tgt) }
      if (IsErr $j) { return $j }
      if ([bool](P $a 'Verify' $false) -and -not [bool](P $a 'Enter' $false)) { $script:verifyText = $t }
      if ([bool](P $a 'Enter' $false)) {
        $e = if ($fg) { [CU.Core]::FgKey($ctx.hw, 'enter', 1) } else { [CU.Core]::BgKey($ctx.hw, 'enter', $tgt, 1) }
        if (IsErr $e) { return $e }
        $j = Merge $j '"enter":true'
      }
      return Complete-Action $a $ctx $j $before
    }

    '^mark$' {
      $fr = $null
      $src = P $a 'Path' ""
      if (-not $src) { $fr = Get-Frame $a; if ($null -eq $fr) { Fail "ERR_NO_FRAME" "need -Path or a frame" }; $src = $fr.img }
      $pts = P $a 'Pts' ""
      if (-not $pts -and (Has $a 'X')) { $pts = "$($a.X):$($a.Y)" }
      if (-not $pts) { Fail "ERR_NO_PTS" "need -Pts 'x:y,x:y' or -X -Y" }
      $dir = [IO.Path]::GetDirectoryName($src); $base = [IO.Path]::GetFileNameWithoutExtension($src)
      $out = P $a 'Out' (Join-Path $dir ($base + "-marked.png"))
      $zo = if ([int](P $a 'Zoom' 24) -gt 0) { Join-Path $dir ($base + "-zoom.png") } else { "" }
      return [CU.Core]::Mark($src, $out, $pts, [int](P $a 'Size' 16), [int](P $a 'Zoom' 24), $zo)
    }

    '^ocr$' {
      $lines = Get-Ocr $a
      $items = @(); foreach ($ln in $lines) { $bx = Box $ln.words; $bx.text = $ln.text; $items += (HitJson $bx) }
      return '{"ok":true,"space":' + (Q $script:ocrSpace) + ',"cached":' + $(if ($script:ocrCached) { 'true' } else { 'false' }) + ',"lines":[' + ($items -join ',') + ']}'
    }
    '^find$' {
      $q = Get-Text $a 'Find' 'FindB64' $null
      if (-not $q) { Fail "ERR_ARGS" "need -Find or -FindB64" }
      $hits = @(Search-Hits $a $q)
      return '{"ok":' + $(if ($hits.Count) { 'true' } else { 'false' }) + ',"space":' + (Q $script:ocrSpace) + ',"pass":' + (Q $script:ocrPass) + ',"cached":' + $(if ($script:ocrCached) { 'true' } else { 'false' }) + ',"query":' + (Q $q) + ',"hits":[' + (($hits | ForEach-Object { HitJson $_ }) -join ',') + ']' + $(if (-not $hits.Count) { ',"err":"ERR_TEXT_NOT_FOUND"' } else { '' }) + '}'
    }

    '^wait$' {
      $sw = [Diagnostics.Stopwatch]::StartNew()
      if ((Has $a 'Find') -or (Has $a 'FindB64')) {
        $q = Get-Text $a 'Find' 'FindB64' $null
        $to = [int](P $a 'Timeout' 8000)
        while ($true) {
          $hits = @(Search-Hits $a $q)
          if ($hits.Count) { return '{"ok":true,"found":' + (HitJson $hits[0]) + ',"ms":' + $sw.ElapsedMilliseconds + '}' }
          if ($sw.ElapsedMilliseconds -ge $to) { return (Err "ERR_TIMEOUT" ("'" + $q + "' did not appear within " + $to + "ms")) }
          Start-Sleep -Milliseconds 250
        }
      }
      if ([bool](P $a 'Stable' $false)) {
        $ctx = Get-Target $a $false $false
        return [CU.Core]::WaitStable($ctx.hw, [int](P $a 'Timeout' 8000))
      }
      Start-Sleep -Milliseconds ([int](P $a 'Ms' 300))
      return '{"ok":true,"ms":' + $sw.ElapsedMilliseconds + '}'
    }

    '^do$' {
      $raw = if (Has $a 'StepsFile') { [IO.File]::ReadAllText($a.StepsFile, [Text.Encoding]::UTF8) } else { P $a 'Steps' "" }
      if (-not $raw) { Fail "ERR_ARGS" "need -Steps '[{""cmd"":""click"",""X"":10,""Y"":20}, ...]' or -StepsFile" }
      $steps = ConvertFrom-Json -InputObject $raw
      $results = @(); $i = 0
      foreach ($s in $steps) {
        $i++
        $b = @{}
        foreach ($k in @('Title', 'Proc', 'Hwnd', 'Fg', 'Force', 'Method', 'KeepCursor', 'NoSnapTo', 'Browser')) { if (Has $a $k) { $b[$k] = $a[$k] } }
        $b['Settle'] = 0                                  # batch default: no settle wait unless the step asks
        foreach ($pp in $s.PSObject.Properties) { $b[$pp.Name] = $pp.Value }
        if ($b.ContainsKey('a') -and -not $b.ContainsKey('Cmd')) { $b['Cmd'] = $b['a'] }
        try { $r = Invoke-Cu $b } catch { $r = ConvertTo-ErrJson $_ }
        $results += $r
        if (IsErr $r) { return '{"ok":false,"err":"ERR_STEP","step":' + $i + ',"results":[' + ($results -join ',') + ']}' }
      }
      return '{"ok":true,"steps":' + $i + ',"results":[' + ($results -join ',') + ']}'
    }

    '^web$' {
      $wsub = ("" + (P $a 'Sub' "")).Trim().ToLowerInvariant()
      $br = ("" + (P $a 'Browser' 'edge')).Trim().ToLowerInvariant()
      if ($br -eq 'microsoftedge' -or $br -eq 'msedge') { $br = 'edge' }
      if ($br -eq 'googlechrome') { $br = 'chrome' }
      if ($br -ne 'edge' -and $br -ne 'chrome') { Fail "ERR_ARGS" "-Browser must be edge or chrome" }
      if (-not $wsub -or $wsub -eq 'help') {
        return '{"ok":true,"help":"web start|stop|status|open|reload|back|fwd|tabs|tab|close|find|els|click|hover|scroll|type|keys|text|val|info|eval|shot|wait  (opts: -Browser edge|chrome -Url -Sel -Text/-Find(-TextB64/-FindB64) -Id -Index -Exact -Method -Js -X/-Y -Wheel -NewTab -Full -Marks -Ready -Stable -Enter -Append -Verify)"}'
      }
      $dtxt = Get-Text $a 'Text' 'TextB64' $null; if (-not $dtxt) { $dtxt = Get-Text $a 'Find' 'FindB64' $null }; if ($null -eq $dtxt) { $dtxt = "" }
      $wid = [int](P $a 'Id' 0)
      $started = $false
      if ($wsub -notin @('stop', 'status', 'start') -and -not [CU.Web]::Running($br)) {
        $rs = [CU.Web]::Start($br, "")
        if (IsErr $rs) { return $rs }
        $started = $true
      }
      $r = $null
      switch -Regex ($wsub) {
        '^start$' { $r = [CU.Web]::Start($br, (P $a 'Url' "")) }
        '^stop$' { $r = [CU.Web]::Stop($br) }
        '^status$' { $r = [CU.Web]::Status($br) }
        '^(open|go|nav)$' {
          $u = P $a 'Url' ""; if (-not $u) { Fail "ERR_ARGS" "web open needs -Url" }
          $to = [int](P $a 'Timeout' 8000)
          $r = if ([bool](P $a 'NewTab' $false)) { [CU.Web]::NewTab($br, $u, $to) } else { [CU.Web]::Navigate($br, $u, $to) }
        }
        '^reload$' { $r = [CU.Web]::Reload($br, [int](P $a 'Timeout' 8000)) }
        '^(back|fwd|forward)$' { $r = [CU.Web]::History($br, $(if ($wsub -eq 'back') { -1 } else { 1 })) }
        '^tabs$' { $r = [CU.Web]::Tabs($br) }
        '^tab$' { $r = [CU.Web]::Tab($br, [int](P $a 'Index' 0), (P $a 'Url' ""), (P $a 'Title' "")) }
        '^close$' { $r = [CU.Web]::CloseTab($br, [int](P $a 'Index' 0), (P $a 'Url' ""), (P $a 'Title' "")) }
        '^click$' {
          if ((Has $a 'X') -or (Has $a 'Y')) {
            $r = [CU.Web]::ClickXY($br, [double](P $a 'X' 0), [double](P $a 'Y' 0), ("" + (P $a 'Button' 'left')).ToLowerInvariant())
          } else {
            if (-not (P $a 'Sel' "") -and -not $dtxt -and $wid -le 0) { Fail "ERR_ARGS" "web click needs -Sel, -Text/-Find, -Id, or -X/-Y" }
            $r = [CU.Web]::Click($br, (P $a 'Sel' ""), $dtxt, [int](P $a 'Index' 1), [bool](P $a 'Exact' $false),
                                 ("" + (P $a 'Method' 'auto')).ToLowerInvariant(), ("" + (P $a 'Button' 'left')).ToLowerInvariant(), $wid)
          }
        }
        '^(find|locate)$' { $r = [CU.Web]::Find($br, (P $a 'Sel' ""), $dtxt, [int](P $a 'Index' 1), [bool](P $a 'Exact' $false), $wid) }
        '^(els|elements)$' { $r = [CU.Web]::Els($br, (P $a 'Sel' ""), [bool](P $a 'All' $false), [int](P $a 'Size' 0)) }
        '^hover$' { $r = [CU.Web]::Hover($br, (P $a 'Sel' ""), $dtxt, [int](P $a 'Index' 1), [bool](P $a 'Exact' $false), $wid) }
        '^scroll$' {
          $wh = 0; if (Has $a 'Wheel' -and -not (Has $a 'Sel') -and -not $dtxt -and $wid -le 0 -and -not (Has $a 'X') -and -not (Has $a 'Y')) { $wh = [int]$a['Wheel'] }
          $r = [CU.Web]::Scroll($br, (P $a 'Sel' ""), $dtxt, [int](P $a 'Index' 1), [bool](P $a 'Exact' $false), $wid, [double](P $a 'X' 0), [double](P $a 'Y' 0), $wh)
        }
        '^type$' {
          $t = Get-Text $a 'Text' 'TextB64' 'TextFile'
          if ($null -eq $t) { Fail "ERR_ARGS" "web type needs -Text / -TextB64 / -TextFile" }
          $r = [CU.Web]::Type($br, (P $a 'Sel' ""), $t, [bool](P $a 'Append' $false), [bool](P $a 'Enter' $false), [bool](P $a 'Verify' $false),
                              ("" + (P $a 'Method' 'auto')).ToLowerInvariant(), $wid)
        }
        '^keys$' {
          $ks = P $a 'Keys' ""; if (-not $ks) { Fail "ERR_ARGS" "web keys needs -Keys" }
          $r = [CU.Web]::Keys($br, $ks, [int](P $a 'Repeat' 1), (P $a 'Sel' ""), $wid)
        }
        '^(text|txt)$' { $r = [CU.Web]::Text($br, (P $a 'Sel' "")) }
        '^(val|value)$' { $r = [CU.Web]::Value($br, (P $a 'Sel' ""), $wid) }
        '^info$' { $r = [CU.Web]::Info($br) }
        '^eval$' {
          $js = P $a 'Js' ""; if (-not $js) { Fail "ERR_ARGS" "web eval needs -Js" }
          $r = [CU.Web]::Eval($br, $js, [int](P $a 'Timeout' 10000))
        }
        '^shot$' {
          $mx = [int](P $a 'MaxSide' -1); if ($mx -le 0) { $mx = 1568 }
          $so = P $a 'Out' ""; if (-not $so) { $so = Join-Path $script:state "web.jpg" }
          $r = [CU.Web]::Shot($br, $so, [bool](P $a 'Full' $false), [int](P $a 'Quality' 85), $mx, [bool](P $a 'Marks' $false), (P $a 'Sel' ""))
        }
        '^wait$' {
          $stable = 0; if ([bool](P $a 'Stable' $false)) { $stable = [int](P $a 'Ms' 0); if ($stable -le 0) { $stable = 500 } }
          $r = [CU.Web]::Wait($br, (P $a 'Sel' ""), $dtxt, (P $a 'Url' ""), [bool](P $a 'Ready' $false),
                              [int](P $a 'Timeout' 8000), [int](P $a 'Ms' 300), $stable, "")
        }
        default { Fail "ERR_ARGS" ("unknown web sub: '" + $wsub + "' - run bare 'web' for the list") }
      }
      if ($started -and -not (IsErr $r)) { $r = Merge $r '"started":true' }
      return $r
    }

    default {
      return '{"ok":true,"help":"cmds: info snap zoom click double rclick mclick move down up drag scroll hscroll key type mark ocr find wait activate frame els do web - see SKILL.md"}'
    }
  }
}

function ConvertTo-ErrJson($e) {
  $m = $e.Exception.Message
  if ($m -like "CUERR|*") { $p = $m.Split('|', 3); return (Err $p[1] $p[2]) }
  return (Err "ERR_EXCEPTION" ($m + " @" + $e.InvocationInfo.ScriptLineNumber))
}

# ------------------------------------------------------------------ daemon (used by cu.exe)
# argv strings -> the same hashtable the CLI produces (re-uses this script's own param block)
function Get-ArgParser {
  if (-not $script:argParser) {
    $pb = $script:selfAst.ParamBlock.Extent.Text
    $script:argParser = [scriptblock]::Create($pb + "`n" + '$h = @{}; foreach ($k in @($PSBoundParameters.get_Keys())) { $v = $PSBoundParameters[$k]; if ($v -is [System.Management.Automation.SwitchParameter]) { $v = $v.IsPresent }; $h[$k] = $v }; $h[''Cmd''] = $Cmd; return $h')
  }
  return $script:argParser
}
# argv -> "& $args[0] 'info' -Title $args[1]": parameter names stay bare, every value travels through $args, so the
# compiled scriptblock only depends on the argument *shape* and is cached (no per-request scriptblock compile)
function Invoke-Parse($parser, [string[]]$argv) {
  if (-not $script:paramNames) { $script:paramNames = @{}; foreach ($pp in $script:selfAst.ParamBlock.Parameters) { $script:paramNames[$pp.Name.VariablePath.UserPath.ToLowerInvariant()] = 1 } }
  if (-not $script:parseCache) { $script:parseCache = @{} }
  $sb = New-Object System.Text.StringBuilder('& $args[0]')
  $vals = New-Object System.Collections.ArrayList
  [void]$vals.Add($parser)
  foreach ($x in $argv) {
    if ($x -match '^-([A-Za-z][A-Za-z0-9]*)(:.*)?$' -and $script:paramNames.ContainsKey($Matches[1].ToLowerInvariant())) {
      if ($Matches[2]) { [void]$vals.Add($Matches[2].Substring(1)); [void]$sb.Append(' -' + $Matches[1] + ':$args[' + ($vals.Count - 1) + ']') }
      else { [void]$sb.Append(' -' + $Matches[1]) }
    } else { [void]$vals.Add($x); [void]$sb.Append(' $args[' + ($vals.Count - 1) + ']') }
  }
  $key = $sb.ToString()
  $blk = $script:parseCache[$key]
  if (-not $blk) { $blk = [scriptblock]::Create($key); if ($script:parseCache.Count -gt 300) { $script:parseCache.Clear() }; $script:parseCache[$key] = $blk }
  return (& $blk @vals)
}
function Invoke-Serve([string]$name, [int]$idleMin) {
  if (-not $name) { Fail "ERR_ARGS" "serve needs -Pipe name" }
  if ($idleMin -le 0) { $idleMin = if ($env:CU_IDLE) { [int]$env:CU_IDLE } else { 20 } }
  $me = [System.Security.Principal.WindowsIdentity]::GetCurrent().User
  $sec = New-Object System.IO.Pipes.PipeSecurity
  $sec.AddAccessRule((New-Object System.IO.Pipes.PipeAccessRule($me, [System.IO.Pipes.PipeAccessRights]::FullControl, [System.Security.AccessControl.AccessControlType]::Allow)))
  $enc = New-Object System.Text.UTF8Encoding($false)
  $parser = Get-ArgParser
  $mx = New-Object System.Threading.Mutex($false, "Local\$name-serve")
  if (-not $mx.WaitOne(0)) { return }                      # another daemon already serves this pipe
  try {
    while ($true) {
      $srv = New-Object System.IO.Pipes.NamedPipeServerStream($name, [System.IO.Pipes.PipeDirection]::InOut, 1,
               [System.IO.Pipes.PipeTransmissionMode]::Byte, [System.IO.Pipes.PipeOptions]::Asynchronous, 65536, 65536, $sec)
      $ar = $srv.BeginWaitForConnection($null, $null)
      if (-not $ar.AsyncWaitHandle.WaitOne($idleMin * 60000)) { $srv.Dispose(); break }
      $stop = $false
      try {
        $srv.EndWaitForConnection($ar)
        $rd = New-Object System.IO.StreamReader($srv, $enc, $false, 65536, $true)
        $line = $rd.ReadLine()
        $res = $null
        if ($line) {
          try {
            $req = ConvertFrom-Json -InputObject $line
            $argv = @($req.argv | ForEach-Object { [string]$_ })
            if ($argv.Count -gt 0 -and $argv[0] -eq '__stop') { $stop = $true; $res = '{"ok":true,"daemon":"stopping"}' }
            else {
              if ($req.cwd -and (Test-Path -LiteralPath $req.cwd)) { Set-Location -LiteralPath $req.cwd; [Environment]::CurrentDirectory = $req.cwd }
              $script:verifyText = $null; $script:ocrPrep = $false; $script:ocrCached = $false
              $h = Invoke-Parse $parser $argv
              if ($h['Cmd'] -eq 'serve') { $res = (Err "ERR_ARGS" "nested serve") } else { $res = Invoke-Cu $h }
            }
          } catch { $res = ConvertTo-ErrJson $_ }
        }
        if ($null -eq $res) { $res = (Err "ERR_DAEMON" "empty request") }
        $b = $enc.GetBytes(([string]$res).Replace("`r", "").Replace("`n", " ") + "`n")
        $srv.Write($b, 0, $b.Length); $srv.Flush()
        try { $srv.WaitForPipeDrain() } catch { }
      } catch { } finally { $srv.Dispose() }
      if ($stop) { break }
      $script:served = [int]$script:served + 1
      if (($script:served % 25) -eq 0) { [GC]::Collect() }   # a blocking full GC per request cost 10-40 ms; do it occasionally
    }
  } finally { $mx.ReleaseMutex(); $mx.Dispose() }
}

# ------------------------------------------------------------------ main
$args0 = @{}
foreach ($k in @($PSBoundParameters.get_Keys())) { $v = $PSBoundParameters[$k]; if ($v -is [System.Management.Automation.SwitchParameter]) { $v = $v.IsPresent }; $args0[$k] = $v }
$args0['Cmd'] = $Cmd
$script:selfAst = $MyInvocation.MyCommand.ScriptBlock.Ast
try {
  Import-Core
  if ($Cmd -eq 'serve') { Invoke-Serve $Pipe $Idle; exit 0 }
  $result = Invoke-Cu $args0
} catch {
  if ('CU.J' -as [type]) { $result = ConvertTo-ErrJson $_ }
  else { $result = '{"ok":false,"err":"ERR_LOAD","msg":"' + ($_.Exception.Message -replace '["\\]', "'" -replace '[\r\n]+', ' ') + '"}' }
}
[Console]::Out.WriteLine($result)
if ($result.StartsWith('{"ok":false')) { exit 1 }
exit 0
