#!/usr/bin/env bash
#
# cache-tests — Hermod's HTTP/1.x client cache against the suite behind
# cache-tests.fyi (github.com/http-tests/cache-tests).
#
# The suite tests a cache the way a CDN or a reverse proxy is tested: its
# client sends every request to the cache, the cache forwards what it cannot
# answer to the suite's own origin, and the origin counts what reaches it.
# Hermod is not a proxy, but since H-5 its client has a cache, and
# tests/cache-proxy/ is the thinnest reverse proxy that puts that cache where
# the suite expects one:
#
#     cache-tests client ─▶ HermodCacheProxy :8001 ─▶ HTTPClient + HTTPClientCache ─▶ origin :8000
#
# Each test's raw outcome is classified by the suite's own rules
# (tests/cache-proxy/classify.mjs calls its determineTestResult): a failed
# required test is a conformance failure, a failed optional one a missed
# optimisation, a check a yes or a no, and a test whose dependency failed says
# nothing of its own. Every test that is not a pass or a yes is recorded with
# its reason in tests/cache-tests-known.txt, and the run fails on ANY test
# whose result differs from the record, in either direction - a test that
# starts passing is a row to delete, and a file that could not tell
# "stopped failing" from "never ran" would be one of the checks CLAUDE.md
# warns about. A test the record names that the suite no longer has is
# reported as well.
#
# Usage:
#     tests/cache-tests.sh                     # shared cache, gated on the record
#     tests/cache-tests.sh --mode private      # a private cache, reported, not gated
#     tests/cache-tests.sh --mode none         # no cache at all: calibrates the run
#     tests/cache-tests.sh --id freshness-none # one test, with its requests dumped
#     tests/cache-tests.sh --update            # rewrite the record, keeping its notes
#     tests/cache-tests.sh --no-fetch          # skip rather than clone
#
# PREREQUISITES
#
# Node 22.19 or newer and npm, for the suite; the proxy built by
# `dotnet build HTTP1.slnx`. The suite is checked out under tests/thirdparty/
# (gitignored) and pinned to a commit. A missing prerequisite is a SKIP with
# its reason on the line, never a pass.

set -uo pipefail

ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
THIRD="$ROOT/tests/thirdparty"
SUITE="$THIRD/cache-tests"
KNOWN="$ROOT/tests/cache-tests-known.txt"
OUT="$ROOT/artifacts/cache-tests"

# Pinned on 2026-11-22. The suite moves - tests are added and reworded every
# few weeks - and a moving checkout would turn every upstream commit into a
# red night here. Bumping the pin is a decision, and the record moves with it.
SUITE_REPO="https://github.com/http-tests/cache-tests"
SUITE_PIN="3942fbfe1f6d23406e55b4120cde61da98080539"

MODE="shared"
ID=""
UPDATE=0
FETCH=1
ORIGIN_PORT=8000
PROXY_PORT=8001

while [ $# -gt 0 ]; do
    case "$1" in
        --mode)        MODE="${2:-}"; shift ;;
        --id)          ID="${2:-}"; shift ;;
        --update)      UPDATE=1 ;;
        --no-fetch)    FETCH=0 ;;
        --origin-port) ORIGIN_PORT="${2:-}"; shift ;;
        --proxy-port)  PROXY_PORT="${2:-}"; shift ;;
        -h|--help)     sed -n '2,40p' "${BASH_SOURCE[0]}" | sed 's/^# \{0,1\}//'; exit 0 ;;
        *)             echo "unknown option: $1" >&2; exit 2 ;;
    esac
    shift
done

case "$MODE" in
    shared|private|none) ;;
    *) echo "unknown mode: $MODE (shared, private or none)" >&2; exit 2 ;;
esac

if [ "$UPDATE" -eq 1 ] && [ "$MODE" != "shared" ]; then
    echo "--update writes the shared cache's record; drop --mode $MODE" >&2
    exit 2
fi

if [ -t 1 ]; then
    RED=$'\033[31m'; GREEN=$'\033[32m'; YELLOW=$'\033[33m'; CYAN=$'\033[36m'; DIM=$'\033[2m'; OFF=$'\033[0m'
else
    RED=""; GREEN=""; YELLOW=""; CYAN=""; DIM=""; OFF=""
fi

note() { printf '  %s%s%s\n' "$DIM" "$1" "$OFF"; }
skip() { echo; echo "  ${YELLOW}SKIP${OFF}  cache-tests — $1"; exit 0; }

echo
echo "  ${CYAN}cache-tests — Hermod's client cache, behind a reverse proxy of its own${OFF}"
note "suite:      $SUITE_REPO @ ${SUITE_PIN:0:12} (tests/thirdparty/, gitignored)"
note "cache:      $MODE"

# ---------------------------------------------------------------------------
# Prerequisites
# ---------------------------------------------------------------------------

command -v node > /dev/null 2>&1 || skip "no node on PATH"
command -v npm  > /dev/null 2>&1 || skip "no npm on PATH"

NODE_VERSION="$(node -p 'process.versions.node')"
node -e '
    const [a, b] = process.versions.node.split(".").map(Number);
    process.exit(a > 22 || (a === 22 && b >= 19) ? 0 : 1)
' || skip "node $NODE_VERSION is older than the 22.19 the suite needs"
note "node:       $NODE_VERSION"

PROXY_EXE="$ROOT/tests/cache-proxy/bin/Debug/net11.0/HermodCacheProxy"
# The Windows apphost only on Windows - a Windows-built tree carries both, and
# the .exe started from inside WSL runs on the other machine. See the long
# note in tests/run-tests.sh.
case "$(uname -s)" in
    Linux|Darwin) ;;
    *) [ -f "$PROXY_EXE.exe" ] && PROXY_EXE="$PROXY_EXE.exe" ;;
esac

if [ ! -f "$PROXY_EXE" ]; then
    echo
    echo "  ${RED}the cache proxy is not built: $PROXY_EXE${OFF}" >&2
    echo "  run: dotnet build HTTP1.slnx" >&2
    exit 1
fi

# ---------------------------------------------------------------------------
# The suite, pinned
# ---------------------------------------------------------------------------

checkout() {

    if [ -d "$SUITE/.git" ]; then
        [ "$(git -C "$SUITE" rev-parse HEAD 2>/dev/null)" = "$SUITE_PIN" ] && return 0
        git -C "$SUITE" fetch --quiet origin 2>/dev/null
        git -C "$SUITE" checkout --quiet "$SUITE_PIN" 2>/dev/null && return 0
    fi

    [ "$FETCH" -eq 1 ] || return 1

    mkdir -p "$THIRD"
    rm -rf "$SUITE"
    git clone --quiet "$SUITE_REPO" "$SUITE" > /dev/null 2>&1 || return 1
    git -C "$SUITE" checkout --quiet "$SUITE_PIN" > /dev/null 2>&1 || return 1

}

checkout || skip "no checkout of the suite, and $([ "$FETCH" -eq 1 ] && echo "cloning it failed" || echo "--no-fetch")"

# The suite has no lock file. Its runtime dependencies are undici, pinned
# exactly in its package.json and used for the interim-response tests, and
# what its result export needs, which this does not run.
if [ ! -d "$SUITE/node_modules/undici" ]; then
    ( cd "$SUITE" && npm install --omit=dev --no-fund --no-audit --loglevel=error ) > /dev/null 2>&1 ||
        skip "npm install in the suite failed; no network, or no npm registry"
fi

# ---------------------------------------------------------------------------
# The origin and the proxy
# ---------------------------------------------------------------------------

mkdir -p "$OUT"

ORIGIN_PID=""
PROXY_PID=""
ORIGIN_LOG="$OUT/origin.log"
PROXY_LOG="$OUT/proxy-$MODE.log"

stop_all() {
    for pid in $PROXY_PID $ORIGIN_PID; do
        kill "$pid" 2>/dev/null
        wait "$pid" 2>/dev/null
    done
}
trap stop_all EXIT

# wait_for <url> <pid> <what>
wait_for() {
    for _ in $(seq 1 60); do
        # Any status at all: the origin answers / with a 404.
        if curl -s -o /dev/null --max-time 1 "$1" 2>/dev/null; then
            return 0
        fi
        kill -0 "$2" 2>/dev/null || break
        sleep 0.5
    done
    echo
    echo "  ${RED}$3 did not come up on $1${OFF}" >&2
    return 1
}

# The suite's server reads its settings from npm's environment, as `npm run
# server --port=…` would set them; npm 11 refuses unknown flags, so they are
# set here directly.
( cd "$SUITE" &&
  npm_config_protocol=http npm_config_port="$ORIGIN_PORT" npm_config_pidfile="$OUT/origin.pid" \
  exec node test-engine/server/server.mjs ) > "$ORIGIN_LOG" 2>&1 &
ORIGIN_PID=$!

wait_for "http://127.0.0.1:$ORIGIN_PORT/" "$ORIGIN_PID" "the suite's origin" || { tail -20 "$ORIGIN_LOG" >&2; exit 1; }

"$PROXY_EXE" --port "$PROXY_PORT" --origin "http://127.0.0.1:$ORIGIN_PORT" --mode "$MODE" --log > "$PROXY_LOG" 2>&1 &
PROXY_PID=$!

wait_for "http://127.0.0.1:$PROXY_PORT/" "$PROXY_PID" "the cache proxy" || { tail -20 "$PROXY_LOG" >&2; exit 1; }

note "origin:     http://127.0.0.1:$ORIGIN_PORT/, the suite's own"
note "proxy:      http://127.0.0.1:$PROXY_PORT/, log in ${PROXY_LOG#$ROOT/}"

# ---------------------------------------------------------------------------
# One test, dumped
# ---------------------------------------------------------------------------

if [ -n "$ID" ]; then
    echo
    ( cd "$SUITE" &&
      npm_config_base="http://127.0.0.1:$PROXY_PORT" npm_config_id="$ID" \
      node --no-warnings test-engine/cli.mjs )
    exit $?
fi

# ---------------------------------------------------------------------------
# The whole suite
# ---------------------------------------------------------------------------

RESULTS="$OUT/$MODE.json"
CLASSIFIED="$OUT/$MODE.tsv"

started=$(date +%s)

# npm_package_config_id as well as npm_config_id: the CLI reads the first and
# falls back to the second, and an empty first is falsy.
( cd "$SUITE" &&
  npm_config_base="http://127.0.0.1:$PROXY_PORT" npm_config_id= npm_package_config_id= \
  node --no-warnings test-engine/cli.mjs ) > "$RESULTS" 2> "$OUT/$MODE.stderr" || {
    echo
    echo "  ${RED}the suite's client failed${OFF}" >&2
    tail -20 "$OUT/$MODE.stderr" >&2
    exit 1
}

node "$ROOT/tests/cache-proxy/classify.mjs" "$SUITE" "$RESULTS" > "$CLASSIFIED" 2> /dev/null || {
    echo
    echo "  ${RED}classifying the results failed${OFF}" >&2
    exit 1
}

elapsed=$(( $(date +%s) - started ))

TOTAL=$(wc -l < "$CLASSIFIED" | tr -d ' ')
SUMMARY=$(cut -f2 "$CLASSIFIED" | sort | uniq -c | awk '{ printf "%s%s %s", sep, $2, $1; sep = ", " }')

echo
note "$TOTAL tests in ${elapsed}s: $SUMMARY"
note "results:    ${RESULTS#$ROOT/} (cache-tests.fyi's format), ${CLASSIFIED#$ROOT/}"

if [ "$MODE" != "shared" ]; then
    echo
    echo "  ${CYAN}--mode $MODE is reported, not gated${OFF}: the record is the shared cache's"
    exit 0
fi

# ---------------------------------------------------------------------------
# --update: the record, rewritten from this run, its notes kept
# ---------------------------------------------------------------------------

if [ "$UPDATE" -eq 1 ]; then

    [ -f "$KNOWN" ] || : > "$KNOWN"

    header="$(grep '^#' "$KNOWN")"
    {
        [ -n "$header" ] && printf '%s\n\n' "$header"
        awk -F'\t' '
            FILENAME == ARGV[1] { if ($0 !~ /^#/ && NF >= 2) note[$1 "\t" $2] = $3; next }
            $2 != "pass" && $2 != "yes" { print $1 "\t" $2 "\t" note[$1 "\t" $2] }
        ' "$KNOWN" "$CLASSIFIED"
    } > "$KNOWN.new" 2>/dev/null || true
    mv "$KNOWN.new" "$KNOWN"

    echo
    echo "  ${GREEN}rewrote ${KNOWN#$ROOT/}${OFF}: $(grep -vc '^#\|^$' "$KNOWN") rows; a row without a note needs one"
    exit 0

fi

# ---------------------------------------------------------------------------
# The record
# ---------------------------------------------------------------------------

if [ ! -f "$KNOWN" ]; then
    echo
    echo "  ${RED}no record: ${KNOWN#$ROOT/}${OFF} - run with --update, then write the notes" >&2
    exit 1
fi

# Every test of the run against its row, or against pass/yes without one; and
# every row against the run.
DIFFERENCES="$(awk -F'\t' '
    FILENAME == ARGV[1] {
        if ($0 ~ /^#/ || NF < 2) next
        known[$1] = $2
        next
    }
    {
        seen[$1] = 1
        expected = ($1 in known) ? known[$1] : "pass"
        actual   = $2
        if (expected == "pass" && (actual == "pass" || actual == "yes")) next
        if (expected == actual) next
        if (actual == "pass" || actual == "yes")
            print "improved\t" $1 "\trecorded " expected ", now " actual " - delete the row"
        else
            print "differs\t" $1 "\trecorded " expected ", now " actual "\t" $3
    }
    END {
        for (id in known)
            if (!(id in seen))
                print "stale\t" id "\trecorded " known[id] ", and the suite has no such test"
    }
' "$KNOWN" "$CLASSIFIED" | sort)"

RECORDED=$(grep -vc '^#\|^$' "$KNOWN")

if [ -z "$DIFFERENCES" ]; then
    echo
    echo "  ${GREEN}✓${OFF} all $TOTAL as recorded: $((TOTAL - RECORDED)) pass or yes, $RECORDED rows in ${KNOWN#$ROOT/}"
    exit 0
fi

echo
printf '%s\n' "$DIFFERENCES" | while IFS=$'\t' read -r kind id what message; do
    case "$kind" in
        improved) printf '    %s↑%s %s %s(%s)%s\n' "$GREEN" "$OFF" "$id" "$DIM" "$what" "$OFF" ;;
        *)        printf '    %s✗%s %s %s(%s)%s\n' "$RED"   "$OFF" "$id" "$DIM" "$what" "$OFF"
                  [ -n "${message:-}" ] && printf '        %s%s%s\n' "$DIM" "$message" "$OFF" ;;
    esac
done

COUNT=$(printf '%s\n' "$DIFFERENCES" | wc -l | tr -d ' ')
echo
echo "  ${RED}$COUNT of $TOTAL not as recorded${OFF} in ${KNOWN#$ROOT/}; one test: tests/cache-tests.sh --id <test>"
exit 1
