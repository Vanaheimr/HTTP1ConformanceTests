#!/usr/bin/env bash
#
# RFC 9421 — HTTP Message Signatures against a peer that is not .NET.
#
# Hermod#357 brought RFC 9421 and measured it against the RFC's examples and
# against itself. Both are .NET reading the RFC the way its author read it.
# This driver puts pyauth's http-message-signatures on the other end, a
# Python implementation with its own signature base, its own Structured Field
# parser and its own crypto (pyca/cryptography over OpenSSL):
#
#   the peer signs, the demo verifies    /signatures/verify, with every one of
#                                        the RFC's five example keys, a body
#                                        under Content-Digest, and the requests
#                                        the demo must refuse, each for its own
#                                        reason
#
#   the demo signs, the peer verifies    /signatures/signed/{keyid}, over
#                                        @status and the content's fields, and
#                                        not compressed although the peer asks
#                                        for gzip
#
# Before anything is sent, the library is held against the RFC's own examples
# (Appendix B.2), and against each of them changed in one covered field. A peer
# that cannot reproduce the RFC proves nothing about the demo, and one that
# cannot say no is no witness at all.
#
# Usage:
#     tests/signatures.sh                            # starts the demo itself, both listeners
#     tests/signatures.sh --base http://127.0.0.1:8080
#     tests/signatures.sh --no-fetch                 # skip rather than pip-install
#
# An https:// base has to name the demo by something its certificate carries,
# 127.0.0.1 or localhost. From WSL, by the Windows host's address, every
# request is a misdirected one and is answered 421 (RFC 9110, Section 7.4;
# H-36), measured 2026-11-28: the cleartext leg passes from there, the TLS leg
# cannot.
#
# PREREQUISITES
#
# python3, and http-message-signatures, pinned below, which this installs into
# a venv under tests/thirdparty/ (gitignored) on its first run. A missing
# prerequisite is a SKIP with its reason on the line, never a pass.

set -uo pipefail

ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
THIRD="$ROOT/tests/thirdparty"
VENV="$THIRD/sig-venv"

# Pinned on 2026-11-28. 2.0.1 needs cryptography and, undeclared, the one name
# SupportsIndex from typing_extensions, which tests/signatures/peer.py lends it
# from typing.
PACKAGE="http-message-signatures==2.0.1"

BASES=()
FETCH=1

while [ $# -gt 0 ]; do
    case "$1" in
        --base)      BASES+=("${2:-}"); shift ;;
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

TOTAL=0
FAILED=0
SKIPPED=0

pass()    { TOTAL=$((TOTAL+1)); printf '    %s✓%s %s\n' "$GREEN" "$OFF" "$1"; }
failure() { TOTAL=$((TOTAL+1)); FAILED=$((FAILED+1)); printf '    %s✗%s %s\n' "$RED" "$OFF" "$1"; [ -n "${2:-}" ] && printf '        %s%s%s\n' "$DIM" "$2" "$OFF"; }
skipped() { SKIPPED=$((SKIPPED+1)); printf '    %s~%s %s %s(%s)%s\n' "$YELLOW" "$OFF" "$1" "$DIM" "${2:-}" "$OFF"; }
note()    { printf '  %s%s%s\n' "$DIM" "$1" "$OFF"; }

echo
echo "  ${CYAN}signatures — RFC 9421 against a peer that is not .NET${OFF}"

# Git Bash calls it `python`; Debian calls it `python3`.
PY=""
for candidate in python3 python; do
    if command -v "$candidate" > /dev/null 2>&1 &&
       "$candidate" -c 'import sys; sys.exit(0 if sys.version_info >= (3, 10) else 1)' 2>/dev/null; then
        PY="$candidate"
        break
    fi
done

if [ -z "$PY" ]; then
    echo
    echo "  ${YELLOW}SKIP${OFF}  no Python 3.10 or later on PATH"
    exit 0
fi

# The venv layout differs by platform; Git Bash sees the Windows one.
venv_python() {
    if [ -x "$VENV/Scripts/python.exe" ]; then
        echo "$VENV/Scripts/python.exe"
    else
        echo "$VENV/bin/python"
    fi
}

VPY="$(venv_python)"

installed() {
    [ "$("$VPY" -c 'import importlib.metadata as m; print(m.version("http-message-signatures"))' 2>/dev/null | tr -d '\r')" = "${PACKAGE##*==}" ]
}

if ! installed; then

    if [ "$FETCH" -eq 0 ]; then
        echo
        echo "  ${YELLOW}SKIP${OFF}  $PACKAGE is not in $VENV, and --no-fetch"
        exit 0
    fi

    note "installing $PACKAGE into tests/thirdparty/sig-venv …"
    mkdir -p "$THIRD"
    [ -x "$VPY" ] || "$PY" -m venv "$VENV" > /dev/null 2>&1
    VPY="$(venv_python)"
    "$VPY" -m pip install --quiet --disable-pip-version-check "$PACKAGE" > /dev/null 2>&1

    if ! installed; then
        echo
        echo "  ${YELLOW}SKIP${OFF}  $PACKAGE could not be installed into $VENV"
        exit 0
    fi

fi

note "peer:       $PACKAGE, $("$VPY" -c 'import cryptography; print("cryptography", cryptography.__version__)' 2>/dev/null)"

# ---------------------------------------------------------------------------
# The demo host
#
# Started here, as tests/smuggler.sh does it: this driver is a nightly of its
# own, because it installs a package on its first run, which a push gate must
# not. Both listeners by default, since @scheme and @target-uri are the one
# thing the request line does not say and the demo derives from the
# connection.
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

if [ ${#BASES[@]} -eq 0 ]; then

    BASES=("http://127.0.0.1:8080" "https://127.0.0.1:8443")

    if curl -s -o /dev/null --max-time 2 "http://127.0.0.1:8080/" 2>/dev/null; then
        note "demo:       already listening on :8080, using it"
    else

        DEMO_EXE="$ROOT/Demo/bin/Debug/net11.0/HTTP1.Demo"
        # The Windows apphost only on Windows - see the long note in
        # tests/run-tests.sh.
        case "$(uname -s)" in
            Linux|Darwin) ;;
            *) [ -f "$DEMO_EXE.exe" ] && DEMO_EXE="$DEMO_EXE.exe" ;;
        esac

        if [ ! -f "$DEMO_EXE" ]; then
            echo
            echo "  ${RED}demo host not built: $DEMO_EXE${OFF}" >&2
            echo "  run: dotnet build HTTP1.slnx" >&2
            exit 1
        fi

        DEMO_LOG="$(mktemp -t h1-signatures-demo.XXXXXX.log)"
        "$DEMO_EXE" > "$DEMO_LOG" 2>&1 &
        DEMO_PID=$!

        ready=0
        for _ in $(seq 1 60); do
            if curl -s -o /dev/null --max-time 1 "http://127.0.0.1:8080/" 2>/dev/null &&
               curl -s -k -o /dev/null --max-time 1 "https://127.0.0.1:8443/" 2>/dev/null; then
                ready=1
                break
            fi
            kill -0 "$DEMO_PID" 2>/dev/null || break
            sleep 0.5
        done

        if [ "$ready" -eq 0 ]; then
            echo
            echo "  ${RED}the demo host did not become ready${OFF}" >&2
            tail -20 "$DEMO_LOG" >&2
            exit 1
        fi

        note "demo:       started (pid $DEMO_PID)"

    fi

fi

# ---------------------------------------------------------------------------
# The peer, once per listener
# ---------------------------------------------------------------------------

PEER="$ROOT/tests/signatures/peer.py"
command -v cygpath > /dev/null 2>&1 && [ -x "$VENV/Scripts/python.exe" ] && PEER="$(cygpath -w "$PEER")"

for base in "${BASES[@]}"; do

    echo
    echo "  -- $base --"

    # -I: the venv's packages and nothing from the environment or the
    # current directory.
    output="$("$VPY" -I "$PEER" "$base" 2>&1)"
    status=$?

    ended=0
    while IFS=$'\t' read -r verdict name detail; do
        case "$verdict" in
            PASS) pass    "$name" ;;
            FAIL) failure "$name" "$detail" ;;
            SKIP) skipped "$name" "$detail" ;;
            END)  ended=1 ;;
            *)    [ -n "$verdict" ] && note "      $verdict $name $detail" ;;
        esac
    done <<< "$output"

    # A peer that stops early is a failure, not a shorter list: the lesson of
    # tests/interop.sh on 2026-11-01. The peer prints END as its last line.
    if [ "$ended" -eq 0 ]; then
        failure "peer — died before its end, exit $status" \
                "$(printf '%s\n' "$output" | grep -vE '^(PASS|FAIL|SKIP)'$'\t' | grep -v '^[[:space:]]*$' | tail -3 | tr '\n' ' ' | cut -c1-240)"
    fi

done

# ---------------------------------------------------------------------------

echo
if [ "$SKIPPED" -gt 0 ]; then
    note "$SKIPPED check(s) skipped — each with a reason above"
fi

if [ "$FAILED" -eq 0 ]; then
    printf '  %ssignatures (RFC 9421 peer): %d/%d checks passed%s\n' "$GREEN" "$TOTAL" "$TOTAL" "$OFF"
    exit 0
else
    printf '  %ssignatures (RFC 9421 peer): %d/%d checks passed%s\n' "$RED" "$((TOTAL-FAILED))" "$TOTAL" "$OFF"
    exit 1
fi
