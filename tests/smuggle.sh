#!/usr/bin/env bash
#
# A6 — the request-smuggling differential.
#
# A single origin server cannot smuggle a request past itself. The attack is a
# *disagreement*: the front end reads one message where the back end reads two,
# and nobody authorised the second one. So the only instrument that can find a
# gadget is one that asks several implementations the same question and
# compares the answers — which is what this does.
#
# tests/h1desync --observe sends a fixed catalogue of ambiguously framed
# messages and prints one line per probe saying how many messages came back,
# what they were, and whether the peer hung up. This script runs that against
# three implementations and joins the lines on the probe id:
#
#     hermod   our server, via the demo host
#     go       net/http, via tests/peers/server.go
#     node     node:http, via tests/peers/server.mjs
#
# A row where the three agree is a row where no chain built from them can
# desync. A row where they differ is a gadget, and it is reported whether or
# not anything here is at fault — "Go and Node disagree" is a true and useful
# sentence that says nothing about Hermod.
#
# WHAT MAKES IT FAIL
#
# Not a disagreement: those are the expected output, and a script that went
# red on them would be red forever and read by nobody. What fails the run is
# a disagreement that is NOT in tests/smuggle-known.txt — the same bargain
# tests/autobahn.sh strikes with its floor and tests/h1fuzz with its
# known-findings list. A row that stops disagreeing is reported too, loudly,
# because that is a line to delete.
#
# Usage:
#     tests/smuggle.sh                              # against the default demo
#     tests/smuggle.sh --base http://127.0.0.1:8080
#     tests/smuggle.sh --only go                    # one peer
#     tests/smuggle.sh --no-build
#     tests/smuggle.sh --update                     # rewrite smuggle-known.txt
#
# PLATFORM
#
# The same arrangement as tests/interop.sh: on Linux everything is native, and
# on Windows the foreign runtimes live in the Debian WSL distribution, so the
# peer and the probe that drives it both run in there and talk over the VM's
# own loopback. Only the Hermod leg crosses the boundary, and it does not have
# to: the demo is reached from the host side.

set -uo pipefail

ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"

BASE="http://127.0.0.1:8080"
ONLY=""
BUILD=1
UPDATE=0
KNOWN="$ROOT/tests/smuggle-known.txt"

while [ $# -gt 0 ]; do
    case "$1" in
        --base)      BASE="${2:-}"; shift ;;
        --only)      ONLY="${2:-}"; shift ;;
        --no-build)  BUILD=0 ;;
        --update)    UPDATE=1 ;;
        -h|--help)   sed -n '2,50p' "${BASH_SOURCE[0]}" | sed 's/^# \{0,1\}//'; exit 0 ;;
        *)           echo "unknown option: $1" >&2; exit 2 ;;
    esac
    shift
done

if [ -t 1 ]; then
    RED=$'\033[31m'; GREEN=$'\033[32m'; YELLOW=$'\033[33m'; CYAN=$'\033[36m'; DIM=$'\033[2m'; OFF=$'\033[0m'
else
    RED=""; GREEN=""; YELLOW=""; CYAN=""; DIM=""; OFF=""
fi

note() { printf '  %s%s%s\n' "$DIM" "$1" "$OFF"; }

WORK="$(mktemp -d -t h1smuggle.XXXXXX)"
trap 'rm -rf "$WORK"' EXIT

# ---------------------------------------------------------------------------
# Where the peers run — identical to tests/interop.sh, and for the same reason
# ---------------------------------------------------------------------------

PEERS_DIR="$ROOT/tests/peers"
DESYNC_DLL="$ROOT/tests/h1desync/bin/Debug/net10.0/h1desync.dll"

case "$(uname -s)" in

    Linux)
        RUN=()
        PEERS_PATH="$PEERS_DIR"
        DESYNC_PATH="$DESYNC_DLL"
        ;;

    *)
        if ! command -v wsl > /dev/null 2>&1; then
            echo "  SKIP  smuggle — no WSL, and the foreign runtimes live there" >&2
            exit 0
        fi
        RUN=(wsl -d Debian -e bash -lc)
        PEERS_PATH="$(wsl -d Debian -e wslpath -a "$(cygpath -w "$PEERS_DIR" 2>/dev/null || echo "$PEERS_DIR")" | tr -d '\r')"
        DESYNC_PATH="$(wsl -d Debian -e wslpath -a "$(cygpath -w "$DESYNC_DLL" 2>/dev/null || echo "$DESYNC_DLL")" | tr -d '\r')"
        ;;

esac

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
echo "  ${CYAN}smuggle — A6: do independent parsers agree where a message ends?${OFF}"
note "probes:     tests/h1desync --observe"
note "known:      ${KNOWN#"$ROOT/"}"
note "a trailing ! in a cell means the peer closed the connection"

if [ "$BUILD" -eq 1 ]; then
    dotnet build "$ROOT/tests/h1desync/h1desync.csproj" -v q --nologo > /dev/null 2>&1
fi

if [ ! -f "$DESYNC_DLL" ]; then
    echo "  ${RED}h1desync is not built: $DESYNC_DLL${OFF}" >&2
    exit 1
fi

COLUMNS_PRESENT=()

# --- hermod: run on this side, against the demo -----------------------------

if wants hermod; then
    if dotnet "$DESYNC_DLL" --observe --base "$BASE" > "$WORK/hermod.obs" 2>"$WORK/hermod.err" \
       && [ -s "$WORK/hermod.obs" ]; then
        COLUMNS_PRESENT+=(hermod)
        note "hermod:     $BASE  ($(wc -l < "$WORK/hermod.obs" | tr -d ' ') probes)"
    else
        echo "  ${RED}hermod leg produced nothing — is the demo up at $BASE?${OFF}" >&2
        head -3 "$WORK/hermod.err" >&2
        exit 1
    fi
fi

# --- the foreign peers: peer and probe both where the runtimes are ----------

run_peer_column() {

    local label="$1" command="$2" runtime="$3" port="$4"

    wants "$label" || return 0

    if ! peer "command -v $runtime > /dev/null 2>&1"; then
        note "$label:       skipped — $runtime is not installed where the peers run"
        return 0
    fi

    # The peer prints LISTENING <port> once it is accepting, so this waits on
    # a line rather than on a guess at a startup delay.
    peer "($command $port > /tmp/h1smuggle-$label.log 2>&1 &) ; \
          for i in \$(seq 1 60); do grep -q LISTENING /tmp/h1smuggle-$label.log 2>/dev/null && break; sleep 0.5; done ; \
          dotnet '$DESYNC_PATH' --observe --base http://127.0.0.1:$port 2>/dev/null ; \
          pkill -f '$command' > /dev/null 2>&1 ; true" > "$WORK/$label.obs" 2>/dev/null

    if [ -s "$WORK/$label.obs" ]; then
        COLUMNS_PRESENT+=("$label")
        note "$label:       127.0.0.1:$port  ($(grep -c '^OBS' "$WORK/$label.obs" | tr -d ' ') probes)"
    else
        note "$label:       skipped — the peer produced no observations"
    fi

}

run_peer_column go   "go run server.go"  go   18090
run_peer_column node "node server.mjs"   node 18091

if [ "${#COLUMNS_PRESENT[@]}" -lt 2 ]; then
    echo
    echo "  ${YELLOW}SKIP${OFF}  only ${#COLUMNS_PRESENT[@]} implementation reachable — a differential needs at least two"
    exit 0
fi

# ---------------------------------------------------------------------------
# Join on the probe id and compare
# ---------------------------------------------------------------------------

# class_of <column> <probe-id>  ->  the comparable verdict, or "-"
class_of() {
    awk -F'\t' -v id="$2" '$1=="OBS" && $2==id { print $3; found=1 } END { if (!found) print "-" }' \
        "$WORK/$1.obs" | head -1
}

# The cell the table shows. The close state is part of it, because RFC 9112
# Section 6.1 requires the close and permits either answer before it: "200 and
# hung up" and "200 and kept the connection" are a compliance and a violation,
# and a table printing them identically would hide the very difference it
# exists to show.
codes_of() {
    awk -F'\t' -v id="$2" '$1=="OBS" && $2==id { print $3 "[" $4 "]" ($5=="closed" ? "!" : ""); found=1 } END { if (!found) print "-" }' \
        "$WORK/$1.obs" | head -1
}

# The probe order is the catalogue's order, taken from whichever column ran
# first, so the table reads in the same groups h1desync prints.
PROBE_IDS="$(awk -F'\t' '$1=="OBS" { print $2 }' "$WORK/${COLUMNS_PRESENT[0]}.obs")"

touch "$KNOWN"

echo
printf '  %-24s' "probe"
for column in "${COLUMNS_PRESENT[@]}"; do printf '%-22s' "$column"; done
echo
printf '  %-24s' "------------------------"
for _ in "${COLUMNS_PRESENT[@]}"; do printf '%-22s' "--------------------"; done
echo

TOTAL=0
AGREED=0
KNOWN_DIFF=0
NEW_DIFF=0
GONE=()
NEW_ROWS=()
ALL_ROWS=()

while IFS= read -r id; do

    [ -n "$id" ] || continue

    TOTAL=$((TOTAL + 1))

    classes=()
    for column in "${COLUMNS_PRESENT[@]}"; do
        classes+=("$(class_of "$column" "$id")")
    done

    signature="$(IFS=,; echo "${classes[*]}")"
    ALL_ROWS+=("$id	$signature")

    # Do they all say the same thing?
    differs=0
    for c in "${classes[@]}"; do
        [ "$c" = "${classes[0]}" ] || differs=1
    done

    recorded="$(awk -F'\t' -v id="$id" '$1==id { print $2 }' "$KNOWN" | head -1)"

    printf '  %-24s' "$id"
    for column in "${COLUMNS_PRESENT[@]}"; do
        printf '%-22s' "$(codes_of "$column" "$id")"
    done

    if [ "$differs" -eq 0 ]; then
        AGREED=$((AGREED + 1))
        echo
        if [ -n "$recorded" ]; then
            GONE+=("$id  (was $recorded, now all agree on ${classes[0]})")
        fi
    elif [ "$signature" = "$recorded" ]; then
        KNOWN_DIFF=$((KNOWN_DIFF + 1))
        printf '%s<- known%s\n' "$DIM" "$OFF"
    else
        NEW_DIFF=$((NEW_DIFF + 1))
        NEW_ROWS+=("$id	$signature${recorded:+   (recorded: $recorded)}")
        printf '%s<- NEW%s\n' "$RED" "$OFF"
    fi

done <<< "$PROBE_IDS"

# ---------------------------------------------------------------------------

if [ "$UPDATE" -eq 1 ]; then
    {
        echo "# A6 — the disagreements this differential has already seen and filed."
        echo "#"
        echo "# One line per probe on which the implementations do not agree, as"
        echo "# <probe-id> TAB <class per column, in the order the table prints them>."
        echo "# A row not listed here fails the run; a row listed here that has"
        echo "# stopped disagreeing is reported so the line can be deleted."
        echo "#"
        echo "# Columns: ${COLUMNS_PRESENT[*]}"
        echo "# Written by tests/smuggle.sh --update on $(date -u +%Y-%m-%d)."
        echo
        for row in "${ALL_ROWS[@]}"; do
            id="${row%%	*}"; sig="${row##*	}"
            first="${sig%%,*}"
            same=1
            IFS=',' read -ra parts <<< "$sig"
            for p in "${parts[@]}"; do [ "$p" = "$first" ] || same=0; done
            [ "$same" -eq 0 ] && printf '%s\t%s\n' "$id" "$sig"
        done
    } > "$KNOWN"
    echo
    echo "  ${YELLOW}--update${OFF}: rewrote ${KNOWN#"$ROOT/"}"
fi

echo
note "$TOTAL probes across ${#COLUMNS_PRESENT[@]} implementations: $AGREED agree, $((KNOWN_DIFF + NEW_DIFF)) disagree"

if [ "${#GONE[@]}" -gt 0 ]; then
    echo
    echo "  ${GREEN}no longer disagreeing${OFF} — delete these lines from ${KNOWN#"$ROOT/"}:"
    for g in "${GONE[@]}"; do echo "    $g"; done
fi

if [ "$NEW_DIFF" -gt 0 ]; then
    echo
    echo "  ${RED}new disagreements${OFF} — each one is a desync gadget for a chain of these two:"
    for r in "${NEW_ROWS[@]}"; do printf '    %s\n' "$r"; done
    echo
    printf '  %ssmuggle: %d/%d checks passed%s\n' "$RED" "$((TOTAL - NEW_DIFF))" "$TOTAL" "$OFF"
    exit 1
fi

echo
printf '  %ssmuggle: %d/%d checks passed%s\n' "$GREEN" "$TOTAL" "$TOTAL" "$OFF"
exit 0
