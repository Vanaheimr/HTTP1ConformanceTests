#!/usr/bin/env bash
#
# curl conformance matrix for the Hermod HTTP/1.x demo host.
#
# curl is the closest thing HTTP/1.1 has to a reference client, and it is the
# first consumer in this repository that is not our own code. Everything the
# C# harnesses establish, they establish about an implementation written here;
# these checks are the first ones an independent implementation has to agree
# with.
#
# Usage:
#     tests/curl-matrix.sh                          # defaults
#     tests/curl-matrix.sh --base http://host:8080
#     tests/curl-matrix.sh --base https://127.0.0.1:8443 --insecure
#     tests/curl-matrix.sh --curl /usr/bin/curl --label "debian curl"
#
set -uo pipefail

BASE="http://127.0.0.1:8080"
CURL="curl"
LABEL=""
INSECURE=""

while [ $# -gt 0 ]; do
    case "$1" in
        --base)      BASE="${2:-}"; shift ;;
        --curl)      CURL="${2:-}"; shift ;;
        --label)     LABEL="${2:-}"; shift ;;
        --insecure)  INSECURE="-k" ;;
        -h|--help)   sed -n '2,16p' "${BASE_SOURCE:-${BASH_SOURCE[0]}}" | sed 's/^# \{0,1\}//'; exit 0 ;;
        *)           echo "unknown option: $1" >&2; exit 2 ;;
    esac
    shift
done

CURL_VERSION="$($CURL --version 2>/dev/null | head -1)"
[ -z "$LABEL" ] && LABEL="$(basename "$CURL")"

# Every request is bounded. A conformance matrix that can hang is as useless as
# a harness that can hang: the same lesson the C# harnesses learned the hard way
# when an unbounded write blocked over TLS while failing fast over cleartext.
# Nothing here legitimately takes more than a second or two, so a generous
# ceiling still turns a hang into a failure with a name on it.
TIMEOUTS=(--max-time 20 --connect-timeout 5)

# HTTP/2 in the build changes what a bare request means, so say which one this
# is. A curl that *cannot* upgrade and one that can but is told not to are two
# different tests, and both are worth having.
if $CURL --version 2>/dev/null | grep -qi "HTTP2"; then
    HAS_H2="yes"
else
    HAS_H2="no"
fi

TMP="$(mktemp -d -t curlmatrix.XXXXXX)"
trap 'rm -rf "$TMP"' EXIT

if [ -t 1 ]; then
    RED=$'\033[31m'; GREEN=$'\033[32m'; DIM=$'\033[2m'; OFF=$'\033[0m'
else
    RED=""; GREEN=""; DIM=""; OFF=""
fi

TOTAL=0
FAILED=0

pass() { TOTAL=$((TOTAL+1)); printf '  %s✓%s %s\n' "$GREEN" "$OFF" "$1"; }
fail() { TOTAL=$((TOTAL+1)); FAILED=$((FAILED+1)); printf '  %s✗%s %s\n' "$RED" "$OFF" "$1"; printf '      %s%s%s\n' "$DIM" "$2" "$OFF"; }

# Assert on a --write-out field.
#   wo <label> <format> <expected> <curl args...>
#
# CRs are stripped: the Windows curl terminates --write-out newlines with CRLF,
# so a multi-line expectation would never match on that platform while matching
# fine on Linux. Normalising here keeps the checks identical across both builds.
wo() {
    local label="$1" fmt="$2" expect="$3"; shift 3
    local got
    got="$($CURL -s $INSECURE "${TIMEOUTS[@]}" -o /dev/null -w "$fmt" "$@" 2>/dev/null | tr -d '\r')"
    if [ "$got" = "$expect" ]; then pass "$label"; else fail "$label" "expected '$expect', got '$got'"; fi
}

# Assert the response body/headers contain a pattern.
#   has <label> <pattern> <curl args...>
has() {
    local label="$1" needle="$2"; shift 2
    local out
    out="$($CURL -s $INSECURE "${TIMEOUTS[@]}" "$@" 2>/dev/null)"
    if printf '%s' "$out" | grep -qi -- "$needle"; then
        pass "$label"
    else
        fail "$label" "no '$needle' in: $(printf '%s' "$out" | head -c 160 | tr '\n' ' ')"
    fi
}

# Assert the response body/headers do NOT contain a pattern.
hasnt() {
    local label="$1" needle="$2"; shift 2
    local out
    out="$($CURL -s $INSECURE "${TIMEOUTS[@]}" "$@" 2>/dev/null)"
    if printf '%s' "$out" | grep -qi -- "$needle"; then
        fail "$label" "unexpected '$needle' in: $(printf '%s' "$out" | head -c 160 | tr '\n' ' ')"
    else
        pass "$label"
    fi
}

# Assert curl's own exit code — its verdict on the exchange, not ours.
exits() {
    local label="$1" expect="$2"; shift 2
    $CURL -s $INSECURE "${TIMEOUTS[@]}" -o /dev/null "$@" 2>/dev/null
    local got=$?
    if [ "$got" = "$expect" ]; then pass "$label"; else fail "$label" "expected exit $expect, got $got"; fi
}

# Assert on curl's verbose wire trace — what actually went over the socket.
trace() {
    local label="$1" needle="$2"; shift 2
    local out
    out="$($CURL -s $INSECURE "${TIMEOUTS[@]}" -v -o /dev/null "$@" 2>&1)"
    if printf '%s' "$out" | grep -qi -- "$needle"; then
        pass "$label"
    else
        fail "$label" "no '$needle' in trace: $(printf '%s' "$out" | grep -E '^[<>]' | head -c 200 | tr '\n' ' ')"
    fi
}

echo
echo "=== curl matrix — $LABEL → $BASE ==="
echo "    $CURL_VERSION"
echo "    HTTP/2 in this build: $HAS_H2"
echo

# ---------------------------------------------------------------------------
# Protocol version
# ---------------------------------------------------------------------------
echo "  -- version --"
wo    "default request negotiates HTTP/1.1" '%{http_version}' "1.1"  "$BASE/"
wo    "--http1.1 stays on 1.1"              '%{http_version}' "1.1"  --http1.1 "$BASE/"
# curl reports HTTP/1.0 as "1", not "1.0" — its own formatting, not ours.
wo    "--http1.0 downgrades the exchange"   '%{http_version}' "1"    --http1.0 "$BASE/"
has   "HTTP/1.0 response closes"            "Connection: close"      --http1.0 -D- "$BASE/"
has   "HTTP/1.0 status line echoes the version" "HTTP/1.0 200"       --http1.0 -D- "$BASE/"

if [ "$HAS_H2" = "yes" ]; then
    # The interesting direction: a client that *could* upgrade and does not.
    # A build without HTTP/2 cannot prove this at all.
    wo "--http1.1 honoured by an HTTP/2-capable curl" '%{http_version}' "1.1" --http1.1 "$BASE/"
fi

# ---------------------------------------------------------------------------
# Methods
# ---------------------------------------------------------------------------
echo "  -- methods --"
wo    "GET /"                          '%{http_code}' "200"  "$BASE/"
wo    "HEAD via -I"                    '%{http_code}' "200"  -I "$BASE/"
wo    "HEAD sends no body"             '%{size_download}' "0" -I "$BASE/"
wo    "POST -d"                        '%{http_code}' "200"  -X POST -d "hello" "$BASE/echo"
has   "POST body is echoed"            "hello"                -X POST -d "hello" "$BASE/echo"
wo    "OPTIONS on a resource"          '%{http_code}' "204"  -X OPTIONS "$BASE/files/resource.txt"
has   "resource OPTIONS lists Allow"   "Allow:"               -X OPTIONS -D- "$BASE/files/resource.txt"
wo    "server-wide OPTIONS *"          '%{http_code}' "204"  --request-target '*' -X OPTIONS "$BASE"
wo    "405 for an unsupported method"  '%{http_code}' "405"  -X DELETE "$BASE/files/resource.txt"
has   "405 carries Allow"              "Allow:"               -X DELETE -D- "$BASE/files/resource.txt"
wo    "QUERY (RFC 10008)"              '%{http_code}' "200"  -X QUERY -d "ap" "$BASE/search"
has   "QUERY filters"                  "apple"                -X QUERY -d "ap" "$BASE/search"

# ---------------------------------------------------------------------------
# Framing — curl generating and consuming real bodies
# ---------------------------------------------------------------------------
echo "  -- framing --"
wo    "fixed-length response length"   '%{size_download}' "131072" "$BASE/large"
has   "chunked response is reassembled" "chunk-three"               "$BASE/chunked"
has   "trailered response body"        "body with trailers"         "$BASE/trailers"

# -T with stdin makes curl chunk the *request*: it cannot know the length up
# front, so it must use Transfer-Encoding. A real client emitting real chunks,
# which is a different thing from our harness emitting hand-written ones.
echo "chunky payload" > "$TMP/up.txt"
trace "curl chunks an unsized upload" "Transfer-Encoding: chunked" -T - "$BASE/echo" < "$TMP/up.txt"
has   "chunked upload arrives intact"  "chunky payload"      -T - "$BASE/echo" < "$TMP/up.txt"

# ---------------------------------------------------------------------------
# Expect: 100-continue
# ---------------------------------------------------------------------------
echo "  -- expect --"
trace "explicit Expect gets 100 Continue" "100 Continue"  -X POST -H "Expect: 100-continue" -d "payload" "$BASE/expect"
wo    "body accepted after 100"         '%{http_code}' "200" -X POST -H "Expect: 100-continue" -d "payload" "$BASE/expect"
wo    "unsupported expectation → 417"   '%{http_code}' "417" -X POST -H "Expect: the-impossible" -d "payload" "$BASE/expect"
wo    "Expect suppressed by -H 'Expect:'" '%{http_code}' "200" -X POST -H "Expect:" -d "payload" "$BASE/expect"

# ---------------------------------------------------------------------------
# Connection reuse
# ---------------------------------------------------------------------------
echo "  -- connections --"
# Three URLs in one invocation: curl opens one connection and reuses it. Its own
# accounting is the assertion, which makes this a claim about what curl observed
# rather than what we hoped.
#
# Two curl details that both have to be right for this to mean anything:
#   * -o applies per URL, so it has to be repeated — otherwise the second and
#     third bodies land on stdout and mix into the --write-out output;
#   * --write-out is emitted once per URL with no separator of its own, so the
#     format has to supply one. "1,0,0," is one new connection and two reuses.
wo    "3 requests reuse one connection" '%{num_connects},' "1,0,0," \
      -o /dev/null "$BASE/" -o /dev/null "$BASE/" -o /dev/null "$BASE/"

# ---------------------------------------------------------------------------
# Conditional requests — curl's own ETag store
# ---------------------------------------------------------------------------
echo "  -- conditional --"
$CURL -s $INSECURE "${TIMEOUTS[@]}" -o /dev/null --etag-save "$TMP/etag" "$BASE/files/resource.txt" 2>/dev/null
if [ -s "$TMP/etag" ]; then
    pass "curl stored the ETag ($(cat "$TMP/etag"))"
else
    fail "curl stored the ETag" "--etag-save produced nothing"
fi
wo    "--etag-compare → 304"           '%{http_code}' "304" --etag-compare "$TMP/etag" "$BASE/files/resource.txt"
wo    "-z after Last-Modified → 304"   '%{http_code}' "304" -z "Wed, 01 Jul 2026 00:00:00 GMT" "$BASE/files/resource.txt"
wo    "-z before Last-Modified → 200"  '%{http_code}' "200" -z "Mon, 01 Jan 2024 00:00:00 GMT" "$BASE/files/resource.txt"

# ---------------------------------------------------------------------------
# Ranges
# ---------------------------------------------------------------------------
echo "  -- ranges --"
wo    "-r 0-4 → 206"                   '%{http_code}' "206" -r 0-4   "$BASE/files/resource.txt"
has   "-r 0-4 body"                    "Hello"               -r 0-4   "$BASE/files/resource.txt"
wo    "-r -6 (suffix) → 206"           '%{http_code}' "206" -r -6    "$BASE/files/resource.txt"
wo    "-r 9999- → 416"                 '%{http_code}' "416" -r 9999- "$BASE/files/resource.txt"
# -C makes curl build the Range itself from a resume offset — the same mechanism
# every download manager uses.
wo    "-C 5 (resume) → 206"            '%{http_code}' "206" -C 5     "$BASE/files/resource.txt"

# ---------------------------------------------------------------------------
# Content negotiation
# ---------------------------------------------------------------------------
echo "  -- negotiation --"
has   "default variant"                "Hello World"    "$BASE/files/greeting"
has   "Accept-Language: de"            "Hallo Welt"     -H "Accept-Language: de" "$BASE/files/greeting"
has   "Accept: application/json"       "greeting"       -H "Accept: application/json" "$BASE/files/greeting"
has   "negotiated response carries Vary" "Vary:"        -D- "$BASE/files/greeting"

# ---------------------------------------------------------------------------
# Authentication
# ---------------------------------------------------------------------------
echo "  -- auth --"
wo    "anonymous → 401"                '%{http_code}' "401" "$BASE/secret"
has   "401 carries a challenge"        "WWW-Authenticate:" -D- "$BASE/secret"
wo    "-u (Basic) → 200"               '%{http_code}' "200" -u alice:secret "$BASE/secret"
wo    "-u with wrong password → 401"   '%{http_code}' "401" -u alice:wrong  "$BASE/secret"
wo    "Bearer token → 200"             '%{http_code}' "200" -H "Authorization: Bearer valid-token-123" "$BASE/secret"
# --anyauth makes curl probe first, parse WWW-Authenticate, and pick a scheme.
# It passing means our challenge is not just present but *parseable* by an
# independent implementation.
wo    "--anyauth negotiates from the challenge" '%{http_code}' "200" --anyauth -u alice:secret "$BASE/secret"

# --digest is an expected failure, and it is documented as one: Hermod's
# HTTPDigestAuthentication is not RFC 7616 (PLAN.md, H-3), so curl's Digest
# implementation cannot authenticate against it. Pinning it here means the day
# H-3 is fixed, this check starts failing and says so.
wo    "--digest fails (expected — H-3, not RFC 7616)" '%{http_code}' "401" --digest -u alice:secret "$BASE/secret"

# ---------------------------------------------------------------------------
# Content coding
# ---------------------------------------------------------------------------
echo "  -- content coding --"
# The server has no codec at all (H-2). What matters is that it degrades
# cleanly rather than emitting something it cannot produce: curl asks for
# gzip/br/zstd and must still get a correct identity response.
wo    "--compressed still succeeds"    '%{http_code}' "200" --compressed "$BASE/"
has   "--compressed yields identity content" "Hermod HTTP/1.1 demo host" --compressed "$BASE/"
hasnt "no Content-Encoding is claimed" "Content-Encoding:" --compressed -D- "$BASE/"

# ---------------------------------------------------------------------------
# Redirects
# ---------------------------------------------------------------------------
echo "  -- redirects --"
for code in 301 302 303 307; do
    wo "redirect $code"                '%{http_code}' "$code" "$BASE/redirect/$code"
done
wo    "-L follows to the target"       '%{http_code}' "200" -L "$BASE/redirect/301"
wo    "-L counts one redirect"         '%{num_redirects}' "1" -L "$BASE/redirect/302"

# ---------------------------------------------------------------------------
# curl's own verdicts
# ---------------------------------------------------------------------------
echo "  -- curl exit codes --"
exits "--fail on 404 → exit 22"        22 --fail "$BASE/status/404"
exits "successful request → exit 0"     0        "$BASE/"

# ---------------------------------------------------------------------------
echo
if [ "$FAILED" -eq 0 ]; then
    printf '  %scurl-matrix (%s): %d/%d checks passed%s\n' "$GREEN" "$LABEL" "$TOTAL" "$TOTAL" "$OFF"
    exit 0
else
    printf '  %scurl-matrix (%s): %d/%d checks passed%s\n' "$RED" "$LABEL" "$((TOTAL-FAILED))" "$TOTAL" "$OFF"
    exit 1
fi
