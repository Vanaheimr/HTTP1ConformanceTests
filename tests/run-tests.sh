#!/usr/bin/env bash
#
# Test runner for the hand-rolled HTTP/1.x stack.
#
# Builds the solution, starts the demo host, drives every harness against it,
# and prints a pass/fail summary. Each harness self-reports its own check count
# and exits non-zero on any failure, so the runner relies on exit codes alone
# and never has to scrape output.
#
# Bash rather than PowerShell on purpose: CI runs on Linux, and Git Bash makes
# the same script work on Windows. One script, no drift between two copies.
#
# Usage:
#     tests/run-tests.sh                  # build + run everything
#     tests/run-tests.sh --no-build       # skip the build step
#     tests/run-tests.sh --filter attack  # only harnesses matching *attack*
#     tests/run-tests.sh --tls            # drive the TLS listener instead
#     tests/run-tests.sh --keep-demo      # leave the demo running afterwards
#     tests/run-tests.sh --wsl            # also drive the Debian curl (see below)
#
# --wsl starts the demo with --bind-any (0.0.0.0 instead of loopback) so the WSL
# VM can reach it, and then runs the Debian curl leg as well. Opt-in, because a
# plain test run must never widen a listener as a side effect.
#
set -uo pipefail

ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
SLN="$ROOT/HTTP1.slnx"

HTTP_PORT=8080
TLS_PORT=8443

NO_BUILD=0
FILTER=""
USE_TLS=0
KEEP_DEMO=0
USE_WSL=0

while [ $# -gt 0 ]; do
    case "$1" in
        --no-build)  NO_BUILD=1 ;;
        --filter)    FILTER="${2:-}"; shift ;;
        --tls)       USE_TLS=1 ;;
        --keep-demo) KEEP_DEMO=1 ;;
        --wsl)       USE_WSL=1 ;;
        -h|--help)   sed -n '2,20p' "${BASH_SOURCE[0]}" | sed 's/^# \{0,1\}//'; exit 0 ;;
        *)           echo "unknown option: $1" >&2; exit 2 ;;
    esac
    shift
done

# Colours only when stdout is a terminal — CI logs should stay plain text.
if [ -t 1 ]; then
    RED=$'\033[31m'; GREEN=$'\033[32m'; CYAN=$'\033[36m'; DIM=$'\033[2m'; OFF=$'\033[0m'
else
    RED=""; GREEN=""; CYAN=""; DIM=""; OFF=""
fi

section() { printf '\n%s=== %s ===%s\n' "$CYAN" "$1" "$OFF"; }

TOTAL=0
PASSED=0
FAILURES=()

DEMO_PID=""
DEMO_LOG="$(mktemp -t h1demo.XXXXXX.log)"

cleanup() {
    if [ -n "$DEMO_PID" ] && [ "$KEEP_DEMO" -eq 0 ]; then
        section "Stopping demo host"
        kill "$DEMO_PID" 2>/dev/null
        wait "$DEMO_PID" 2>/dev/null
        echo "  stopped (pid $DEMO_PID)"
    elif [ -n "$DEMO_PID" ]; then
        echo "  demo host left running (pid $DEMO_PID), log: $DEMO_LOG"
    fi
}
trap cleanup EXIT

# ---------------------------------------------------------------------------
# Build
# ---------------------------------------------------------------------------
if [ "$NO_BUILD" -eq 0 ]; then
    section "Build"
    if ! dotnet build "$SLN" -v quiet --nologo > /dev/null; then
        echo "${RED}  solution build failed${OFF}" >&2
        exit 1
    fi
    echo "${GREEN}  build OK${OFF}"
fi

# ---------------------------------------------------------------------------
# Demo host
# ---------------------------------------------------------------------------
section "Starting demo host"

# Refuse to start on top of a stale listener rather than silently testing
# whatever is already bound to the port — a harness that reports on the wrong
# process is worse than one that will not start.
if curl -s -o /dev/null --max-time 2 "http://127.0.0.1:$HTTP_PORT/" 2>/dev/null; then
    echo "${RED}  something is already listening on :$HTTP_PORT${OFF}" >&2
    echo "  stop it first, or run with --no-build against it deliberately" >&2
    exit 1
fi

DEMO_EXE="$ROOT/Demo/bin/Debug/net10.0/HTTP1.Demo"
[ -f "$DEMO_EXE.exe" ] && DEMO_EXE="$DEMO_EXE.exe"

if [ ! -f "$DEMO_EXE" ]; then
    echo "${RED}  demo host not built: $DEMO_EXE${OFF}" >&2
    exit 1
fi

# --fast-timeouts shortens the demo's read deadlines so the timeout checks
# resolve in seconds rather than half a minute each. It changes how long the
# harness waits, not what it asserts: the claim under test is "an incomplete
# message eventually yields 408", never a particular number of seconds.
DEMO_ARGS=(--fast-timeouts)
if [ "$USE_WSL" -eq 1 ]; then
    DEMO_ARGS+=(--bind-any)
    echo "  --wsl: binding 0.0.0.0 so the WSL VM can reach the demo"
fi

"$DEMO_EXE" "${DEMO_ARGS[@]}" > "$DEMO_LOG" 2>&1 &
DEMO_PID=$!

READY=0
for _ in $(seq 1 60); do
    if curl -s -o /dev/null --max-time 1 "http://127.0.0.1:$HTTP_PORT/" 2>/dev/null; then
        READY=1
        break
    fi
    if ! kill -0 "$DEMO_PID" 2>/dev/null; then
        echo "${RED}  demo host exited during startup${OFF}" >&2
        cat "$DEMO_LOG" >&2
        exit 1
    fi
    sleep 0.5
done

if [ "$READY" -eq 0 ]; then
    echo "${RED}  demo host did not become ready on :$HTTP_PORT${OFF}" >&2
    cat "$DEMO_LOG" >&2
    exit 1
fi

echo "${GREEN}  demo host up (pid $DEMO_PID)${OFF}"

# ---------------------------------------------------------------------------
# Harnesses
# ---------------------------------------------------------------------------
if [ "$USE_TLS" -eq 1 ]; then
    TARGET_ARGS=(--tls --port "$TLS_PORT")
    echo "  driving the TLS listener on :$TLS_PORT"
else
    TARGET_ARGS=(--port "$HTTP_PORT")
fi

run_harness() {
    local name="$1"

    if [ -n "$FILTER" ] && [[ "$name" != *"$FILTER"* ]]; then
        return
    fi

    TOTAL=$((TOTAL + 1))

    local output
    output="$(dotnet run --project "$ROOT/tests/$name/$name.csproj" --no-build -- "${TARGET_ARGS[@]}" 2>&1)"
    local status=$?

    # The harness's own verdict line, e.g. "h1syntax: 34/34 checks passed".
    local verdict
    verdict="$(printf '%s\n' "$output" | grep -E 'checks passed' | tail -1 | sed 's/^ *//')"

    if [ $status -eq 0 ]; then
        PASSED=$((PASSED + 1))
        printf '  %sPASS%s  %-14s %s%s%s\n' "$GREEN" "$OFF" "$name" "$DIM" "$verdict" "$OFF"
    else
        FAILURES+=("$name")
        printf '  %sFAIL%s  %-14s %s\n' "$RED" "$OFF" "$name" "$verdict"
        printf '%s\n' "$output" | grep -E '^\s+✗' | sed 's/^/      /'
    fi
}

section "Wire-level harnesses"
run_harness h1syntax
run_harness h1framing
run_harness h1conn

section "Semantics"
run_harness h1semantics
run_harness h1sse

section "Hardening"
run_harness h1attack

# ---------------------------------------------------------------------------
# Third-party: curl
#
# The first consumer in this gate that is not our own code. Everything above
# establishes something about an implementation written here; this establishes
# that an independent one agrees.
# ---------------------------------------------------------------------------
run_curl_matrix() {
    local label="$1"; shift

    if [ -n "$FILTER" ] && [[ "curl" != *"$FILTER"* ]] && [[ "$label" != *"$FILTER"* ]]; then
        return
    fi

    TOTAL=$((TOTAL + 1))

    local output
    output="$("$ROOT/tests/curl-matrix.sh" "$@" 2>&1)"
    local status=$?

    local verdict
    verdict="$(printf '%s\n' "$output" | grep -E 'checks passed' | tail -1 | sed 's/^ *//')"

    if [ $status -eq 0 ]; then
        PASSED=$((PASSED + 1))
        printf '  %sPASS%s  %-14s %s%s%s\n' "$GREEN" "$OFF" "$label" "$DIM" "$verdict" "$OFF"
    else
        FAILURES+=("$label")
        printf '  %sFAIL%s  %-14s %s\n' "$RED" "$OFF" "$label" "$verdict"
        printf '%s\n' "$output" | grep -E '^\s+✗' | sed 's/^/      /'
    fi
}

section "Third-party (curl)"

if command -v curl > /dev/null 2>&1; then
    if [ "$USE_TLS" -eq 1 ]; then
        run_curl_matrix "curl/TLS" --base "https://127.0.0.1:$TLS_PORT" --insecure --label "curl/TLS"
    else
        run_curl_matrix "curl" --base "http://127.0.0.1:$HTTP_PORT" --label "curl"
    fi
else
    echo "  SKIP  curl not on PATH"
fi

# The second curl — the Debian one, which has HTTP/2 and therefore proves that
# --http1.1 is honoured by a client that *could* do otherwise. Needs --wsl, which
# binds the demo to 0.0.0.0; without it the demo is loopback-only and the WSL VM
# has no route. Skipped with a reason rather than silently, since a silent skip
# is indistinguishable from a pass.
if [ "$USE_WSL" -eq 1 ] && command -v wsl > /dev/null 2>&1 && [ "$USE_TLS" -eq 0 ]; then
    WSL_HOST="$(wsl -d Debian -- ip route show default 2>/dev/null | awk '{print $3}' | tr -d '\r')"
    if [ -n "$WSL_HOST" ] && wsl -d Debian -- curl -s -o /dev/null -m 3 "http://$WSL_HOST:$HTTP_PORT/" 2>/dev/null; then
        run_curl_matrix "curl/debian" --base "http://$WSL_HOST:$HTTP_PORT" --curl "wsl -d Debian -- curl" --label "debian curl"
    else
        echo "  SKIP  debian curl — WSL cannot reach the demo at ${WSL_HOST:-<no default route>}"
    fi
elif [ "$USE_TLS" -eq 0 ]; then
    echo "  SKIP  debian curl — pass --wsl to bind 0.0.0.0 and include it"
fi

# ---------------------------------------------------------------------------
# Summary
# ---------------------------------------------------------------------------
section "Summary"

if [ ${#FAILURES[@]} -eq 0 ]; then
    printf '  %s%d/%d harnesses passed%s\n' "$GREEN" "$PASSED" "$TOTAL" "$OFF"
    exit 0
else
    printf '  %s%d/%d harnesses passed%s\n' "$RED" "$PASSED" "$TOTAL" "$OFF"
    echo "  failures:"
    for f in "${FAILURES[@]}"; do echo "    - $f"; done
    exit 1
fi
