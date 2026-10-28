#!/usr/bin/env bash
#
# End-to-end evidence for the update notifier.
#
# It runs the real app, head-less under Xvfb, against a stand-in for the GitHub Releases API that
# reports a release newer than the running build, and drives the real dialog from the command line.
# Nothing here is a mock of the feature: the comparison, the suppression file, the dialog and the
# URL handed to the desktop are the ones the product uses.
#
#   ./run.sh [path-to-scrappy-repository-root]
#
# Exit code 0 means every scenario behaved as specified.

set -u

ROOT="${1:-$(cd "$(dirname "$0")/../.." && pwd)}"
HERE="$(cd "$(dirname "$0")" && pwd)"
WORK="$(mktemp -d)"
STATE="$WORK/update.json"
BROWSER_LOG="$WORK/browser.log"
DIALOG_SHOT="$WORK/update-dialog.png"
export FAKE_BROWSER_LOG="$BROWSER_LOG"

DOTNET="${DOTNET_ROOT:-/root/.dotnet8}/dotnet"
export DOTNET_ROOT="${DOTNET_ROOT:-/root/.dotnet8}"
export PATH="$DOTNET_ROOT:$PATH"
export DOTNET_CLI_TELEMETRY_OPTOUT=1
export DOTNET_NOLOGO=1

# The stand-in for the desktop's URL handler goes first in PATH, so "opens the release page in the
# default browser" is observable: the URL the OS was handed is written to a file.
FAKE_BIN="$WORK/bin"
mkdir -p "$FAKE_BIN"
cp "$HERE/fake-xdg-open" "$FAKE_BIN/xdg-open"
chmod +x "$FAKE_BIN/xdg-open"
export PATH="$FAKE_BIN:$PATH"

pass=0
fail=0

say()  { printf '\n\033[1m%s\033[0m\n' "$*"; }
ok()   { printf '  PASS  %s\n' "$*"; pass=$((pass + 1)); }
bad()  { printf '  FAIL  %s\n' "$*"; fail=$((fail + 1)); }

expect() {
    # expect <description> <file> <pattern>
    if grep -qF -- "$3" "$2"; then ok "$1"; else bad "$1 (no match for '$3' in $2)"; fi
}

absent() {
    # absent <description> <file> <pattern>
    if grep -qF -- "$3" "$2"; then bad "$1 (unexpected '$3')"; else ok "$1"; fi
}

cleanup() {
    [ -n "${STUB_PID:-}" ] && kill "$STUB_PID" 2>/dev/null
    rm -rf "$WORK"
}
trap cleanup EXIT

# --- the stand-in release API ---------------------------------------------------------------

say "starting the stand-in release API"

python3 "$HERE/stub_releases.py" "$WORK/port" "$WORK/api.log" "$WORK/tag" \
    >"$WORK/stub.out" 2>"$WORK/stub.err" &
STUB_PID=$!

for _ in $(seq 1 100); do
    [ -s "$WORK/port" ] && break
    sleep 0.1
done

if [ ! -s "$WORK/port" ]; then
    echo "the stand-in API never started:" >&2
    cat "$WORK/stub.err" >&2
    exit 1
fi

PORT="$(cat "$WORK/port")"
API="http://127.0.0.1:$PORT"
printf '  api   %s\n' "$API"

publish() { printf '%s\n' "$1" >"$WORK/tag"; }

# --- one head-less launch of the real app ----------------------------------------------------

launch() {
    # launch <answer> <tag> [extra args]
    local answer="$1" tag="$2"; shift 2
    publish "$tag"
    local log="$WORK/run-$answer-$tag.log"
    LAST_LOG="$log"

    # One log per launch: what this launch handed to the desktop, and nothing that an earlier one did.
    : >"$BROWSER_LOG"

    ( cd "$ROOT" && timeout 120 xvfb-run -a "$DOTNET" run --project src/Scrappy.App -- \
        --update-api "$API" \
        --update-version 0.2.1 \
        --update-state "$STATE" \
        --update-shot "$DIALOG_SHOT" \
        --update-answer "$answer" \
        "$@" ) >"$log" 2>&1

    LAST_EXIT=$?
    return 0
}

say "building the app"
( cd "$ROOT" && "$DOTNET" build src/Scrappy.App/Scrappy.App.csproj -v q --nologo ) >"$WORK/build.log" 2>&1
if [ $? -ne 0 ]; then
    echo "build failed:" >&2
    cat "$WORK/build.log" >&2
    exit 1
fi
ok "src/Scrappy.App builds"

# --- 1. a newer release exists: the dialog is shown, Update opens the release page ------------

say "scenario 1 - Update opens the release page"

rm -f "$STATE" "$DIALOG_SHOT" "$BROWSER_LOG"
launch update v0.3.0

sed -n '/update/p' "$LAST_LOG"
expect "a newer release is detected"              "$LAST_LOG" "update check          Available"
expect "the dialog is shown"                      "$LAST_LOG" "update dialog: pressing Update"
expect "the release page is the one offered"      "$LAST_LOG" "update opening        https://github.com/orev7s/scrappy-release/releases/tag/v0.3.0"
expect "the URL reaches the desktop's launcher"   "$BROWSER_LOG" "https://github.com/orev7s/scrappy-release/releases/tag/v0.3.0"
expect "the app asked the release API"            "$WORK/api.log" "/releases/latest"
[ -s "$DIALOG_SHOT" ] && ok "the dialog was captured to $DIALOG_SHOT" \
                       || bad "the dialog capture is missing"

# --- 2. Skip: dismissed for now, nothing remembered -------------------------------------------

say "scenario 2 - Skip dismisses this launch and remembers nothing"

rm -f "$STATE"
launch skip v0.3.0
expect "the dialog is shown again after a fresh launch" "$LAST_LOG" "update check          Available"
expect "Skip is the decision"                           "$LAST_LOG" "update decision       Skip"
absent "Skip opens nothing"                             "$BROWSER_LOG" "github.com"
[ -f "$STATE" ] && bad "Skip wrote a suppression file" || ok "Skip wrote no suppression file"

# Skip is not permanent: the next launch asks again.
launch skip v0.3.0
expect "after Skip the next launch asks again" "$LAST_LOG" "update check          Available"
[ -f "$STATE" ] && bad "Skip wrote a suppression file" || ok "Skip still remembered nothing"

# --- 3. Don't show again: this version only ---------------------------------------------------

say "scenario 3 - Don't show again suppresses this version, and only this version"

rm -f "$STATE"
launch never v0.3.0
expect "the dialog is shown"                "$LAST_LOG" "update check          Available"
expect "Don't show again is the decision"   "$LAST_LOG" "update decision       DontShowAgain"
[ -f "$STATE" ] && ok "the dismissal was written to $STATE" || bad "no suppression file was written"
printf '  file  %s\n' "$(cat "$STATE" 2>/dev/null | tr -d '\n ')"

# The same version, on a later launch: silent.
launch update v0.3.0
expect "the same version stays silent"  "$LAST_LOG" "update check          Suppressed"
absent "no dialog for the same version" "$LAST_LOG" "update dialog: pressing"
absent "nothing is opened"              "$BROWSER_LOG" "releases/tag/v0.3.0"

# A newer version: the question is asked again.
launch update v0.4.0
expect "a newer version is offered again" "$LAST_LOG" "update check          Available"
expect "the dialog comes back"            "$LAST_LOG" "update dialog: pressing Update"

# --- 4. no newer release: silence --------------------------------------------------------------

say "scenario 4 - an up-to-date build is not offered an update"

rm -f "$STATE"
launch update v0.2.1
expect "the current version reads as up to date" "$LAST_LOG" "update check          UpToDate"
absent "no dialog when there is nothing new"     "$LAST_LOG" "update dialog: pressing"

# --- 5. the API being unreachable ---------------------------------------------------------------

say "scenario 5 - an unreachable release API is silent and does not stop start-up"

rm -f "$STATE"
publish v0.3.0
( cd "$ROOT" && timeout 120 xvfb-run -a "$DOTNET" run --project src/Scrappy.App -- \
    --update-api "http://127.0.0.1:1" \
    --update-version 0.2.1 \
    --update-state "$STATE" \
    --update-answer update ) >"$WORK/run-offline.log" 2>&1
LAST_LOG="$WORK/run-offline.log"

expect "the app still starts"          "$LAST_LOG" "update check          Unknown"
absent "no dialog when the API is down" "$LAST_LOG" "update dialog: pressing"

# --- summary ------------------------------------------------------------------------------------

say "summary"
printf '  %d passed, %d failed\n' "$pass" "$fail"
if [ "$fail" -eq 0 ]; then
    printf '\nUPDATE E2E PASS\n\n'
    exit 0
fi

printf '\nUPDATE E2E FAIL\n\n'
exit 1
