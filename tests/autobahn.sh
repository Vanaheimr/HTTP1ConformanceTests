#!/usr/bin/env bash
#
# Run the Autobahn TestSuite WebSocket conformance run against the demo host's
# echo server on :8081. Linux, macOS, WSL, and Windows under Git Bash.
#
# The Autobahn TestSuite (https://github.com/crossbario/autobahn-testsuite) is
# the canonical RFC 6455 conformance suite; its "fuzzingclient" drives ~500
# cases against a WebSocket ECHO server. We run it from the official Docker
# image (the native wstest is legacy Python 2), so the only prerequisite is
# Docker.
#
# WHY THIS REPOSITORY IS THE RIGHT HOME FOR IT
#
# Autobahn speaks WebSocket over an HTTP/1.1 Upgrade handshake, which is
# exactly what this stack does. The HTTP/2 sibling also runs Autobahn, but it
# cannot drive RFC 8441 with it — Autobahn does not speak WebSocket-over-HTTP/2
# — so there the suite is pointed at a plain-TCP tunnel behind an HTTP/1.1
# handshake, and what it certifies is the framing layer alone. Here the whole
# path is the real one: handshake, framing, permessage-deflate, close.
#
# The two stacks are also not the same code. Hermod carries three separate
# WebSocket implementations — HTTP1/WebSocket (24 files, ~12k lines, with its
# own WebSocketFrame and WebSocketPerMessageDeflate), HTTP2/WebSocket (~860
# lines), and an HTTP3/WebSocket that is a byte-identical copy of the HTTP/2
# one. Until this script existed, Autobahn's 517/517 certified the smallest of
# the three and the largest was covered by nothing.
#
# The demo host has served the target all along: Demo/Program.cs brings up a
# WebSocket echo server on :8081 with the comment "the Autobahn fuzzingclient
# target". It was simply never driven.
#
# Usage:
#   tests/autobahn.sh                      # build + run everything
#   tests/autobahn.sh --no-build
#   tests/autobahn.sh --ws-port 8081 --image crossbario/autobahn-testsuite
#   tests/autobahn.sh --run-timeout 1200   # cap the fuzzingclient (0 = off)
#
set -euo pipefail

ws_port=8081
http_port=8080
tls_port=8443
image="crossbario/autobahn-testsuite"
nobuild=0

# A ceiling on the fuzzingclient itself, so a hang fails inside this script
# rather than hanging whatever called it. The HTTP/2 sibling learned this the
# hard way: without a cap its nightly step burned the whole 45-minute budget,
# and a step that overruns timeout-minutes counts as a *cancellation*, so the
# summary, the artifact and even the job log were all lost.
run_timeout=1200

while [ $# -gt 0 ]; do
    case "$1" in
        --ws-port)     ws_port="$2";     shift 2 ;;
        --image)       image="$2";       shift 2 ;;
        --run-timeout) run_timeout="$2"; shift 2 ;;
        --no-build)    nobuild=1;        shift ;;
        -h|--help)     grep '^#' "$0" | sed 's/^# \{0,1\}//'; exit 0 ;;
        *) echo "Unknown argument: $1" >&2; exit 2 ;;
    esac
done

ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
SLN="$ROOT/HTTP1.slnx"
REPDIR="$ROOT/tests/autobahn/reports"

command -v docker >/dev/null 2>&1 || {
    echo "docker not found. Install Docker and retry." >&2
    exit 127
}

is_windows() {
    case "$(uname -s)" in
        MINGW*|MSYS*|CYGWIN*) return 0 ;;
        *)                    return 1 ;;
    esac
}

# --- free the demo's ports -------------------------------------------------
# All three, because the demo brings them up as one set and a stale listener on
# any of them faults the whole startup.
#
# Neither fuser nor ss exists under Git Bash, and netstat there is *localized*
# (a German Windows prints "ABHÖREN", not "LISTENING"), so its state column is
# not safe to key on. Get-NetTCPConnection returns a typed State instead, which
# is why the Windows branch goes through powershell.exe — a system query, the
# counterpart of ss, not a second implementation of anything.
free_ports() {
    if is_windows; then
        local list pids pid
        list="$(printf '%s,' "$@" | sed 's/,$//')"
        pids="$(powershell.exe -NoProfile -NonInteractive -Command \
                  "Get-NetTCPConnection -LocalPort $list -State Listen -ErrorAction SilentlyContinue |
                   Select-Object -ExpandProperty OwningProcess" 2>/dev/null \
                | tr -d '\r' | grep -E '^[0-9]+$' | sort -u || true)"
        for pid in $pids; do
            # //PID, not /PID: MSYS rewrites a single leading slash into a path.
            taskkill //PID "$pid" //F >/dev/null 2>&1 || true
        done
        [ -n "$pids" ] && sleep 0.5
    elif command -v fuser >/dev/null 2>&1; then
        local p
        for p in "$@"; do fuser -k "${p}/tcp" >/dev/null 2>&1 || true; done
    elif command -v ss >/dev/null 2>&1; then
        local spec="" p pids
        for p in "$@"; do
            if [ -n "$spec" ]; then spec="$spec or "; fi
            spec="${spec}sport = :${p}"
        done
        pids="$(ss -ltnpH "$spec" 2>/dev/null | grep -oE 'pid=[0-9]+' | grep -oE '[0-9]+' | sort -u || true)"
        if [ -n "$pids" ]; then
            # shellcheck disable=SC2086
            kill $pids 2>/dev/null || true
            sleep 0.5
        fi
    fi
    return 0
}

# --- build -----------------------------------------------------------------
if [ "$nobuild" -eq 0 ]; then
    echo "Building the solution..."
    dotnet build "$SLN" -v quiet >/dev/null
fi

# --- locate the demo host --------------------------------------------------
# The extensionless apphost is preferred everywhere except a Windows shell.
# That ordering is deliberate and is the opposite of what a naive `.exe`-first
# check does: under WSL the Windows .exe is both present (via /mnt) and
# *executable* through binfmt interop, so it would start as a Windows process
# bound to Windows' loopback — which nothing inside the WSL network namespace
# can then reach. The failure looks like "the demo never became ready".
DEMO="$ROOT/Demo/bin/Debug/net10.0/HTTP1.Demo"
if is_windows && [ -f "$DEMO.exe" ]; then
    DEMO="$DEMO.exe"
fi
[ -f "$DEMO" ] || { echo "Demo host not built ($DEMO). Run without --no-build first." >&2; exit 1; }

free_ports "$http_port" "$tls_port" "$ws_port"

# NOT --fast-timeouts, which tests/run-tests.sh does pass: it shortens the
# demo's read deadlines so the timeout harness resolves in seconds. Several
# Autobahn cases deliberately pause mid-message, and a shortened deadline would
# close those connections and report our own test configuration as a
# conformance failure.
DEMO_LOG="$(mktemp -t h1-autobahn-demo.XXXXXX.log)"
"$DEMO" > "$DEMO_LOG" 2>&1 &
DEMO_PID=$!

cleanup() {
    kill "$DEMO_PID" 2>/dev/null || true
    wait "$DEMO_PID" 2>/dev/null || true
    free_ports "$http_port" "$tls_port" "$ws_port"
    # The demo's own log is the only view of a failure from OUR side of the
    # wire; keep it next to the report rather than discarding it.
    if [ -s "$DEMO_LOG" ] && [ -d "$REPDIR" ]; then
        cp "$DEMO_LOG" "$REPDIR/demo-host.log" 2>/dev/null || true
    fi
    rm -f "$DEMO_LOG"
    if [ -n "${CONTAINER:-}" ]; then
        docker rm -f "$CONTAINER" >/dev/null 2>&1 || true
    fi
    return 0
}
trap cleanup EXIT

# Wait for the WebSocket listener specifically. A bare TCP connect rather than
# an HTTP request: :8081 answers an Upgrade handshake, not a plain GET.
ready=0
for _ in $(seq 1 60); do
    if ! kill -0 "$DEMO_PID" 2>/dev/null; then
        echo "Demo host exited during startup:" >&2; cat "$DEMO_LOG" >&2; exit 1
    fi
    if (exec 3<>"/dev/tcp/127.0.0.1/$ws_port") 2>/dev/null; then
        exec 3>&- 3<&-; ready=1; break
    fi
    sleep 0.5
done
[ "$ready" -eq 1 ] || { echo "Demo host did not start listening on :$ws_port" >&2; cat "$DEMO_LOG" >&2; exit 1; }
echo "Demo host up (pid $DEMO_PID), WebSocket echo on ws://127.0.0.1:$ws_port/"

# --- how the container reaches the echo server -----------------------------
# `--network host` puts the container in the host's own network namespace, so
# 127.0.0.1 inside it *is* the host. That is a Linux-namespace feature: Docker
# Desktop either lacks it or offers it as an opt-in beta, so there the default
# bridge plus the host.docker.internal alias is the documented way in.
#
# The mounts need the same care under Git Bash: MSYS rewrites an argument
# beginning with a slash into a Windows path and treats the colon in
# "src:/config" as a path-list separator. MSYS_NO_PATHCONV=1 turns that off and
# cygpath hands docker the D:\... form it wants. Both are inert on Linux.
if is_windows; then
    docker_net=""
    ws_host="host.docker.internal"
    mount_src="$(cygpath -w "$REPDIR")"
else
    docker_net="--network host"
    ws_host="127.0.0.1"
    mount_src="$REPDIR"
fi

rm -rf "$REPDIR"; mkdir -p "$REPDIR"
cat >"$REPDIR/fuzzingclient.json" <<JSON
{
    "outdir": "/reports",
    "servers": [{ "agent": "Hermod.HTTP1", "url": "ws://$ws_host:$ws_port" }],
    "cases": ["*"],
    "exclude-cases": [],
    "exclude-agent-cases": {}
}
JSON

CONTAINER="h1-autobahn-wstest-$ws_port"
docker rm -f "$CONTAINER" >/dev/null 2>&1 || true

runner=""
if [ "$run_timeout" -gt 0 ] && command -v timeout >/dev/null 2>&1; then
    runner="timeout --signal=TERM --kill-after=30s ${run_timeout}s"
fi

echo "Running Autobahn fuzzingclient (image $image, ws://$ws_host:$ws_port)..."
# PYTHONUNBUFFERED, because without it wstest's "Running test case ID X" lines
# arrive in blocks and the last one printed is NOT the case it is working on.
# That cost the HTTP/2 sibling a wrong diagnosis: three logs ended on the same
# case and it looked like one that reliably hangs, when a passing run showed
# that line sitting as the last flushed one for four minutes.
#
# Deliberately not --rm: an auto-removed container takes its exit status with
# it, and `docker inspect` below is what separates a kernel OOM kill from every
# other cause of a 137.
# shellcheck disable=SC2086  # $runner and $docker_net are fixed literals or empty
MSYS_NO_PATHCONV=1 $runner docker run --name "$CONTAINER" -e PYTHONUNBUFFERED=1 $docker_net \
    -v "$mount_src:/config" \
    -v "$mount_src:/reports" \
    "$image" \
    wstest -m fuzzingclient -s /config/fuzzingclient.json || {
        rc=$?
        case "$rc" in
            124) echo "fuzzingclient exceeded the ${run_timeout}s cap -- stopping the container." >&2 ;;
            137) echo "fuzzingclient was killed (SIGKILL, exit 137) before the ${run_timeout}s cap." >&2 ;;
            *)   echo "docker run returned $rc" >&2 ;;
        esac

        if kill -0 "$DEMO_PID" 2>/dev/null; then
            echo "demo host (pid $DEMO_PID) was still alive at this point." >&2
        else
            echo "demo host (pid $DEMO_PID) had ALREADY EXITED -- see demo-host.log." >&2
        fi

        state="$(docker inspect \
                   --format 'exit={{.State.ExitCode}} oom-killed={{.State.OOMKilled}} error="{{.State.Error}}"' \
                   "$CONTAINER" 2>/dev/null || true)"
        if [ -n "$state" ]; then
            echo "container state: $state" >&2
        else
            echo "container already gone; no state to inspect." >&2
        fi
    }

# --- parse the report ------------------------------------------------------
INDEX="$REPDIR/index.json"
[ -f "$INDEX" ] || { echo "No Autobahn report at $INDEX (did the container reach the server?)" >&2; exit 1; }

# Pass = behavior AND behaviorClose both in {OK, NON-STRICT, INFORMATIONAL}.
# Prefer jq, fall back to python3, last resort a grep heuristic that can only
# answer yes/no.
bad=1
if command -v jq >/dev/null 2>&1; then
    bad="$(jq '[.. | objects | select(has("behavior")) |
                 select((.behavior     | IN("OK","NON-STRICT","INFORMATIONAL") | not) or
                        (.behaviorClose | IN("OK","NON-STRICT","INFORMATIONAL") | not))] | length' "$INDEX")"
    total="$(jq '[.. | objects | select(has("behavior"))] | length' "$INDEX")"
    echo; echo "Autobahn: $((total - bad))/$total cases OK"
    if [ "$bad" -gt 0 ]; then
        echo "Non-passing cases:" >&2
        jq -r '.. | objects | select(has("behavior")) |
               select((.behavior     | IN("OK","NON-STRICT","INFORMATIONAL") | not) or
                      (.behaviorClose | IN("OK","NON-STRICT","INFORMATIONAL") | not)) |
               "  behavior=\(.behavior) close=\(.behaviorClose)"' "$INDEX" >&2
    fi
elif command -v python3 >/dev/null 2>&1; then
    read -r total bad < <(python3 - "$INDEX" <<'PY'
import json, sys
allowed = {"OK", "NON-STRICT", "INFORMATIONAL"}
d = json.load(open(sys.argv[1]))
total = fail = 0
for agent in d.values():
    for cid, r in agent.items():
        total += 1
        if r.get("behavior") not in allowed or r.get("behaviorClose") not in allowed:
            fail += 1
            print(f"  {cid}: behavior={r.get('behavior')} close={r.get('behaviorClose')}", file=sys.stderr)
print(total, fail)
PY
)
    echo; echo "Autobahn: $((total - bad))/$total cases OK"
else
    echo "Neither jq nor python3 found; falling back to a grep heuristic." >&2
    if grep -qE '"(behavior|behaviorClose)": *"(FAILED|WRONG CODE|UNCLEAN)"' "$INDEX"; then bad=1; else bad=0; fi
fi

echo "Full HTML report: $REPDIR/index.html"
echo
if [ "$bad" -gt 0 ]; then
    echo "Autobahn reported non-passing cases."
    exit 1
fi
echo "Autobahn: all cases passed."
exit 0
