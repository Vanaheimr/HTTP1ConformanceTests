#!/usr/bin/env bash
#
# A7 — interop against reference peers that are not .NET.
#
# Every interop check in this repository before this one was .NET against .NET,
# or curl. That matters in two different ways for the two directions:
#
#   Foreign clients -> our server.  Go, Java, Node, Python and wget each bring
#   their own header parser, chunked decoder, gzip and connection pool. curl
#   already proved the server answers a client nobody here wrote; five more
#   independent stacks prove it is not curl's quirks we happen to match.
#
#   Our client -> foreign servers.  This one had no witness at all. The server
#   has been judged by curl, by Autobahn and now by five clients; the client had
#   only ever talked to a server from the same source tree, so every wire-visible
#   assumption the two share was invisible to both. Go's net/http and Node's
#   node:http frame the responses here, and tests/h1peer is our client reading
#   them.
#
# Usage:
#     tests/interop.sh                             # against the default demo host
#     tests/interop.sh --base http://127.0.0.1:8080
#     tests/interop.sh --only go                   # one peer
#     tests/interop.sh --no-build                  # skip the h1peer build
#
# PLATFORM
#
# On Linux everything runs natively. On Windows the foreign toolchains live in
# the Debian WSL distribution - the same one the second curl build comes from -
# so peers run through `wsl -d Debian`. The demo host is then on the Windows
# side and is reached over the WSL default route, which needs the demo started
# with --bind-any; without it this exits with a reason rather than a pass.
#
# The peers are stdlib-only on purpose: `go run`, `java Client.java`, `node`,
# `python3`. Nothing is fetched, nothing is installed, and a clean checkout
# needs only the runtimes themselves.

set -uo pipefail

ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"

BASE="http://127.0.0.1:8080"
ONLY=""
BUILD=1

while [ $# -gt 0 ]; do
    case "$1" in
        --base)      BASE="${2:-}"; shift ;;
        --only)      ONLY="${2:-}"; shift ;;
        --no-build)  BUILD=0 ;;
        -h|--help)   sed -n '2,40p' "${BASH_SOURCE[0]}" | sed 's/^# \{0,1\}//'; exit 0 ;;
        *)           echo "unknown option: $1" >&2; exit 2 ;;
    esac
    shift
done

if [ -t 1 ]; then
    RED=$'\033[31m'; GREEN=$'\033[32m'; YELLOW=$'\033[33m'; DIM=$'\033[2m'; OFF=$'\033[0m'
else
    RED=""; GREEN=""; YELLOW=""; DIM=""; OFF=""
fi

TOTAL=0
FAILED=0
SKIPPED=0

pass()    { TOTAL=$((TOTAL+1)); printf '    %s✓%s %s\n' "$GREEN" "$OFF" "$1"; }
failure() { TOTAL=$((TOTAL+1)); FAILED=$((FAILED+1)); printf '    %s✗%s %s\n' "$RED" "$OFF" "$1"; [ -n "${2:-}" ] && printf '        %s%s%s\n' "$DIM" "$2" "$OFF"; }
skipped() { SKIPPED=$((SKIPPED+1)); printf '    %s~%s %s %s(%s)%s\n' "$YELLOW" "$OFF" "$1" "$DIM" "${2:-}" "$OFF"; }
note()    { printf '  %s%s%s\n' "$DIM" "$1" "$OFF"; }

# ---------------------------------------------------------------------------
# Where the peers run
# ---------------------------------------------------------------------------

PEERS_DIR="$ROOT/tests/peers"
H1PEER_DLL="$ROOT/tests/h1peer/bin/Debug/net10.0/h1peer.dll"

case "$(uname -s)" in

    Linux)
        RUN=()                                   # native
        PEERS_PATH="$PEERS_DIR"
        H1PEER_PATH="$H1PEER_DLL"
        DEMO_BASE="$BASE"
        ;;

    *)
        if ! command -v wsl > /dev/null 2>&1; then
            echo "  SKIP  interop — no WSL, and the foreign toolchains live there" >&2
            exit 0
        fi
        RUN=(wsl -d Debian -e bash -lc)
        PEERS_PATH="$(wsl -d Debian -e wslpath -a "$(cygpath -w "$PEERS_DIR" 2>/dev/null || echo "$PEERS_DIR")" | tr -d '\r')"
        H1PEER_PATH="$(wsl -d Debian -e wslpath -a "$(cygpath -w "$H1PEER_DLL" 2>/dev/null || echo "$H1PEER_DLL")" | tr -d '\r')"

        # The demo is on the Windows side; the WSL VM reaches it over the
        # default route, not over loopback.
        WSL_HOST="$(wsl -d Debian -- ip route show default 2>/dev/null | awk '{print $3}' | tr -d '\r')"
        DEMO_BASE="$(printf '%s' "$BASE" | sed -E "s#://(127\.0\.0\.1|localhost|\[::1\])#://$WSL_HOST#")"
        ;;

esac

# Run a command where the peers live.
peer() {
    if [ ${#RUN[@]} -eq 0 ]; then
        bash -lc "cd '$PEERS_PATH' && $1"
    else
        "${RUN[@]}" "cd '$PEERS_PATH' && $1"
    fi
}

wants() { [ -z "$ONLY" ] || [ "$ONLY" = "$1" ]; }

# ---------------------------------------------------------------------------

echo
echo "  interop — reference peers that are not .NET"
note "peers:      $PEERS_PATH"
note "demo host:  $DEMO_BASE"
echo

if ! peer "curl -s -o /dev/null -m 5 '$DEMO_BASE/'" 2>/dev/null; then
    echo "  ${RED}✗${OFF} the demo host is not reachable from where the peers run"
    note "on Windows the demo must be started with --bind-any; tests/run-tests.sh --wsl does that"
    echo
    printf '  %sinterop (peers): 0/1 checks passed%s\n' "$RED" "$OFF"
    exit 1
fi

# ---------------------------------------------------------------------------
# Foreign clients -> our server
# ---------------------------------------------------------------------------

echo "  -- foreign clients against our server --"

# Each peer program prints PASS/FAIL/SKIP lines, tab separated, and exits
# non-zero on any FAIL. Counting happens here so that one peer failing to start
# at all is a failure with a name rather than a silently missing section.
run_peer_client() {

    local label="$1" command="$2" runtime="$3"

    wants "$label" || return 0

    if ! peer "command -v $runtime > /dev/null 2>&1"; then
        skipped "$label" "$runtime not installed where the peers run"
        return 0
    fi

    echo "    ${DIM}$label${OFF}"

    local output
    output="$(peer "$command '$DEMO_BASE'" 2>&1)"

    if [ -z "$output" ]; then
        failure "$label — produced no output" "the peer program did not run"
        return 0
    fi

    while IFS=$'\t' read -r verdict name detail; do
        case "$verdict" in
            PASS) pass    "$label/$name" ;;
            FAIL) failure "$label/$name" "$detail" ;;
            SKIP) skipped "$label/$name" "$detail" ;;
            *)    [ -n "$verdict" ] && note "      $verdict $name $detail" ;;
        esac
    done <<< "$output"

}

run_peer_client go     "go run client.go"    go
run_peer_client java   "java Client.java"    java
run_peer_client node   "node client.mjs"     node
run_peer_client python "python3 client.py"   python3

# wget has no program of its own: it is a binary with opinions, driven here.
if wants wget && peer "command -v wget > /dev/null 2>&1"; then

    echo "    ${DIM}wget${OFF}"

    body="$(peer "wget -qO- --timeout=20 '$DEMO_BASE/chunked'" 2>&1)"
    if [ "$body" = "$(printf 'chunk-one\nchunk-two\nchunk-three')" ]; then
        pass "wget/chunked"
    else
        failure "wget/chunked" "got: $(printf '%s' "$body" | head -c 120 | tr '\n' ' ')"
    fi

    if peer "wget -q -O /dev/null --timeout=20 '$DEMO_BASE/files/resource.txt'"; then
        pass "wget/baseline"
    else
        failure "wget/baseline" "non-zero exit"
    fi

    if peer "wget -q -O /dev/null --timeout=20 '$DEMO_BASE/status/404'"; then
        failure "wget/status-404" "wget reported success for a 404"
    else
        pass "wget/status-404"
    fi

elif wants wget; then
    skipped "wget" "not installed where the peers run"
fi

# ---------------------------------------------------------------------------
# Our client -> foreign servers
# ---------------------------------------------------------------------------

echo
echo "  -- our client against foreign servers --"

if [ "$BUILD" -eq 1 ]; then
    dotnet build "$ROOT/tests/h1peer/h1peer.csproj" -v q --nologo > /dev/null 2>&1
fi

if ! peer "test -f '$H1PEER_PATH'"; then
    failure "h1peer" "not built: $H1PEER_PATH"
else

    run_peer_server() {

        local label="$1" command="$2" runtime="$3" port="$4"

        wants "$label" || return 0

        if ! peer "command -v $runtime > /dev/null 2>&1"; then
            skipped "h1peer/$label" "$runtime not installed where the peers run"
            return 0
        fi

        echo "    ${DIM}$label server${OFF}"

        # The peer prints LISTENING <port> once it is accepting, so the wait is
        # on a line rather than on a guess at a startup delay. Everything here
        # is loopback *inside* the peer environment: no VM boundary to cross,
        # which is also why this half works unchanged in CI.
        local output
        output="$(peer "($command $port > /tmp/h1peer-$label.log 2>&1 &) ; \
                        for i in \$(seq 1 60); do grep -q LISTENING /tmp/h1peer-$label.log 2>/dev/null && break; sleep 0.5; done ; \
                        dotnet '$H1PEER_PATH' --base http://127.0.0.1:$port --peer $label 2>&1 ; \
                        pkill -f '$command' > /dev/null 2>&1 ; true")"

        local verdict
        verdict="$(printf '%s\n' "$output" | grep -E 'checks passed' | tail -1)"

        if [ -z "$verdict" ]; then
            failure "h1peer/$label" "no verdict; $(printf '%s' "$output" | tail -3 | tr '\n' ' ')"
            return 0
        fi

        local got want
        got="$(printf '%s' "$verdict"  | sed -E 's#.*: ([0-9]+)/([0-9]+) checks passed.*#\1#')"
        want="$(printf '%s' "$verdict" | sed -E 's#.*: ([0-9]+)/([0-9]+) checks passed.*#\2#')"

        local i=0
        while [ "$i" -lt "$want" ]; do
            if [ "$i" -lt "$got" ]; then TOTAL=$((TOTAL+1)); else TOTAL=$((TOTAL+1)); FAILED=$((FAILED+1)); fi
            i=$((i+1))
        done

        if [ "$got" = "$want" ]; then
            printf '    %s✓%s h1peer/%s %s(%s/%s)%s\n' "$GREEN" "$OFF" "$label" "$DIM" "$got" "$want" "$OFF"
        else
            printf '    %s✗%s h1peer/%s %s(%s/%s)%s\n' "$RED" "$OFF" "$label" "$DIM" "$got" "$want" "$OFF"
            printf '%s\n' "$output" | grep -E '✗' | sed 's/^/        /'
        fi

    }

    run_peer_server go   "go run server.go"   go   18080
    run_peer_server node "node server.mjs"    node 18081

fi

# ---------------------------------------------------------------------------

echo
if [ "$SKIPPED" -gt 0 ]; then
    note "$SKIPPED check(s) skipped — each with a reason above"
fi

if [ "$FAILED" -eq 0 ]; then
    printf '  %sinterop (peers): %d/%d checks passed%s\n' "$GREEN" "$TOTAL" "$TOTAL" "$OFF"
    exit 0
else
    printf '  %sinterop (peers): %d/%d checks passed%s\n' "$RED" "$((TOTAL-FAILED))" "$TOTAL" "$OFF"
    exit 1
fi
