#!/usr/bin/env bash
#
# A6 — the two third-party smuggling probes.
#
# tests/h1desync and tests/smuggle.sh are ours, and that is their weakness:
# the payloads they send are the ones we thought of. These two tools were
# written by people whose job was finding desyncs in other people's servers,
# and they bring payload sets nobody here designed.
#
#   smuggler (defparam)      134 obfuscations of the Transfer-Encoding line,
#                            each tried as CL.TE and as TE.CL - a brute-force
#                            sweep of the byte space around the field name and
#                            the colon (0x01, 0x0b, 0x0c, 0x7f, 0xa0, 0xff …),
#                            which is exactly the part h1desync does not
#                            enumerate. Detection is by TIMING: a desynced
#                            server waits for a body that never comes.
#
#   h2csmuggler (BishopFox)  whether an HTTP/1.1 upgrade to h2c is honoured
#                            and can be tunnelled through. It MUST find
#                            nothing here - this server implements no h2c
#                            upgrade - and that negative is the point. If h2c
#                            support is ever added, this is the line that
#                            notices.
#
# Both are checked out on demand under tests/thirdparty/ (gitignored) and
# pinned to a commit, so a run is reproducible and an upstream change is a
# decision rather than a surprise.
#
# Usage:
#     tests/smuggler.sh                              # starts the demo itself
#     tests/smuggler.sh --base http://127.0.0.1:8080 # or point it somewhere
#     tests/smuggler.sh --only smuggler
#     tests/smuggler.sh --only h2c
#     tests/smuggler.sh --config exhaustive          # smuggler's larger set
#     tests/smuggler.sh --no-fetch                   # skip rather than clone
#
# PREREQUISITES
#
# python3 for both, plus the `h2` package for h2csmuggler, which this installs
# into a venv under tests/thirdparty/. smuggler itself is stdlib-only. A
# missing prerequisite is a SKIP with its reason on the line, never a pass.

set -uo pipefail

ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
THIRD="$ROOT/tests/thirdparty"

# Pinned on 2026-09-27. Both repositories are quiet - the newest commit in
# either predates this by years - so a moving checkout would buy nothing and
# cost reproducibility.
SMUGGLER_REPO="https://github.com/defparam/smuggler"
SMUGGLER_PIN="2be871e6151ce85167a277fab21c74c851d8b20b"
H2C_REPO="https://github.com/BishopFox/h2csmuggler"
H2C_PIN="7ea573a9b13bfce6d0f5d4a96a82e637a9dafe2e"

BASE=""
ONLY=""
CONFIG="default"
FETCH=1
TIMEOUT=3

while [ $# -gt 0 ]; do
    case "$1" in
        --base)      BASE="${2:-}"; shift ;;
        --only)      ONLY="${2:-}"; shift ;;
        --config)    CONFIG="${2:-}"; shift ;;
        --timeout)   TIMEOUT="${2:-}"; shift ;;
        --no-fetch)  FETCH=0 ;;
        -h|--help)   sed -n '2,42p' "${BASH_SOURCE[0]}" | sed 's/^# \{0,1\}//'; exit 0 ;;
        *)           echo "unknown option: $1" >&2; exit 2 ;;
    esac
    shift
done

if [ -t 1 ]; then
    RED=$'\033[31m'; GREEN=$'\033[32m'; YELLOW=$'\033[33m'; CYAN=$'\033[36m'; DIM=$'\033[2m'; OFF=$'\033[0m'
else
    RED=""; GREEN=""; YELLOW=""; CYAN=""; DIM=""; OFF=""
fi

CR=$'\r'
NL=$'\n'

TOTAL=0
FAILED=0
SKIPPED=0

pass()    { TOTAL=$((TOTAL+1)); printf '    %s✓%s %s\n' "$GREEN" "$OFF" "$1"; }
failure() { TOTAL=$((TOTAL+1)); FAILED=$((FAILED+1)); printf '    %s✗%s %s\n' "$RED" "$OFF" "$1"; [ -n "${2:-}" ] && printf '        %s%s%s\n' "$DIM" "$2" "$OFF"; }
skipped() { SKIPPED=$((SKIPPED+1)); printf '    %s~%s %s %s(%s)%s\n' "$YELLOW" "$OFF" "$1" "$DIM" "${2:-}" "$OFF"; }
note()    { printf '  %s%s%s\n' "$DIM" "$1" "$OFF"; }

wants() { [ -z "$ONLY" ] || [ "$ONLY" = "$1" ]; }

# Git Bash calls it `python`; Debian calls it `python3`. Neither reliably has
# the other, so take whichever is a Python 3.
PY=""
for candidate in python3 python; do
    if command -v "$candidate" > /dev/null 2>&1 &&
       "$candidate" -c 'import sys; sys.exit(0 if sys.version_info[0] == 3 else 1)' 2>/dev/null; then
        PY="$candidate"
        break
    fi
done

# checkout <dir> <repo> <pin>
checkout() {

    local dir="$1" repo="$2" pin="$3"

    if [ -d "$dir/.git" ]; then
        [ "$(git -C "$dir" rev-parse HEAD 2>/dev/null)" = "$pin" ] && return 0
        git -C "$dir" fetch --quiet origin "$pin" 2>/dev/null
        git -C "$dir" checkout --quiet "$pin" 2>/dev/null && return 0
    fi

    [ "$FETCH" -eq 1 ] || return 1

    mkdir -p "$THIRD"
    rm -rf "$dir"
    git clone --quiet "$repo" "$dir" > /dev/null 2>&1 || return 1
    git -C "$dir" checkout --quiet "$pin" > /dev/null 2>&1 || return 1

}

echo
echo "  ${CYAN}smuggler — A6: payload sets nobody here designed${OFF}"
note "checkouts:  tests/thirdparty/ (gitignored, pinned)"

if [ -z "$PY" ]; then
    echo
    echo "  ${YELLOW}SKIP${OFF}  no python3 on PATH — both tools are Python"
    exit 0
fi

# ---------------------------------------------------------------------------
# The demo host
#
# Started here rather than by tests/run-tests.sh, the way tests/autobahn.sh
# does it, because this driver is a nightly of its own: it needs Python and a
# checkout from GitHub, neither of which belongs in a push gate. Passing
# --base points it at something already running instead, which is how it gets
# aimed at a proxy chain later.
# ---------------------------------------------------------------------------

DEMO_PID=""
DEMO_LOG=""

stop_demo() {
    if [ -n "$DEMO_PID" ]; then
        kill "$DEMO_PID" 2>/dev/null
        wait "$DEMO_PID" 2>/dev/null
    fi
    [ -n "$DEMO_LOG" ] && rm -f "$DEMO_LOG"
}
trap stop_demo EXIT

if [ -z "$BASE" ]; then

    BASE="http://127.0.0.1:8080"

    if curl -s -o /dev/null --max-time 2 "$BASE/" 2>/dev/null; then
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

        DEMO_LOG="$(mktemp -t h1-smuggler-demo.XXXXXX.log)"
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
            tail -20 "$DEMO_LOG" >&2
            exit 1
        fi

        note "demo:       started (pid $DEMO_PID)"

    fi

fi

note "target:     $BASE"

# ---------------------------------------------------------------------------
# smuggler (defparam) — the Transfer-Encoding obfuscation sweep
# ---------------------------------------------------------------------------

if wants smuggler; then

    echo
    echo "  -- smuggler: CL.TE / TE.CL over every Transfer-Encoding obfuscation it knows --"

    DIR="$THIRD/smuggler"

    if ! checkout "$DIR" "$SMUGGLER_REPO" "$SMUGGLER_PIN"; then
        skipped "smuggler" "could not check out $SMUGGLER_REPO at ${SMUGGLER_PIN:0:12}"
    else

        # It writes a file into payloads/ for every finding and never cleans
        # up, so a stale file from an earlier run would be counted as today's.
        rm -f "$DIR"/payloads/*.txt 2>/dev/null

        # How many mutations the chosen config declares. The config is plain
        # Python filling a `mutations` dict, so it can be counted by running
        # it with a stub for the one class it takes from the tool. Hard-coding
        # "about 134" would go stale the first time upstream adds a byte to
        # the sweep, and would say nothing at all for --config doubles.
        expected="$("$PY" -c '
import sys
mutations = {}
class Payload:
    def __init__(self):
        self.header = ""
        self.body   = ""
exec(open(sys.argv[1]).read(), {"mutations": mutations, "Payload": Payload})
print(len(mutations))
' "$DIR/configs/$CONFIG.py" 2>/dev/null)"

        OUT="$(mktemp -t smuggler.XXXXXX.log)"

        # -u must point at an endpoint that accepts a body, or every probe is
        # answered 404 before the framing is ever looked at.
        #
        # -c takes a bare NAME rather than a path: smuggler decides whether
        # the value is absolute by testing configfile[1] == '/', which
        # "D:/..." fails, so it prepends its own configs/ directory to the
        # whole thing and then reports the file as missing.
        #
        # -q is deliberately NOT passed. Quiet mode prints only findings,
        # and the per-mutation lines it suppresses are the only evidence
        # that the scan covered the set rather than stopping after one.
        "$PY" "$DIR/smuggler.py" \
              -u "${BASE%/}/echo" \
              -m POST \
              --no-color \
              -t "$TIMEOUT" \
              -c "$CONFIG.py" > "$OUT" 2>&1
        status=$?

        # Its output is carriage-return animated, so flatten it before
        # counting anything.
        flat="$(tr "$CR" "$NL" < "$OUT")"

        findings="$(printf '%s\n' "$flat" | grep -c "Issue Found")"
        probed="$(printf '%s\n'  "$flat" | grep -cE ': (OK \(|Potential)')"
        saved="$(ls "$DIR"/payloads/*.txt 2>/dev/null | wc -l | tr -d ' ')"

        # A tool that did not run reports no findings, and so does a tool that
        # ran and found nothing. The second check is therefore gated on the
        # first, and "it ran" means it covered the whole set rather than
        # whatever it got through before something went wrong.
        if [ "$status" -ne 0 ]; then

            failure "smuggler ran to completion" \
                    "exit $status: $(printf '%s\n' "$flat" | grep -v '^$' | tail -2 | tr "$NL" ' ')"
            failure "no CL.TE or TE.CL issue reported" \
                    "not established — the scan did not finish"

        elif [ -z "$expected" ] || [ "$probed" -lt "$expected" ]; then

            failure "smuggler covered the whole '$CONFIG' set" \
                    "probed $probed of ${expected:-?} mutations"
            failure "no CL.TE or TE.CL issue reported" \
                    "not established — the set was not covered"

        else

            pass "smuggler probed $probed/$expected mutations of '$CONFIG', CL.TE and TE.CL each"

            if [ "$findings" -eq 0 ] && [ "$saved" -eq 0 ]; then
                pass "no CL.TE or TE.CL issue reported"
            else
                failure "no CL.TE or TE.CL issue reported" \
                        "$findings reported, $saved payload file(s) written to $DIR/payloads/"
                printf '%s\n' "$flat" | grep "Issue Found" | sed 's/^/        /'
            fi

        fi

        rm -f "$OUT"

    fi

fi

# ---------------------------------------------------------------------------
# h2csmuggler (BishopFox) — the negative that has to stay negative
# ---------------------------------------------------------------------------

if wants h2c; then

    echo
    echo "  -- h2csmuggler: is an h2c upgrade honoured? --"

    DIR="$THIRD/h2csmuggler"
    VENV="$THIRD/h2c-venv"

    if ! checkout "$DIR" "$H2C_REPO" "$H2C_PIN"; then
        skipped "h2csmuggler" "could not check out $H2C_REPO at ${H2C_PIN:0:12}"
    else

        # The venv layout differs by platform; Git Bash sees the Windows one.
        venv_python() {
            if [ -x "$VENV/Scripts/python.exe" ]; then
                echo "$VENV/Scripts/python.exe"
            else
                echo "$VENV/bin/python"
            fi
        }

        VPY="$(venv_python)"

        if ! "$VPY" -c 'import h2' > /dev/null 2>&1; then
            "$PY" -m venv "$VENV" > /dev/null 2>&1
            VPY="$(venv_python)"
            "$VPY" -m pip install --quiet --disable-pip-version-check h2 > /dev/null 2>&1
        fi

        if ! "$VPY" -c 'import h2' > /dev/null 2>&1; then
            skipped "h2csmuggler" "the 'h2' package could not be installed into $VENV"
        else

            LIST="$(mktemp -t h2curls.XXXXXX.txt)"
            printf '%s/\n' "${BASE%/}" > "$LIST"

            OUT="$("$VPY" "$DIR/h2csmuggler.py" --scan-list "$LIST" -m 5 2>&1)"

            rm -f "$LIST"

            # "Failed to upgrade" is the answer we want. The success markers
            # are looked for separately rather than inferred from its absence,
            # because a tool that crashed also fails to print the success line
            # and must not be read as a pass.
            if printf '%s\n' "$OUT" | grep -q "Success!\|h2c stream established"; then
                failure "no h2c upgrade is honoured" \
                        "$(printf '%s\n' "$OUT" | grep 'Success!\|h2c stream' | head -1)"

            elif printf '%s\n' "$OUT" | grep -q "Failed to upgrade"; then
                pass "no h2c upgrade is honoured — there is no h2c surface to smuggle through"

            else
                failure "h2csmuggler reached a verdict" \
                        "neither marker in its output: $(printf '%s\n' "$OUT" | tail -2 | tr "$NL" ' ')"
            fi

        fi

    fi

fi

# ---------------------------------------------------------------------------

echo
if [ "$SKIPPED" -gt 0 ]; then
    note "$SKIPPED check(s) skipped — each with a reason above"
fi

if [ "$TOTAL" -eq 0 ]; then
    echo "  ${YELLOW}SKIP${OFF}  nothing ran"
    exit 0
fi

if [ "$FAILED" -eq 0 ]; then
    printf '  %ssmuggler (third-party): %d/%d checks passed%s\n' "$GREEN" "$TOTAL" "$TOTAL" "$OFF"
    exit 0
else
    printf '  %ssmuggler (third-party): %d/%d checks passed%s\n' "$RED" "$((TOTAL-FAILED))" "$TOTAL" "$OFF"
    exit 1
fi
