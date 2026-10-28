#!/usr/bin/env bash
#
# Startup cost of the update check, measured rather than asserted.
#
# Two numbers:
#
#   time to first frame   how long the process takes to get a window on screen, with and without
#                          the update check in the build. This is the number a user feels.
#   check cost             what the check itself costs once it runs: a failed check against an
#                          unreachable API, and a successful one that opens the dialog.
#
#   ./perf.sh <baseline-worktree> <branch-worktree> [repeats]
#
# Exit code 0 means the run finished; the numbers are printed for the report.

set -u

BASELINE="${1:?baseline worktree}"
BRANCH="${2:?branch worktree}"
REPEATS="${3:-7}"

DOTNET="${DOTNET_ROOT:-/root/.dotnet8}/dotnet"
export DOTNET_ROOT="${DOTNET_ROOT:-/root/.dotnet8}"
export PATH="$DOTNET_ROOT:$PATH"
export DOTNET_CLI_TELEMETRY_OPTOUT=1
export DOTNET_NOLOGO=1

WORK="$(mktemp -d)"
trap 'rm -rf "$WORK"' EXIT

# The stub release API, so a "newer release exists" can be produced on demand.
python3 "$(dirname "$0")/stub_releases.py" "$WORK/port" "$WORK/api.log" "$WORK/tag" \
    >"$WORK/stub.out" 2>"$WORK/stub.err" &
STUB_PID=$!
for _ in $(seq 1 100); do
    [ -s "$WORK/port" ] && break
    sleep 0.1
done
PORT="$(cat "$WORK/port")"
printf 'v0.3.0\n' >"$WORK/tag"

echo "building both trees"
( cd "$BASELINE" && "$DOTNET" build src/Scrappy.App/Scrappy.App.csproj -v q --nologo ) >"$WORK/build-a.log" 2>&1 || { cat "$WORK/build-a.log"; exit 1; }
( cd "$BRANCH"   && "$DOTNET" build src/Scrappy.App/Scrappy.App.csproj -v q --nologo ) >"$WORK/build-b.log" 2>&1 || { cat "$WORK/build-b.log"; exit 1; }

A="$BASELINE/src/Scrappy.App/bin/Debug/net8.0/Scrappy.App.dll"
B="$BRANCH/src/Scrappy.App/bin/Debug/net8.0/Scrappy.App.dll"

run() {
    # run <dll> <extra args...> -> prints the wall time in ms
    local dll="$1"; shift
    local start end
    start=$(date +%s%N)
    ( cd "$(dirname "$dll")" && timeout 120 xvfb-run -a "$DOTNET" "$dll" "$@" ) >/dev/null 2>&1
    end=$(date +%s%N)
    echo $(( (end - start) / 1000000 ))
}

median() {
    printf '%s\n' "$@" | sort -n | awk '{a[NR]=$1} END {print (NR%2)?a[(NR+1)/2]:(a[NR/2]+a[NR/2+1])/2}'
}

spread() {
    printf '%s\n' "$@" | sort -n | awk 'NR==1{min=$1} {max=$1; s+=$1; n++} END {printf "min %d, max %d, mean %.0f", min, max, s/n}'
}

echo
echo "time to first frame (process start -> the window has been rendered), $REPEATS repeats each"
echo "  command: xvfb-run -a dotnet Scrappy.App.dll --shot <png> --shot-delay 300 --window 900x640"
echo

BASE=()
UPD=()
for i in $(seq 1 "$REPEATS"); do
    # Alternate so a slow patch of the machine hits both sides equally.
    BASE+=( "$(run "$A" --shot "$WORK/a.png" --shot-delay 300 --window 900x640)" )
    UPD+=( "$(run "$B" --shot "$WORK/b.png" --shot-delay 300 --window 900x640)" )
done

echo "  baseline (no update check) : $(median "${BASE[@]}") ms   $(spread "${BASE[@]}")"
echo "  branch   (update check in) : $(median "${UPD[@]}") ms   $(spread "${UPD[@]}")"
echo

echo "cost of the check itself, once it runs, $REPEATS repeats each"
echo "  command: xvfb-run -a dotnet Scrappy.App.dll --update-api <api> --update-answer update ..."
echo

FAIL=()
OK=()
for i in $(seq 1 "$REPEATS"); do
    FAIL+=( "$(run "$B" --update-api "http://127.0.0.1:1" --update-version 0.2.1 --update-state "$WORK/f.json" --update-answer update)" )
    OK+=( "$(run "$B" --update-api "http://127.0.0.1:$PORT" --update-version 0.2.1 --update-state "$WORK/o.json" --update-answer update)" )
done

echo "  check against an unreachable API : $(median "${FAIL[@]}") ms   $(spread "${FAIL[@]}")"
echo "  check that finds a new release   : $(median "${OK[@]}") ms   $(spread "${OK[@]}")"
echo

kill "$STUB_PID" 2>/dev/null
echo "done"
