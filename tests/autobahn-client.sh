#!/usr/bin/env bash
#
# Autobahn TestSuite, CLIENT side: drives Hermod's WebSocketClient through
# `wstest -m fuzzingserver`.
#
# This is the mirror of tests/autobahn.sh. There the suite is the client and our
# echo server is under test; here the suite is the server and our client is under
# test. They cover different code: WebSocketServer and WebSocketClient are
# separate implementations inside the same subsystem, and until this script
# existed only the first of them had ever met a foreign suite.
#
# Hermod's own WebSocket README has claimed "Client, sections 1-7, 10 - 242 OK /
# 0 FAILED" and "Client, compression 12/13 - 126 OK / 0 FAILED" for a long time,
# and PLAN.md recorded those as unreproducible from a clean checkout. This is
# where they become a command.
#
# Usage:
#   tests/autobahn-client.sh
#   tests/autobahn-client.sh --no-build
#   tests/autobahn-client.sh --no-deflate          # offer no permessage-deflate
#   tests/autobahn-client.sh --min-pass 0          # measure without a verdict
#   tests/autobahn-client.sh --first 1 --last 20   # a slice, for debugging
#
# The only prerequisite is Docker; the suite ships usably only as an image,
# because the native wstest is legacy Python 2.

set -u

port=9001
image="crossbario/autobahn-testsuite"
agent="Hermod.HTTP1.Client"
nobuild=0
deflate=1
first=0
last=0

# A ceiling on the whole run, so a hang fails inside this script with the report
# intact rather than as a step timeout - which GitHub counts as a *cancellation*
# and which therefore skips everything that would have explained it.
run_timeout=1200

# The floor this run must not fall below.
#
# 445 of 517, measured 2026-09-23, the first time Hermod's WebSocket CLIENT was
# ever pointed at a foreign suite. 440 OK, 3 INFORMATIONAL, 2 NON-STRICT, 72
# UNIMPLEMENTED, and -- the number that matters -- ZERO hard failures. Everything
# this client attempts, it gets right.
#
# The 72 are sections 13.3, 13.4, 13.5 and 13.6, eighteen cases each, and they
# are declined by the SUITE rather than by us. Read off the wire from each case
# report: those sections configure the fuzzingserver to expect a client offer
# carrying server_max_window_bits (9 or 15, with and without
# client_no_context_takeover). Our offer is the fixed constant
# WebSocketPerMessageDeflate.ClientOfferHeader --
#
#     permessage-deflate; client_no_context_takeover; server_no_context_takeover
#
# -- which never carries that parameter, so the server answers those four
# sections with no Sec-WebSocket-Extensions at all and the compression they
# wanted to exercise is never negotiated. 13.1, 13.2 (no parameter expected) and
# 13.7 (a list containing one offer without it) match our offer and pass.
#
# Nothing is wrong on the wire, and this is NOT the same thing as the 36 declines
# on the server side: those are an RFC 7692 Section 7.1.2.1 refusal we are
# REQUIRED to make, because DeflateStream cannot compress with a 9-bit window.
# These are an offer we do not know how to make. Both trace back to the same
# fact -- DeflateStream exposes no window-size control -- from the two opposite
# ends of it.
#
# Raise it when the number goes up. A floor that is never raised is a ratchet
# that has rusted.
min_pass=445

while [ $# -gt 0 ]; do
    case "$1" in
        --port)        port="$2";        shift 2 ;;
        --image)       image="$2";       shift 2 ;;
        --agent)       agent="$2";       shift 2 ;;
        --run-timeout) run_timeout="$2"; shift 2 ;;
        --min-pass)    min_pass="$2";    shift 2 ;;
        --first)       first="$2";       shift 2 ;;
        --last)        last="$2";        shift 2 ;;
        --no-deflate)  deflate=0;        shift ;;
        --no-build)    nobuild=1;        shift ;;
        -h|--help)     grep '^#' "$0" | sed 's/^# \{0,1\}//'; exit 0 ;;
        *) echo "Unknown argument: $1" >&2; exit 2 ;;
    esac
done

here="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
root="$(dirname "$here")"
repdir="$root/tests/autobahn/reports-client"

is_windows() {
    case "$(uname -s 2>/dev/null)" in
        MINGW*|MSYS*|CYGWIN*) return 0 ;;
        *) return 1 ;;
    esac
}

command -v docker >/dev/null 2>&1 || {
    echo "docker not found. Install Docker and retry. See tests/TestingAgainst_Autobahn.md." >&2
    exit 127
}

# --- build -----------------------------------------------------------------
if [ "$nobuild" -eq 0 ]; then
    echo "Building the client driver..."
    dotnet build "$root/tests/autobahn-client/autobahn-client.csproj" --nologo --verbosity quiet || exit 1
fi

driver="$root/tests/autobahn-client/bin/Debug/net10.0/autobahn-client.dll"
[ -f "$driver" ] || {
    echo "Client driver not built (no autobahn-client.dll). Run without --no-build first." >&2
    exit 1
}

# --- how we reach the suite ------------------------------------------------
# The topology is the reverse of the server-side script, and so is the network
# fix. There the CONTAINER had to reach a listener on the host, which needs
# --network host on Linux and the host.docker.internal alias on Docker Desktop.
# Here the HOST has to reach a listener in the container, and publishing the port
# does that identically on both - so this script has no platform branch in the
# way autobahn.sh does. Only the bind-mount path still needs cygpath under Git
# Bash, because MSYS rewrites "src:/config" into a Windows path list.
if is_windows; then
    mount_src="$(cygpath -w "$repdir")"
else
    mount_src="$repdir"
fi

rm -rf "$repdir"; mkdir -p "$repdir"
cat >"$repdir/fuzzingserver.json" <<JSON
{
    "url": "ws://0.0.0.0:$port",
    "outdir": "/reports",
    "cases": ["*"],
    "exclude-cases": [],
    "exclude-agent-cases": {}
}
JSON

container="h1-autobahn-fuzzingserver-$port"
docker rm -f "$container" >/dev/null 2>&1 || true

cleanup() {
    docker rm -f "$container" >/dev/null 2>&1 || true
}
trap cleanup EXIT INT TERM

echo "Starting the Autobahn fuzzingserver (image $image) on :$port..."
# Deliberately not --rm: an auto-removed container takes its exit status and its
# logs with it, and those are the only view of the suite's own side of a failure.
MSYS_NO_PATHCONV=1 docker run -d --name "$container" -e PYTHONUNBUFFERED=1 \
    -p "$port:$port" \
    -v "$mount_src:/config" \
    -v "$mount_src:/reports" \
    "$image" \
    wstest -m fuzzingserver -s /config/fuzzingserver.json >/dev/null || {
        echo "Could not start the fuzzingserver container." >&2
        exit 1
    }

# Wait until the suite actually answers a WebSocket handshake.
#
# A plain TCP connect is NOT enough here and cost an hour proving it. With -p,
# docker-proxy binds the host port the moment the container is created, so a
# connect succeeds immediately -- several seconds before wstest has loaded its
# spec, started Twisted and begun listening inside. The driver then handshakes
# into a socket nobody is reading and reports HTTP 408, which reads exactly like
# a client bug. This probes the thing we actually need: a 101.
ready=0
for _ in $(seq 1 60); do
    if python3 - "$port" <<'PYEOF' >/dev/null 2>&1
import socket, sys
port = int(sys.argv[1])
# The port belongs in the Host header: Autobahn answers a bare "Host: 127.0.0.1"
# with "400 missing port in HTTP Host header", which a probe looking only for a
# 101 would report as "not up yet" until it gave up.
request = (
    'GET /getCaseCount HTTP/1.1\r\n'
    'Host: 127.0.0.1:%d\r\n'
    'Upgrade: websocket\r\n'
    'Connection: Upgrade\r\n'
    'Sec-WebSocket-Key: dGhlIHNhbXBsZSBub25jZQ==\r\n'
    'Sec-WebSocket-Version: 13\r\n\r\n' % port
).encode()
s = socket.create_connection(('127.0.0.1', port), 2)
s.sendall(request)
s.settimeout(2)
sys.exit(0 if b'101' in s.recv(64) else 1)
PYEOF
    then
        ready=1
        break
    fi
    sleep 0.5
done

if [ "$ready" -ne 1 ]; then
    echo "The fuzzingserver did not come up on :$port" >&2
    docker logs "$container" 2>&1 | tail -30 >&2
    exit 1
fi
echo "fuzzingserver up on ws://127.0.0.1:$port/"
echo

args="--host 127.0.0.1 --port $port --agent $agent"
[ "$deflate" -eq 1 ] && args="$args --deflate"
[ "$first" -gt 0 ] && args="$args --first $first"
[ "$last"  -gt 0 ] && args="$args --last $last"

# A slice cannot reach a floor that counts the whole suite, so applying one to it
# reports FAIL for a reason that has nothing to do with conformance. --first and
# --last exist for debugging one section; the gate always runs everything.
if [ "$first" -gt 0 ] || [ "$last" -gt 0 ]; then
    if [ "$min_pass" -gt 0 ]; then
        echo "Slice requested (--first/--last): the floor of $min_pass does not apply to a partial run."
        echo "Hard failures are still fatal."
        min_pass=0
    fi
fi

runner=""
if [ "$run_timeout" -gt 0 ] && command -v timeout >/dev/null 2>&1; then
    runner="timeout --signal=TERM --kill-after=30s ${run_timeout}s"
fi

# shellcheck disable=SC2086  # $runner and $args are fixed literals or empty
$runner dotnet "$driver" $args || {
    rc=$?
    case "$rc" in
        124) echo "The client driver exceeded the ${run_timeout}s cap." >&2 ;;
        *)   echo "The client driver returned $rc" >&2 ;;
    esac
}

echo
echo "fuzzingserver log (tail):"
docker logs "$container" 2>&1 | tail -5

# --- parse the report ------------------------------------------------------
indexfile="$repdir/index.json"
if [ ! -f "$indexfile" ]; then
    echo >&2
    echo "No index.json in $repdir -- the suite never wrote a report." >&2
    echo "Container log:" >&2
    docker logs "$container" 2>&1 | tail -40 >&2
    exit 1
fi

# Three buckets, not two, exactly as on the server side:
#   passing  OK / NON-STRICT / INFORMATIONAL   must stay at or above min_pass
#   declined UNIMPLEMENTED                     tolerated and counted
#   hard     FAILED / WRONG CODE / UNCLEAN     always fatal, whatever the count
#
# Python does the counting; jq is not assumed.
summary="$(python3 - "$indexfile" <<'PYEOF'
import json, sys, collections

with open(sys.argv[1], encoding='utf-8') as f:
    report = json.load(f)

passing = declined = hard = 0
buckets = collections.Counter()
hard_cases = []

for agent, cases in report.items():
    for case, info in cases.items():
        behavior = info.get('behavior', 'MISSING')
        buckets[behavior] += 1
        if behavior in ('OK', 'NON-STRICT', 'INFORMATIONAL'):
            passing += 1
        elif behavior == 'UNIMPLEMENTED':
            declined += 1
        else:
            hard += 1
            if len(hard_cases) < 12:
                hard_cases.append('%s %s' % (case, behavior))

total = passing + declined + hard
print('PASSING=%d' % passing)
print('DECLINED=%d' % declined)
print('HARD=%d' % hard)
print('TOTAL=%d' % total)
print('BUCKETS=%s' % ', '.join('%s %d' % (k, v) for k, v in sorted(buckets.items())))
print('HARDCASES=%s' % '; '.join(hard_cases))
PYEOF
)" || { echo "Could not parse $indexfile" >&2; exit 1; }

passing=$(echo "$summary"  | sed -n 's/^PASSING=//p')
declined=$(echo "$summary" | sed -n 's/^DECLINED=//p')
hard=$(echo "$summary"     | sed -n 's/^HARD=//p')
total=$(echo "$summary"    | sed -n 's/^TOTAL=//p')
bucketline=$(echo "$summary"  | sed -n 's/^BUCKETS=//p')
hardcases=$(echo "$summary"   | sed -n 's/^HARDCASES=//p')

echo
echo "Autobahn (client): $passing/$total passing, $declined declined (UNIMPLEMENTED), $hard hard failures"
echo "  verdicts: $bucketline"
echo "Full HTML report: $repdir/index.html"
echo

if [ "$hard" -gt 0 ]; then
    echo "FAIL: $hard hard failure(s): $hardcases" >&2
    exit 1
fi

if [ "$passing" -lt "$min_pass" ]; then
    echo "FAIL: $passing passing is below the floor of $min_pass." >&2
    exit 1
fi

if [ "$min_pass" -gt 0 ] && [ "$passing" -gt "$min_pass" ]; then
    echo "NOTE: $passing passing is ABOVE the floor of $min_pass. Raise min_pass in this script"
    echo "      so the improvement cannot be lost again without a failure."
fi

echo "Autobahn (client): at or above the floor ($passing >= $min_pass), no hard failures."
