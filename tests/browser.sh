#!/usr/bin/env bash
#
# A8 — real browsers against the demo host.
#
# Every other consumer in this repository was written to be a test: our
# harnesses, curl, five foreign stdlib clients, five reverse proxies. A
# browser was not, and it is the least forgiving one in daily use. Three
# things only it can say:
#
#   it names the protocol itself     performance.nextHopProtocol is Chrome's
#                                    verdict that it spoke HTTP/1.1, not ours
#   it implements EventSource        curl can read an SSE body; only a browser
#                                    has the client half of the protocol
#   it enforces CORS                 curl sends the request and is answered.
#                                    A browser refuses to make it at all - see
#                                    H-10 below, which is the whole point
#
# Three engines, via Playwright: Chromium, Firefox and WebKit. WebKit is the
# one nobody tests and the only way to reach Safari's engine from a script.
#
# WHAT IS EXPECTED TO FAIL
#
# The preflighted cross-origin POST, on all three. The demo's /cors route sets
# Access-Control-Allow-Origin, so the simple GET works - but a POST carrying a
# custom header is not simple, the browser sends an OPTIONS preflight first,
# and nothing answers it: 405, Allow: GET, POST. That is H-10, and A8 is where
# it stops being a line in a table. The same POST from curl is answered 200,
# which is exactly why no other driver here could find it.
#
# Recorded in tests/browser-known.txt with its finding number. A failure that
# is NOT in that file fails the run; one that stops failing is reported so the
# line can be deleted.
#
# Usage:
#     tests/browser.sh                     # starts its own demo, all engines
#     tests/browser.sh --browser webkit
#     tests/browser.sh --base http://127.0.0.1:8080
#     tests/browser.sh --headed            # watch it
#     tests/browser.sh --update            # rewrite the known file
#     tests/browser.sh --no-install        # skip rather than fetch Playwright
#
# COST
#
# Playwright's three engines are about half a gigabyte, once. tests/browser.sh
# fetches them on demand and skips with a reason if it cannot, which is why
# this is a nightly and not a gate. It needs no Docker and no WSL: the
# browsers run natively wherever this runs.

set -uo pipefail

ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
BROWSER_DIR="$ROOT/tools/browser"
KNOWN="$ROOT/tests/browser-known.txt"

BASE=""
DEMO_PORT=8080
WHICH="all"
HEADED=""
UPDATE=0
INSTALL=1

while [ $# -gt 0 ]; do
    case "$1" in
        --base)        BASE="${2:-}"; shift ;;
        --browser)     WHICH="${2:-}"; shift ;;
        --port)        DEMO_PORT="${2:-}"; shift ;;
        --headed)      HEADED="--headed" ;;
        --update)      UPDATE=1 ;;
        --no-install)  INSTALL=0 ;;
        -h|--help)     sed -n '2,46p' "${BASH_SOURCE[0]}" | sed 's/^# \{0,1\}//'; exit 0 ;;
        *)             echo "unknown option: $1" >&2; exit 2 ;;
    esac
    shift
done

if [ -t 1 ]; then
    RED=$'\033[31m'; GREEN=$'\033[32m'; YELLOW=$'\033[33m'; CYAN=$'\033[36m'; DIM=$'\033[2m'; OFF=$'\033[0m'
else
    RED=""; GREEN=""; YELLOW=""; CYAN=""; DIM=""; OFF=""
fi

note() { printf '  %s%s%s\n' "$DIM" "$1" "$OFF"; }

DEMO_PID=""
DEMO_LOG=""
KEEP_DEMO_LOG=0

cleanup() {
    if [ -n "$DEMO_PID" ]; then
        kill "$DEMO_PID" 2>/dev/null
        wait "$DEMO_PID" 2>/dev/null
    fi
    [ -n "$DEMO_LOG" ] && [ "$KEEP_DEMO_LOG" -eq 0 ] && rm -f "$DEMO_LOG"
    return 0
}
trap cleanup EXIT

echo
echo "  ${CYAN}browser — A8: three engines against the demo host${OFF}"
note "runner:     tools/browser/interop.mjs"
note "known:      ${KNOWN#"$ROOT/"}"

# ---------------------------------------------------------------------------
# Node, and Playwright on demand
# ---------------------------------------------------------------------------

if ! command -v node > /dev/null 2>&1; then
    echo
    echo "  ${YELLOW}SKIP${OFF}  browser — node is not on PATH, and Playwright is a node package"
    exit 0
fi

if [ ! -d "$BROWSER_DIR/node_modules/playwright" ]; then

    if [ "$INSTALL" -eq 0 ]; then
        echo
        echo "  ${YELLOW}SKIP${OFF}  browser — Playwright is not installed and --no-install was given"
        exit 0
    fi

    note "installing Playwright into tools/browser …"
    ( cd "$BROWSER_DIR" && npm install --no-fund --no-audit --loglevel=error ) > /dev/null 2>&1

    if [ ! -d "$BROWSER_DIR/node_modules/playwright" ]; then
        echo
        echo "  ${YELLOW}SKIP${OFF}  browser — npm install failed; no network, or no npm"
        exit 0
    fi

fi

# The engines are a separate, much larger download, and "installed" is not
# something the package can be asked about cheaply - so the launch below is
# what decides. A missing engine is reported by interop.mjs as a skipped
# browser with its reason, not as a failure.
if [ "$INSTALL" -eq 1 ]; then
    note "making sure the engines are present (about half a gigabyte, once) …"
    ( cd "$BROWSER_DIR" && npx --yes playwright install chromium firefox webkit ) > /dev/null 2>&1
fi

# ---------------------------------------------------------------------------
# The demo host
#
# Started here when there is none, the way tests/smuggler.sh and
# tests/proxy.sh do. Deliberately NOT with --bind-any: the browsers run on
# this machine, and the cross-origin twin the CORS checks need is
# http://localhost, which reaches a loopback-bound listener perfectly well.
# A test run has no business widening a listener it did not have to.
# ---------------------------------------------------------------------------

if [ -z "$BASE" ]; then

    BASE="http://127.0.0.1:$DEMO_PORT"

    if curl -s -o /dev/null --max-time 3 "$BASE/" 2>/dev/null; then
        note "demo:       already listening on $BASE, using it"
    else

        DEMO_EXE="$ROOT/Demo/bin/Debug/net10.0/HTTP1.Demo"
        [ -f "$DEMO_EXE.exe" ] && DEMO_EXE="$DEMO_EXE.exe"

        if [ ! -f "$DEMO_EXE" ]; then
            echo
            echo "  ${RED}demo host not built: $DEMO_EXE${OFF}" >&2
            echo "  run: dotnet build HTTP1.slnx" >&2
            exit 1
        fi

        DEMO_LOG="$(mktemp -t h1-browser-demo.XXXXXX.log)"
        "$DEMO_EXE" > "$DEMO_LOG" 2>&1 &
        DEMO_PID=$!

        ready=0
        for _ in $(seq 1 60); do
            if curl -s -o /dev/null --max-time 1 "$BASE/" 2>/dev/null; then
                ready=1
                break
            fi
            kill -0 "$DEMO_PID" 2>/dev/null || break
            sleep 0.5
        done

        if [ "$ready" -eq 0 ]; then
            echo
            echo "  ${RED}the demo host did not become ready on $BASE${OFF}" >&2
            KEEP_DEMO_LOG=1
            tail -20 "$DEMO_LOG" >&2
            exit 1
        fi

        note "demo:       started (pid $DEMO_PID)"

    fi

fi

# The CORS half needs the twin origin to resolve to the same listener. If it
# does not - a hosts file without localhost, an IPv6-only resolution to a
# v4-only listener - those two checks would fail for a reason that has
# nothing to do with the server, so say so rather than record it.
TWIN="$(printf '%s' "$BASE" | sed -e 's#127\.0\.0\.1#localhost#' -e 's#//localhost#//localhost#')"
if ! curl -s -o /dev/null --max-time 3 "$TWIN/" 2>/dev/null; then
    echo
    echo "  ${YELLOW}SKIP${OFF}  browser — the cross-origin twin $TWIN does not reach the demo"
    note "the CORS checks would fail for a resolver reason rather than a server one"
    exit 0
fi

# ---------------------------------------------------------------------------
# Run
# ---------------------------------------------------------------------------

OUT="$(mktemp -t h1browser.XXXXXX.log)"

( cd "$BROWSER_DIR" && node interop.mjs --base "$BASE" --browser "$WHICH" $HEADED ) > "$OUT" 2>&1
run_status=$?

# The runner's DIFF lines are for joining rather than reading, and its own
# verdict is re-printed below once this script knows whether the failures
# were expected ones - so neither goes through here.
grep -vE "^DIFF$(printf '\t')|browser: [0-9]+/[0-9]+ checks passed" "$OUT"

verdict="$(grep -E 'browser: [0-9]+/[0-9]+ checks passed' "$OUT" | tail -1)"

if [ -z "$verdict" ]; then
    echo
    echo "  ${RED}the runner produced no verdict${OFF}" >&2
    tail -8 "$OUT" >&2
    echo "  (exit $run_status)" >&2
    rm -f "$OUT"
    exit 1
fi

# ---------------------------------------------------------------------------
# Join the failures against what is already known
# ---------------------------------------------------------------------------

touch "$KNOWN"

NEW_ROWS=()
SEEN_ROWS=()

while IFS=$'\t' read -r _ engine check detail; do

    [ -n "${engine:-}" ] || continue

    SEEN_ROWS+=("$engine	$check")

    if awk -F'\t' -v e="$engine" -v c="$check" \
           '$1==e && $2==c { found=1 } END { exit found ? 0 : 1 }' "$KNOWN"; then
        continue
    fi

    NEW_ROWS+=("$engine	$check	$detail")

done < <(grep -E '^DIFF	' "$OUT")

STALE=()
while IFS= read -r line; do

    case "$line" in ""|"#"*) continue ;; esac

    e="$(printf '%s' "$line" | cut -f1)"
    c="$(printf '%s' "$line" | cut -f2)"

    # Only for engines this run actually launched.
    grep -q -- "-- $e " "$OUT" || continue

    still=0
    i=0
    while [ "$i" -lt "${#SEEN_ROWS[@]}" ]; do
        [ "${SEEN_ROWS[$i]}" = "$(printf '%s\t%s' "$e" "$c")" ] && still=1 && break
        i=$((i + 1))
    done

    [ "$still" -eq 0 ] && STALE+=("$e	$c")

done < "$KNOWN"

if [ "$UPDATE" -eq 1 ]; then
    {
        echo "# A8 — browser-visible gaps that are already filed."
        echo "#"
        echo "# <engine> TAB <check> TAB <why>"
        echo "#"
        echo "# A failure not listed here fails the run. A listed one that has"
        echo "# stopped failing is reported so the line can be deleted."
        echo "#"
        echo "# Written by tests/browser.sh --update on $(date -u +%Y-%m-%d)."
        echo
        for row in ${SEEN_ROWS[@]+"${SEEN_ROWS[@]}"}; do
            printf '%s\tH-10: no automatic CORS preflight; the demo answers OPTIONS 405\n' "$row"
        done | sort -u
    } > "$KNOWN"
    echo
    echo "  ${YELLOW}--update${OFF}: rewrote ${KNOWN#"$ROOT/"}"
fi

if [ "${#STALE[@]}" -gt 0 ] && [ "$UPDATE" -eq 0 ]; then
    echo
    echo "  ${GREEN}no longer failing${OFF} — delete these lines from ${KNOWN#"$ROOT/"}:"
    for row in "${STALE[@]}"; do printf '    %s\n' "$row"; done
fi

rm -f "$OUT"

if [ "${#NEW_ROWS[@]}" -gt 0 ] && [ "$UPDATE" -eq 0 ]; then
    echo
    echo "  ${RED}not in ${KNOWN#"$ROOT/"}${OFF}:"
    for row in "${NEW_ROWS[@]}"; do printf '    %s\n' "$row"; done
    echo
    if [ "${#NEW_ROWS[@]}" -eq 1 ]; then
        printf '  %s%s — and one of them is new%s\n' "$RED" "${verdict#  }" "$OFF"
    else
        printf '  %s%s — and %d of them are new%s\n' "$RED" "${verdict#  }" "${#NEW_ROWS[@]}" "$OFF"
    fi
    exit 1
fi

echo
printf '  %s%s%s\n' "$GREEN" "${verdict#  }" "$OFF"
note "every failure above is a recorded, filed gap — see ${KNOWN#"$ROOT/"}"
exit 0
