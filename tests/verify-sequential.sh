#!/usr/bin/env bash
# Sequential verification; never labels a process-only check as a GUI test.
set -uo pipefail
cd "$(dirname "$0")/.."
mkdir -p validation-results
log="validation-results/validation.log"
: > "$log"
record(){ printf '%s\n' "$*" | tee -a "$log"; }
record 'Emanager Linux verification: build -> localization audit -> smoke tests -> GUI window'
if ! command -v dotnet >/dev/null 2>&1; then
  record 'BLOCKED: dotnet SDK unavailable. Build, C# tests and GUI not run.'
  exit 127
fi
record 'STEP 1: Restore and compile'
if ! dotnet restore src/Emanager.App/Emanager.App.csproj >>"$log" 2>&1 ||
   ! dotnet build src/Emanager.App/Emanager.App.csproj -c Release --no-restore >>"$log" 2>&1; then
  record 'FAILED: application restore/build; later stages skipped.'; exit 1
fi
record 'PASS: application build'
record 'STEP 2: Localization audit'
if ! ./tests/localization-audit.sh >>"$log" 2>&1; then
  record 'FAILED: localization audit; GUI skipped.'; exit 5
fi
record 'PASS: localization audit'
record 'STEP 3: C# smoke tests'
if ! dotnet run --project tests/Emanager.FileOps.Smoke/Emanager.FileOps.Smoke.csproj -c Release >>"$log" 2>&1; then
  record 'FAILED: smoke tests; GUI skipped.'; exit 2
fi
record 'PASS: smoke tests'
record 'STEP 3b: Core xUnit tests through socket-free runner'
if ! ./tests/run-core-tests.sh >>"$log" 2>&1; then
  record 'FAILED: Core xUnit tests; GUI skipped.'; exit 4
fi
record 'PASS: Core xUnit tests'
record 'STEP 4: GUI window under Xvfb'
if ! command -v xvfb-run >/dev/null 2>&1 || ! command -v xdotool >/dev/null 2>&1; then
  record 'BLOCKED: xvfb-run and xdotool required to inspect an actual window.'; exit 3
fi
# Capture the process status and actual X11 window enumeration in the same Xvfb session.
# Xvfb runs without a window manager, so wmctrl (which needs _NET_CLIENT_LIST) cannot
# work here; xdotool searches window properties directly.
# Do not confuse a long-running, headless/crashed process with a visible window.
xvfb-run -a env -u WAYLAND_DISPLAY -u GNOME_SETUP_DISPLAY bash -c '
  dotnet run --project src/Emanager.App/Emanager.App.csproj -c Release --no-build > validation-results/gui.log 2>&1 &
  app_pid=$!
  trap '\''kill "$app_pid" 2>/dev/null || :; wait "$app_pid" 2>/dev/null || :'\'' EXIT
  for i in $(seq 1 30); do
    if ! kill -0 "$app_pid" 2>/dev/null; then exit 11; fi
    if xdotool search --name "Emanager" 2>/dev/null | grep -q .; then exit 0; fi
    sleep 1
  done
  exit 12
'
rc=$?
case "$rc" in
  0) record 'PASS: Emanager window detected in Xvfb. No interaction or screenshot verified.';;
  11) record 'FAILED: GUI process exited before a window was detected.';;
  12) record 'FAILED: no Emanager window detected within 30 seconds.';;
  *) record "FAILED: GUI verification returned exit=$rc.";;
esac
exit "$rc"
