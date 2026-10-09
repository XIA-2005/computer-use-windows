#!/usr/bin/env bash
# Windows/Git Bash E2E regression: isolated headless browser sessions only.
# Run from ShunCode Bash. Never touches the daily browser/profile.
set -euo pipefail

bench="$(cd -- "$(dirname -- "$0")" && pwd -P)"
root="$(cd -- "$bench/../.." && pwd -P)"
cu="$root/win/cu.exe"
fixture="file:///$(cygpath -m "$bench/page.html")"
if [[ -z "$(printenv CU_STATE 2>/dev/null || true)" ]]; then
  export CU_STATE="$(cygpath -w "$LOCALAPPDATA/ShunCode/ComputerUse/state")"
fi
export CU_HEADLESS=1

test_id="cu-e2e-$(date +%Y%m%d%H%M%S)-$$"
session_a="$test_id-A"
session_b="$test_id-B"
session_conflict="$test_id-conflict"
server_pid=""
listener_log=""
cleanup() {
  if [[ -n "$server_pid" ]]; then kill "$server_pid" 2>/dev/null || true; wait "$server_pid" 2>/dev/null || true; fi
  if [[ -n "$listener_log" ]]; then rm -f -- "$listener_log"; fi
  export CU_NODAEMON=1
  for session in "$session_a" "$session_b"; do
    export CU_SESSION="$session"
    "$cu" web stop >/dev/null 2>&1 || true
  done
}
trap cleanup EXIT

assert_contains() {
  local reply="$1" token="$2"
  if [[ "$reply" != *"$token"* ]]; then
    printf 'FAIL: expected [%s], got [%s]\n' "$token" "$reply" >&2
    exit 1
  fi
}

export CU_SESSION="$session_a" CU_NODAEMON=1
assert_contains "$("$cu" web start)" '"started":true'
unset CU_NODAEMON
assert_contains "$("$cu" web open -Url "$fixture")" '"ok":true'
assert_contains "$("$cu" web type -Sel '#q' -Text 'isolated_A' -Verify)" '"verify":true'
assert_contains "$("$cu" web val -Sel '#q')" '"isolated_A"'
assert_contains "$("$cu" web click -Sel '#b1')" '"verified":true'
assert_contains "$("$cu" web eval -Js 'window.__last')" '"b1 click"'
printf '%s\n' 'PASS: session A navigation/input/click/readback'

export CU_SESSION="$session_b" CU_NODAEMON=1
assert_contains "$("$cu" web start)" '"started":true'
unset CU_NODAEMON
assert_contains "$("$cu" web open -Url "$fixture")" '"ok":true'
assert_contains "$("$cu" web type -Sel '#q' -Text 'isolated_B' -Verify)" '"verify":true'
assert_contains "$("$cu" web val -Sel '#q')" '"isolated_B"'
export CU_SESSION="$session_a"
assert_contains "$("$cu" web val -Sel '#q')" '"isolated_A"'
export CU_SESSION="$session_b"
assert_contains "$("$cu" web val -Sel '#q')" '"isolated_B"'
printf '%s\n' 'PASS: separate ports, browser profiles and tab state for A/B'

# A third session's port is occupied by a non-Chromium listener.
export CU_SESSION="$session_conflict" CU_NODAEMON=1
status="$("$cu" web status)"
port="$(printf '%s' "$status" | grep -o '"port":[0-9]*' | cut -d: -f2 | head -1)"
[[ "$port" =~ ^[0-9]+$ ]] || { echo 'FAIL: missing conflict test port'; exit 1; }
listener_log="$(mktemp)"
ps_script='$p=__PORT__; $s=New-Object System.Net.Sockets.TcpListener([Net.IPAddress]::Loopback,$p); $s.Start(); Write-Output "READY:$p"; try { Start-Sleep -Seconds 35 } finally { $s.Stop() }'
ps_script="$(printf '%s' "$ps_script" | sed "s/__PORT__/$port/")"
powershell.exe -NoProfile -NonInteractive -Command "$ps_script" >"$listener_log" 2>&1 &
server_pid=$!
sleep 2
assert_contains "$(cat "$listener_log")" "READY:$port"
for subcommand in status start stop; do
  reply="$("$cu" web "$subcommand" 2>&1 || true)"
  assert_contains "$reply" 'ERR_PORT_CONFLICT'
done
printf '%s\n' 'PASS: foreign CDP port listener rejected by status/start/stop'
printf '%s\n' 'PASS: all CDP isolation E2E checks'
