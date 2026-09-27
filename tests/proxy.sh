#!/usr/bin/env bash
#
# A5 — the demo host behind five reverse proxies.
#
# Reverse proxies are the strictest HTTP/1.1 consumers there are, and they are
# also the only shape in which a smuggling gadget is real: A6 found seven
# probes on which Hermod, Go and Node place the end of a message differently,
# and a disagreement only becomes an attack when two of them are chained.
#
# Four things are measured, and they are not equally strong:
#
#   curl      the whole curl matrix through each proxy. Differences from the
#             direct run are expected - a proxy is an HTTP endpoint on both
#             sides and rewrites plenty - so each one is recorded by name in
#             tests/proxy-known.txt and a NEW one fails the run.
#
#   framing   tests/h1desync --observe through each chain, against the same
#             run made directly. This says what each proxy does with an
#             ambiguously framed message: forwards it, normalises it, refuses
#             it, or splits it into two requests. Recorded the same way.
#
#   poison    the actual attack. Send the ambiguous payload, then ask innocent
#             questions on fresh connections: if an answer comes back that
#             belongs to somebody else, an upstream connection was left with a
#             response on it. READ THE CALIBRATION NOTE BELOW before believing
#             a clean result here.
#
#   via       the intermediary-facing rules that are observable from outside -
#             Via, trailers through a re-framing hop, chunked bodies arriving
#             intact, Connection: close being honoured.
#
# THE CALIBRATION NOTE
#
# The poison probe has never been seen to fire, and that is a fact about the
# proxies rather than about Hermod. Measured on 2026-09-27 with a back end
# that answers one request with two responses - the exact state a successful
# desync leaves behind, verified on the wire - all five proxies discarded the
# upstream connection rather than hand the extra response to the next client.
# So a clean poison column means "no chain here produced an attack AND these
# proxies would have absorbed one anyway", which is weaker than it looks.
# tests/thirdparty/ has the calibration script; docs/TestingAgainst_Proxies.md
# has the numbers.
#
# Usage:
#     tests/proxy.sh                       # bring the proxies up, run, tear down
#     tests/proxy.sh --keep                # leave them running
#     tests/proxy.sh --only nginx
#     tests/proxy.sh --filter framing      # curl | framing | poison | via
#     tests/proxy.sh --update              # rewrite tests/proxy-known.txt
#     tests/proxy.sh --no-build
#
# PLATFORM
#
# Docker on Windows lives inside the WSL VM, and the demo host does not: it
# runs on Windows and the containers reach it over the VM's default route,
# which is why it has to be started with --bind-any. On Linux everything is
# native and the containers use host.docker.internal. That one address is the
# whole of the difference, and tests/proxies/docker-compose.yml takes it as
# DEMO_HOST.

set -uo pipefail

ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
COMPOSE_DIR="$ROOT/tests/proxies"
KNOWN="$ROOT/tests/proxy-known.txt"

DEMO_PORT=8080
ONLY=""
FILTER=""
KEEP=0
UPDATE=0
BUILD=1

while [ $# -gt 0 ]; do
    case "$1" in
        --only)      ONLY="${2:-}"; shift ;;
        --filter)    FILTER="${2:-}"; shift ;;
        --keep)      KEEP=1 ;;
        --update)    UPDATE=1 ;;
        --no-build)  BUILD=0 ;;
        --port)      DEMO_PORT="${2:-}"; shift ;;
        -h|--help)   sed -n '2,64p' "${BASH_SOURCE[0]}" | sed 's/^# \{0,1\}//'; exit 0 ;;
        *)           echo "unknown option: $1" >&2; exit 2 ;;
    esac
    shift
done

if [ -t 1 ]; then
    RED=$'\033[31m'; GREEN=$'\033[32m'; YELLOW=$'\033[33m'; CYAN=$'\033[36m'; DIM=$'\033[2m'; OFF=$'\033[0m'
else
    RED=""; GREEN=""; YELLOW=""; CYAN=""; DIM=""; OFF=""
fi

TOTAL=0
FAILED=0
SKIPPED=0
NEW_ROWS=()
ALL_ROWS=()

pass()    { TOTAL=$((TOTAL+1)); printf '    %s✓%s %s\n' "$GREEN" "$OFF" "$1"; }
failure() { TOTAL=$((TOTAL+1)); FAILED=$((FAILED+1)); printf '    %s✗%s %s\n' "$RED" "$OFF" "$1"; [ -n "${2:-}" ] && printf '        %s%s%s\n' "$DIM" "$2" "$OFF"; }
skipped() { SKIPPED=$((SKIPPED+1)); printf '    %s~%s %s %s(%s)%s\n' "$YELLOW" "$OFF" "$1" "$DIM" "${2:-}" "$OFF"; }
note()    { printf '  %s%s%s\n' "$DIM" "$1" "$OFF"; }

wants()   { [ -z "$ONLY" ]   || [ "$ONLY" = "$1" ]; }
section() { [ -z "$FILTER" ] || [ "$FILTER" = "$1" ]; }

PROXIES="nginx:18101 haproxy:18102 caddy:18103 httpd:18104 envoy:18105"

WORK="$(mktemp -d -t h1proxy.XXXXXX)"

# ---------------------------------------------------------------------------
# Docker, and where the demo is from inside a container
# ---------------------------------------------------------------------------

case "$(uname -s)" in

    Linux)
        DOCKER=(docker)
        COMPOSE_PATH="$COMPOSE_DIR"
        # The demo runs on this host; host-gateway is in the compose file.
        DEMO_HOST="host.docker.internal"
        ;;

    *)
        if ! command -v wsl > /dev/null 2>&1; then
            echo "  SKIP  proxy — no WSL, and Docker lives there on Windows" >&2
            exit 0
        fi
        # MSYS_NO_PATHCONV, because Git Bash rewrites anything that looks
        # like a Unix path before wsl.exe sees it: /mnt/d/... arrived as
        # "C:/Program Files/Git/mnt/d/...". tests/autobahn.sh pays the same
        # toll for the same reason.
        #
        # Per command, NOT exported. Exporting it also stops Git Bash
        # translating /dev/null for native Windows binaries, so every
        # `curl -s -o /dev/null` in this file began to fail - and the first
        # of those is the check for the demo host, which then reported the
        # demo down while it was answering 200 in the next shell along. The
        # same /dev/null trap this repository's .gitignore already carries a
        # note about.
        DOCKER=(env MSYS_NO_PATHCONV=1 wsl -d Debian -e docker)
        COMPOSE_PATH="$(MSYS_NO_PATHCONV=1 wsl -d Debian -e wslpath -a "$(cygpath -w "$COMPOSE_DIR")" | tr -d '\r')"
        # The demo is on WINDOWS. From a container that is the VM's default
        # route - host.docker.internal would be the VM itself, the wrong
        # machine - and the demo has to have been started with --bind-any.
        DEMO_HOST="$(wsl -d Debian -- ip route show default 2>/dev/null | awk '{print $3}' | tr -d '\r')"
        ;;

esac

compose() {
    if [ "${DOCKER[0]}" = "docker" ]; then
        DEMO_HOST="$DEMO_HOST" docker compose --project-directory "$COMPOSE_PATH" "$@"
    else
        MSYS_NO_PATHCONV=1 wsl -d Debian -e env DEMO_HOST="$DEMO_HOST" docker compose --project-directory "$COMPOSE_PATH" "$@"
    fi
}

# Declared before cleanup() because the EXIT trap calls stop_demo, and the
# script can exit before the demo section is reached - with set -u an unbound
# DEMO_PID in a trap is an error message on top of whatever went wrong.
DEMO_PID=""
DEMO_LOG=""
KEEP_DEMO_LOG=0

# Between sections, because one run of this suite lost eighteen checks in a
# row and the output could not say why.
#
# It was not reproduced - it happened once, in a run whose predecessor had
# been killed mid-flight, and four runs since have been clean. So this does
# not fix anything. What it does is make the next occurrence name itself:
# "the demo stopped answering after framing" is a diagnosis, where eighteen
# bare crosses are eighteen things to check by hand.
demo_still_up() {

    curl -s -o /dev/null --max-time 5 "http://127.0.0.1:$DEMO_PORT/" && return 0

    echo
    echo "  ${RED}the demo host stopped answering on :$DEMO_PORT after the $1 section${OFF}" >&2
    echo "  everything below this point would fail for that one reason, so the run stops here." >&2

    if [ -n "$DEMO_LOG" ] && [ -f "$DEMO_LOG" ]; then
        KEEP_DEMO_LOG=1
        echo "  its log: $DEMO_LOG" >&2
        tail -15 "$DEMO_LOG" >&2
    fi

    exit 1

}


stop_demo() {
    if [ -n "$DEMO_PID" ]; then
        kill "$DEMO_PID" 2>/dev/null
        wait "$DEMO_PID" 2>/dev/null
    fi
    [ -n "$DEMO_LOG" ] && [ "${KEEP_DEMO_LOG:-0}" -eq 0 ] && rm -f "$DEMO_LOG"
    return 0
}


cleanup() {
    if [ "$KEEP" -eq 0 ]; then
        compose down --timeout 5 > /dev/null 2>&1
    fi
    stop_demo
    rm -rf "$WORK"
}
trap cleanup EXIT

echo
echo "  ${CYAN}proxy — A5: the demo host behind five reverse proxies${OFF}"
note "compose:    ${COMPOSE_DIR#"$ROOT/"}"
note "demo from a container:  $DEMO_HOST:$DEMO_PORT"

for tool in curl dotnet; do
    command -v "$tool" > /dev/null 2>&1 || { echo "  ${YELLOW}SKIP${OFF}  proxy — $tool is not on PATH"; exit 0; }
done

if ! "${DOCKER[@]}" info > /dev/null 2>&1; then
    echo
    echo "  ${YELLOW}SKIP${OFF}  proxy — the Docker daemon is not reachable"
    exit 0
fi

# --- the demo has to be up, and reachable from a container -----------------

# --- the demo host --------------------------------------------------------
#
# Started here when there is none, the way tests/autobahn.sh and
# tests/smuggler.sh do, so that the nightly step is one line.
#
# --bind-any is not optional on either platform, and for the same reason on
# both: the containers arrive over a bridge or over the VM's default route,
# never over loopback. A demo listening on 127.0.0.1 is invisible to every
# proxy here.

if curl -s -o /dev/null --max-time 3 "http://127.0.0.1:$DEMO_PORT/"; then
    note "demo:       already listening on :$DEMO_PORT, using it"
    note "            (it must have been started with --bind-any)"
else

    DEMO_EXE="$ROOT/Demo/bin/Debug/net10.0/HTTP1.Demo"
    [ -f "$DEMO_EXE.exe" ] && DEMO_EXE="$DEMO_EXE.exe"

    if [ ! -f "$DEMO_EXE" ]; then
        echo
        echo "  ${RED}demo host not built: $DEMO_EXE${OFF}" >&2
        echo "  run: dotnet build HTTP1.slnx" >&2
        exit 1
    fi

    DEMO_LOG="$(mktemp -t h1-proxy-demo.XXXXXX.log)"
    "$DEMO_EXE" --fast-timeouts --bind-any > "$DEMO_LOG" 2>&1 &
    DEMO_PID=$!

    ready=0
    for _ in $(seq 1 60); do
        if curl -s -o /dev/null --max-time 1 "http://127.0.0.1:$DEMO_PORT/"; then
            ready=1
            break
        fi
        kill -0 "$DEMO_PID" 2>/dev/null || break
        sleep 0.5
    done

    if [ "$ready" -eq 0 ]; then
        echo
        echo "  ${RED}the demo host did not become ready on :$DEMO_PORT${OFF}" >&2
        tail -20 "$DEMO_LOG" >&2
        exit 1
    fi

    note "demo:       started on 0.0.0.0:$DEMO_PORT (pid $DEMO_PID)"

fi

if [ "$BUILD" -eq 1 ]; then
    dotnet build "$ROOT/tests/h1desync/h1desync.csproj" -v q --nologo > /dev/null 2>&1
fi

# ---------------------------------------------------------------------------
# Bring the proxies up
# ---------------------------------------------------------------------------

echo
echo "  -- proxies --"

compose up -d > "$WORK/up.log" 2>&1
up_status=$?

if [ "$up_status" -ne 0 ]; then
    echo "  ${RED}docker compose up failed${OFF}" >&2
    tail -5 "$WORK/up.log" >&2
    exit 1
fi

REACHABLE=""
for entry in $PROXIES; do

    name="${entry%%:*}"
    port="${entry##*:}"

    wants "$name" || continue

    ready=0
    for _ in $(seq 1 40); do
        if curl -s -o /dev/null --max-time 2 "http://127.0.0.1:$port/"; then
            ready=1
            break
        fi
        sleep 0.5
    done

    if [ "$ready" -eq 1 ]; then
        REACHABLE="$REACHABLE $name:$port"
        note "$name on :$port"
    else
        skipped "$name" "never answered on :$port — $(compose logs --tail 2 "$name" 2>&1 | tail -1 | cut -c1-90)"
    fi

done

if [ -z "$REACHABLE" ]; then
    echo
    echo "  ${RED}no proxy came up${OFF}" >&2
    exit 1
fi

touch "$KNOWN"

# is_known <kind> <proxy> <detail>
is_known() {
    awk -F'\t' -v k="$1" -v p="$2" -v d="$3" \
        '$1==k && $2==p && $3==d { found=1 } END { exit found ? 0 : 1 }' "$KNOWN"
}

# record <kind> <proxy> <detail> <note>
record() {
    ALL_ROWS+=("$1	$2	$3	${4:-}")
    if is_known "$1" "$2" "$3"; then
        return 0
    fi
    NEW_ROWS+=("$1	$2	$3	${4:-}")
    return 1
}

# ---------------------------------------------------------------------------
# curl: the whole matrix, through each proxy
# ---------------------------------------------------------------------------

if section curl; then

    echo
    echo "  -- curl matrix, direct and through each proxy --"

    bash "$ROOT/tests/curl-matrix.sh" --base "http://127.0.0.1:$DEMO_PORT" --label direct > "$WORK/curl-direct.out" 2>&1
    direct_fails="$(grep -cE '^\s+✗' "$WORK/curl-direct.out")"

    if [ "$direct_fails" -ne 0 ]; then
        failure "the direct run is clean" "$direct_fails check(s) already fail without a proxy — fix that first"
    else
        pass "the direct run is clean, so a difference below is the proxy's"
    fi

    for entry in $REACHABLE; do

        name="${entry%%:*}"
        port="${entry##*:}"

        bash "$ROOT/tests/curl-matrix.sh" --base "http://127.0.0.1:$port" --label "$name" > "$WORK/curl-$name.out" 2>&1

        verdict="$(grep -E 'checks passed' "$WORK/curl-$name.out" | tail -1 | sed 's/^ *//')"
        new_for_this=0

        while IFS= read -r check; do
            [ -n "$check" ] || continue
            record curl "$name" "$check" || new_for_this=$((new_for_this + 1))
        done < <(grep -E '^\s+✗' "$WORK/curl-$name.out" | sed 's/^[[:space:]]*✗[[:space:]]*//')

        if [ "$new_for_this" -eq 0 ]; then
            pass "curl/$name — $verdict, every difference already recorded"
        else
            failure "curl/$name — $verdict" "$new_for_this difference(s) not in ${KNOWN#"$ROOT/"}"
        fi

    done

fi

# ---------------------------------------------------------------------------
# framing: what each chain does with an ambiguously framed message
# ---------------------------------------------------------------------------

demo_still_up "curl"

if section framing; then

    echo
    echo "  -- framing, direct against each chain --"

    DESYNC="$ROOT/tests/h1desync/bin/Debug/net10.0/h1desync.dll"

    if [ ! -f "$DESYNC" ]; then
        skipped "framing" "h1desync is not built"
    else

        dotnet "$DESYNC" --observe --base "http://127.0.0.1:$DEMO_PORT" > "$WORK/obs-direct" 2>/dev/null

        usable="$(awk -F'\t' '$1=="OBS" && $3!="ERROR" { n++ } END { print n+0 }' "$WORK/obs-direct")"

        if [ "$usable" -eq 0 ]; then
            failure "the direct framing run reached the demo" "every probe errored"
        else

            pass "direct: $usable probes observed, the baseline every chain is read against"

            for entry in $REACHABLE; do

                name="${entry%%:*}"
                port="${entry##*:}"

                dotnet "$DESYNC" --observe --base "http://127.0.0.1:$port" > "$WORK/obs-$name" 2>/dev/null

                chain_usable="$(awk -F'\t' '$1=="OBS" && $3!="ERROR" { n++ } END { print n+0 }' "$WORK/obs-$name")"

                if [ "$chain_usable" -eq 0 ]; then
                    failure "framing/$name" "every probe errored through this proxy"
                    continue
                fi

                differing=0
                new_for_this=0

                while IFS= read -r id; do

                    [ -n "$id" ] || continue

                    a="$(awk -F'\t' -v i="$id" '$1=="OBS" && $2==i { print $3 }' "$WORK/obs-direct")"
                    b="$(awk -F'\t' -v i="$id" '$1=="OBS" && $2==i { print $3 }' "$WORK/obs-$name")"

                    [ "$a" = "$b" ] && continue

                    differing=$((differing + 1))
                    record framing "$name" "$id" "direct=$a chain=$b" || new_for_this=$((new_for_this + 1))

                done < <(awk -F'\t' '$1=="OBS" { print $2 }' "$WORK/obs-direct")

                if [ "$new_for_this" -eq 0 ]; then
                    pass "framing/$name — $differing of $usable probes read differently, all recorded"
                else
                    failure "framing/$name — $differing of $usable read differently" "$new_for_this not in ${KNOWN#"$ROOT/"}"
                fi

            done

        fi

    fi

fi

# ---------------------------------------------------------------------------
# poison: the attack itself
# ---------------------------------------------------------------------------

demo_still_up "framing"

if section poison; then

    echo
    echo "  -- poisoning: does an ambiguous message leave a response on a pooled connection --"

    for entry in $REACHABLE; do

        name="${entry%%:*}"
        port="${entry##*:}"

        out="$("$ROOT/tests/proxy-poison.py" --port "$port" 2>&1)" ||
        out="$(python "$ROOT/tests/proxy-poison.py" --port "$port" 2>&1)" ||
        out="$(python3 "$ROOT/tests/proxy-poison.py" --port "$port" 2>&1)"

        verdict="$(printf '%s\n' "$out" | grep -E '^VERDICT' | tail -1)"

        case "$verdict" in
            "VERDICT clean"*)
                pass "poison/$name — ${verdict#VERDICT }"
                ;;
            "VERDICT poisoned"*)
                failure "poison/$name" "${verdict#VERDICT }"
                printf '%s\n' "$out" | grep -E '^POISONED' | sed 's/^/        /'
                ;;
            *)
                failure "poison/$name" "no verdict: $(printf '%s' "$out" | tail -2 | tr '\n' ' ')"
                ;;
        esac

    done

    note "a clean column here is weaker than it looks — see the calibration note in this script"

fi

# ---------------------------------------------------------------------------
# via: the intermediary-facing rules that are visible from outside
# ---------------------------------------------------------------------------

demo_still_up "poison"

if section via; then

    echo
    echo "  -- intermediary-facing behaviour --"

    for entry in $REACHABLE; do

        name="${entry%%:*}"
        port="${entry##*:}"
        base="http://127.0.0.1:$port"

        # A chunked body has to survive the hop intact, whether the proxy
        # re-frames it or passes it through.
        body="$(curl -s --max-time 10 "$base/chunked")"
        if [ "$body" = "$(printf 'chunk-one\nchunk-two\nchunk-three')" ]; then
            pass "via/$name — a chunked body arrives intact"
        else
            failure "via/$name — a chunked body arrives intact" "got: $(printf '%s' "$body" | head -c 80 | tr '\n' ' ')"
        fi

        # Trailers are the field most likely to be dropped by a re-framing
        # hop, and dropping them is allowed. Recorded, not demanded.
        trailer="$(curl -s --max-time 10 -D - -o /dev/null "$base/trailers" | grep -ci "X-Demo-Checksum" || true)"
        if [ "$trailer" -gt 0 ]; then
            pass "via/$name — the trailer survives the hop"
        else
            record via "$name" "trailer dropped" "the proxy did not forward the trailer section" \
                && pass "via/$name — the trailer is dropped, which is recorded and allowed" \
                || failure "via/$name — the trailer survives the hop" "not in ${KNOWN#"$ROOT/"}"
        fi

        # Connection: close from the client must end the client's connection
        # whatever the proxy does upstream.
        if curl -s --max-time 10 -D - -o /dev/null -H "Connection: close" "$base/" | grep -qi "^Connection: close"; then
            pass "via/$name — Connection: close is honoured downstream"
        else
            record via "$name" "no Connection: close echoed" "the proxy kept the client connection" \
                && pass "via/$name — no close echoed, which is recorded" \
                || failure "via/$name — Connection: close is honoured downstream" "not in ${KNOWN#"$ROOT/"}"
        fi

    done

fi

# ---------------------------------------------------------------------------

# Rows in the known file that nothing produced this time.
#
# The half of this bargain that is easy to leave out, and the only thing that
# keeps the list from growing forever: a proxy that starts forwarding trailers,
# or an upstream fix that makes a framing row agree again, would otherwise sit
# in the file unnoticed for good. Reported, never failed - a row can also go
# missing because a proxy did not come up.
STALE=()
while IFS= read -r line; do

    case "$line" in ""|"#"*) continue ;; esac

    kind="$(printf '%s' "$line" | cut -f1)"
    who="$(printf  '%s' "$line" | cut -f2)"
    what="$(printf '%s' "$line" | cut -f3)"

    # Only for proxies this run actually reached.
    case " $REACHABLE " in *" $who:"*) ;; *) continue ;; esac

    # Indexed, not "for row in ${ALL_ROWS[@]}" unquoted: a recorded curl check
    # is a sentence with spaces in it, and word-splitting turned one row into
    # six words that match nothing. Every row would then have looked stale.
    seen=0
    i=0
    while [ "$i" -lt "${#ALL_ROWS[@]}" ]; do
        if [ "$(printf '%s' "${ALL_ROWS[$i]}" | cut -f1-3)" = "$(printf '%s	%s	%s' "$kind" "$who" "$what")" ]; then
            seen=1
            break
        fi
        i=$((i + 1))
    done

    [ "$seen" -eq 0 ] && STALE+=("$kind	$who	$what")

done < "$KNOWN"

if [ "${#STALE[@]}" -gt 0 ] && [ "$UPDATE" -eq 0 ]; then
    echo
    echo "  ${GREEN}no longer differing${OFF} — delete these lines from ${KNOWN#"$ROOT/"}:"
    for row in "${STALE[@]}"; do printf '    %s\n' "$row"; done
fi


if [ "$UPDATE" -eq 1 ]; then
    {
        echo "# A5 — what these five proxies do differently, and that is recorded rather than demanded."
        echo "#"
        echo "# <kind> TAB <proxy> TAB <detail> TAB <note>"
        echo "#"
        echo "#   curl     a check from tests/curl-matrix.sh that passes directly and not through this proxy"
        echo "#   framing  a tests/h1desync probe this chain reads differently from the demo alone"
        echo "#   via      an intermediary behaviour that is allowed but worth knowing"
        echo "#"
        echo "# A row not listed here fails the run. Written by tests/proxy.sh --update on $(date -u +%Y-%m-%d)."
        echo
        for row in "${ALL_ROWS[@]}"; do printf '%s\n' "$row"; done | sort -u
    } > "$KNOWN"
    echo
    echo "  ${YELLOW}--update${OFF}: rewrote ${KNOWN#"$ROOT/"} (${#ALL_ROWS[@]} rows)"
fi

echo
if [ "$SKIPPED" -gt 0 ]; then
    note "$SKIPPED check(s) skipped — each with a reason above"
fi

if [ "${#NEW_ROWS[@]}" -gt 0 ] && [ "$UPDATE" -eq 0 ]; then
    echo "  ${RED}not in ${KNOWN#"$ROOT/"}${OFF}:"
    for row in "${NEW_ROWS[@]}"; do printf '    %s\n' "$row"; done
    echo
fi

if [ "$FAILED" -eq 0 ]; then
    printf '  %sproxy (A5): %d/%d checks passed%s\n' "$GREEN" "$TOTAL" "$TOTAL" "$OFF"
    exit 0
else
    printf '  %sproxy (A5): %d/%d checks passed%s\n' "$RED" "$((TOTAL-FAILED))" "$TOTAL" "$OFF"
    exit 1
fi
