# build.ps1 - reproducible build of the native artifacts (run from anywhere):
#   client.cs            -> cu.exe                    (thin named-pipe front end)
#   cu.cs+uia.cs+web.cs  -> bin\cu-<hash>.dll         (C# core; same hash scheme as cu.ps1 Import-Core)
#
#   -Verify : only check that bin\ has a DLL matching the current sources (release/CI check)
#
# The core DLL is committed so the first call on a fresh install skips the 3-8 s csc compile. If the sources
# change, the hash changes and the runtime simply compiles on the fly - a stale committed DLL is never loaded.
param([switch]$Verify)
$ErrorActionPreference = "Stop"
$here = Split-Path -Parent $MyInvocation.MyCommand.Path
$fw = Join-Path $env:WINDIR "Microsoft.NET\Framework64\v4.0.30319"
if (-not (Test-Path $fw)) { $fw = Join-Path $env:WINDIR "Microsoft.NET\Framework\v4.0.30319" }
$csc = Join-Path $fw "csc.exe"
$wpf = Join-Path $fw "WPF"
$refs = @("System.Drawing.dll", "System.Web.Extensions.dll", "System.Management.dll",
          (Join-Path $wpf "UIAutomationClient.dll"), (Join-Path $wpf "UIAutomationTypes.dll"), (Join-Path $wpf "WindowsBase.dll"))
$core = @((Join-Path $here "cu.cs"), (Join-Path $here "uia.cs"), (Join-Path $here "web.cs"))

# identical to cu.ps1 Import-Core: MD5 of the "|"-joined per-file MD5s, first 12 hex chars
$joined = (($core | ForEach-Object { (Get-FileHash $_ -Algorithm MD5).Hash }) -join '|')
$hash = ([BitConverter]::ToString([Security.Cryptography.MD5]::Create().ComputeHash([Text.Encoding]::UTF8.GetBytes($joined)))).Replace('-', '').ToLowerInvariant().Substring(0, 12)
$bin = Join-Path $here "bin"
$dll = Join-Path $bin "cu-$hash.dll"

if ($Verify) {
    $ok = Test-Path $dll
    Write-Host ("core DLL for the current sources: cu-$hash.dll -> " + $(if ($ok) { "present" } else { "MISSING (run build.ps1)" }))
    if (-not $ok) { exit 1 }
    exit 0
}

if (-not (Test-Path $csc)) { throw "csc.exe not found under $fw (needs .NET Framework 4.x)" }
# a running daemon keeps the old DLL loaded and locked: stop it first (best effort, same-user daemons only)
try {
    Get-CimInstance Win32_Process -Filter "Name='powershell.exe'" |
        Where-Object { $_.CommandLine -like "*cu.ps1*serve*" } |
        ForEach-Object { try { Stop-Process -Id $_.ProcessId -Force; Write-Host ("stopped daemon pid " + $_.ProcessId) } catch { } }
    Start-Sleep -Milliseconds 300
} catch { }
New-Item -ItemType Directory -Force -Path $bin | Out-Null
Get-ChildItem $bin -Filter "cu-*.dll" -ErrorAction SilentlyContinue |
    Where-Object { $_.Name -ne "cu-$hash.dll" } | ForEach-Object { Remove-Item $_.FullName -Force }

& $csc -nologo -target:library -optimize "-out:$dll" ($refs | ForEach-Object { "-r:$_" }) $core
if ($LASTEXITCODE -ne 0) { throw "csc failed (core)" }
& $csc -nologo -target:exe -optimize "-out:$(Join-Path $here 'cu.exe')" -r:System.Management.dll (Join-Path $here "client.cs")
if ($LASTEXITCODE -ne 0) { throw "csc failed (client)" }

Write-Host ("built core   : " + $dll)
Write-Host ("built client : " + (Join-Path $here "cu.exe"))