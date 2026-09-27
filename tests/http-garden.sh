#!/usr/bin/env bash
#
# A6 — Hermod in the HTTP Garden.
#
# tests/smuggle.sh compares three implementations on 38 probes we wrote. The
# HTTP Garden (github.com/narfindustries/http-garden) compares forty-five on
# payloads it mutates itself, and it compares something much sharper than a
# response count: every target in the Garden answers with a JSON description
# of the request AS IT PARSED IT - method, version, URI, header fields, body -
# so a disagreement is visible field by field rather than inferred from how
# many responses came back.
#
# This script puts Hermod in it. tests/http-garden/ holds the image: a
# Dockerfile on the Garden's own pattern, and an app (HermodGarden) that runs
# Hermod's HTTP/1.1 server on 0.0.0.0:443 and answers in the Garden's format.
# Everything here copies those into a checkout, registers the service, builds,
# and drives the Garden's own repl.
#
# Usage:
#     tests/http-garden.sh --build                    # soil + the hermod image
#     tests/http-garden.sh --build --with nginx       # and a peer to compare to
#     tests/http-garden.sh                            # run the differential
#     tests/http-garden.sh --targets "hermod nginx"   # over these
#     tests/http-garden.sh --contract                 # only: does our target answer?
#     tests/http-garden.sh --stop
#
# COST, up front, because it is the reason this is neither a gate nor a
# nightly:
#
#   The Garden builds every target FROM SOURCE with clang and ASan. nginx,
#   Apache, HAProxy, Envoy, Tomcat and the rest are compiler-hours and
#   gigabytes, once. The hermod image alone is cheap by comparison - a .NET
#   SDK download and one dotnet publish - which is why --build with no --with
#   builds only that, and why --contract exists: it answers "is our target
#   still a valid Garden target" without needing a single peer.
#
#   Run it deliberately, on a machine with the disk for it, and read the
#   result by hand. It is an instrument for a session, not for a pipeline.
#
# PLATFORM
#
# Docker on Windows lives inside WSL, and a Garden checkout on /mnt/d builds
# at a crawl, so on Windows this re-executes itself inside Debian with the
# checkout on the VM's own filesystem. On Linux everything is native and the
# checkout lands in tests/thirdparty/.

set -uo pipefail

# --- re-exec into WSL on Windows -------------------------------------------

if [ "$(uname -s)" != "Linux" ]; then

    if ! command -v wsl > /dev/null 2>&1; then
        echo "  SKIP  http-garden — no WSL, and Docker lives there on Windows" >&2
        exit 0
    fi

    SELF="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)/$(basename "${BASH_SOURCE[0]}")"
    SELF_WSL="$(wsl -d Debian -e wslpath -a "$(cygpath -w "$SELF" 2>/dev/null || echo "$SELF")" | tr -d '\r')"

    # HTTP_GARDEN_DIR defaults to the VM's own filesystem, not /mnt/d: a
    # docker build whose context is a 9p mount takes minutes to send what
    # takes seconds from ext4.
    # printf %q, not $*: --targets "hermod tornado" arrives as one argument
    # here and has to arrive as one argument there too. Unquoted it split, and
    # the far side answered "unknown option: tornado".
    QUOTED=""
    for arg in "$@"; do
        QUOTED="$QUOTED $(printf '%q' "$arg")"
    done

    exec wsl -d Debian -e bash -lc "HTTP_GARDEN_WSL=1 '$SELF_WSL'$QUOTED"

fi

ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"

# Pinned 2026-09-27.
GARDEN_REPO="https://github.com/narfindustries/http-garden"
GARDEN_PIN="b417e806c1b15e8ea0b9312f81a91fbdcbc7a83e"

# The stack the image builds. Both, because Hermod.csproj references Styx and
# "the behaviour of Hermod at <sha>" is not a statement unless what is under
# it is fixed too.
HERMOD_REPO="https://github.com/Vanaheimr/Hermod"
HERMOD_PIN="$(git -C "$ROOT/libs/Hermod" rev-parse HEAD 2>/dev/null || echo c85de5a26c735e8431d6198c40454d0eac140ef8)"
STYX_REPO="https://github.com/Vanaheimr/Styx"
STYX_PIN="$(git -C "$ROOT/libs/Styx" rev-parse HEAD 2>/dev/null || echo 67cc74956f624f783f02c864d0fc1f41af4c624c)"

if [ "${HTTP_GARDEN_WSL:-0}" = "1" ]; then
    GARDEN_DIR="${HTTP_GARDEN_DIR:-$HOME/.cache/http-garden}"
else
    GARDEN_DIR="${HTTP_GARDEN_DIR:-$ROOT/tests/thirdparty/http-garden}"
fi

DO_BUILD=0
DO_STOP=0
CONTRACT_ONLY=0
WITH=""
TARGETS="hermod"

while [ $# -gt 0 ]; do
    case "$1" in
        --build)       DO_BUILD=1 ;;
        --with)        WITH="${2:-}"; shift ;;
        --targets)     TARGETS="${2:-}"; shift ;;
        --contract)    CONTRACT_ONLY=1 ;;
        --stop)        DO_STOP=1 ;;
        --garden-dir)  GARDEN_DIR="${2:-}"; shift ;;
        -h|--help)     sed -n '2,44p' "${BASH_SOURCE[0]}" | sed 's/^# \{0,1\}//'; exit 0 ;;
        *)             echo "unknown option: $1" >&2; exit 2 ;;
    esac
    shift
done

if [ -t 1 ]; then
    RED=$'\033[31m'; GREEN=$'\033[32m'; YELLOW=$'\033[33m'; CYAN=$'\033[36m'; DIM=$'\033[2m'; OFF=$'\033[0m'
else
    RED=""; GREEN=""; YELLOW=""; CYAN=""; DIM=""; OFF=""
fi

note()    { printf '  %s%s%s\n' "$DIM" "$1" "$OFF"; }
pass()    { printf '    %s✓%s %s\n' "$GREEN" "$OFF" "$1"; }
failure() { printf '    %s✗%s %s\n' "$RED" "$OFF" "$1"; [ -n "${2:-}" ] && printf '        %s%s%s\n' "$DIM" "$2" "$OFF"; }

# The checkout directory name is load-bearing, which is not obvious and is
# checked nowhere upstream. tools/targets.py hardcodes
#
#     _NETWORK_NAME = "http-garden_default"
#
# and Compose derives the project name - and therefore the network - from the
# directory. A checkout in /tmp/hg produces hg_default, at which point the
# repl finds no containers on the network it is looking at, prints one warning
# naming all fifty services, and then answers every payload with nothing. An
# empty answer that looks like a result is the worst outcome available, so a
# wrong directory name is refused here rather than discovered later.
if [ "$(basename "$GARDEN_DIR")" != "http-garden" ]; then
    echo
    echo "  ${RED}--garden-dir must name a directory called http-garden${OFF}" >&2
    echo "  got: $GARDEN_DIR" >&2
    echo "  the Garden hardcodes the network name http-garden_default, which Compose" >&2
    echo "  derives from this directory; the repl silently sees no containers when" >&2
    echo "  the two do not match." >&2
    exit 2
fi

echo
echo "  ${CYAN}http-garden — A6: forty-five parsers, one payload at a time${OFF}"
note "checkout:   $GARDEN_DIR"
note "hermod:     ${HERMOD_PIN:0:12}   styx: ${STYX_PIN:0:12}"

for tool in docker git python3; do
    command -v "$tool" > /dev/null 2>&1 || {
        echo
        echo "  ${YELLOW}SKIP${OFF}  http-garden — $tool is not installed"
        exit 0
    }
done

docker info > /dev/null 2>&1 || {
    echo
    echo "  ${YELLOW}SKIP${OFF}  http-garden — the Docker daemon is not reachable"
    exit 0
}

# ---------------------------------------------------------------------------
# The checkout, pinned
# ---------------------------------------------------------------------------

if [ ! -d "$GARDEN_DIR/.git" ]; then
    mkdir -p "$(dirname "$GARDEN_DIR")"
    echo
    note "cloning $GARDEN_REPO …"
    git clone --quiet "$GARDEN_REPO" "$GARDEN_DIR" || {
        echo "  ${RED}clone failed${OFF}" >&2
        exit 1
    }
fi

git -C "$GARDEN_DIR" fetch --quiet origin 2>/dev/null
git -C "$GARDEN_DIR" checkout --quiet "$GARDEN_PIN" 2>/dev/null || {
    echo "  ${RED}could not check out ${GARDEN_PIN:0:12}${OFF}" >&2
    exit 1
}

if [ "$DO_STOP" -eq 1 ]; then
    ( cd "$GARDEN_DIR" && docker compose down 2>&1 | tail -3 )
    echo
    note "stopped"
    exit 0
fi

# ---------------------------------------------------------------------------
# Install our image and register the service
#
# Done on every run rather than once: tests/http-garden/ is the source of
# truth and the checkout is a build directory. An edit here that did not
# reach the image would be the worst kind of wrong answer - one that looks
# like a measurement.
# ---------------------------------------------------------------------------

IMG="$GARDEN_DIR/images/hermod"

rm -rf "$IMG"
mkdir -p "$IMG"
cp "$ROOT/tests/http-garden/Dockerfile" "$ROOT/tests/http-garden/start.sh" "$IMG/"
cp -r "$ROOT/tests/http-garden/app" "$IMG/app"
rm -rf "$IMG/app/bin" "$IMG/app/obj"

python3 - "$GARDEN_DIR/docker-compose.yml" "$HERMOD_REPO" "$HERMOD_PIN" "$STYX_REPO" "$STYX_PIN" <<'PYEOF'
import io, sys, re

path, hermod_repo, hermod_pin, styx_repo, styx_pin = sys.argv[1:6]
s = io.open(path).read()

entry = (
    "  hermod:\n"
    "    build:\n"
    "      args:\n"
    "        APP_BRANCH: master\n"
    "        APP_REPO: %s\n"
    "        APP_VERSION: %s\n"
    "        STYX_REPO: %s\n"
    "        STYX_VERSION: %s\n"
    "        START_SCRIPT: start.sh\n"
    "      context: ./images/hermod\n"
    "    x-props:\n"
    "      role: origin\n"
) % (hermod_repo, hermod_pin, styx_repo, styx_pin)

# Replace an existing block rather than appending a second one - the pins
# move whenever the submodule does.
s = re.sub(r"^  hermod:\n(?:    .*\n|      .*\n|        .*\n)*", "", s, flags=re.M)

if "services:\n" not in s:
    sys.exit("docker-compose.yml has no services: key")

s = s.replace("services:\n", "services:\n" + entry, 1)
io.open(path, "w").write(s)
print("  service 'hermod' registered at %s" % hermod_pin[:12])
PYEOF

# ---------------------------------------------------------------------------
# Build
# ---------------------------------------------------------------------------

if [ "$DO_BUILD" -eq 1 ]; then

    echo
    echo "  -- build --"

    if ! docker image inspect http-garden-soil:latest > /dev/null 2>&1; then
        note "building the soil base image (once, ~10 min) …"
        ( cd "$GARDEN_DIR" && docker pull -q debian:trixie-slim && docker build -q ./images/http-garden-soil -t http-garden-soil ) || {
            echo "  ${RED}soil build failed${OFF}" >&2
            exit 1
        }
    fi
    pass "soil base image present"

    for target in hermod $WITH; do
        note "building $target …"
        if ( cd "$GARDEN_DIR" && docker compose build "$target" > "/tmp/garden-build-$target.log" 2>&1 ); then
            pass "$target built"
        else
            failure "$target built" "see /tmp/garden-build-$target.log: $(tail -3 "/tmp/garden-build-$target.log" | tr '\n' ' ')"
            [ "$target" = "hermod" ] && exit 1
        fi
    done

fi

# ---------------------------------------------------------------------------
# Does our target still answer the contract?
#
# Worth its own mode. Everything below depends on Hermod producing the JSON
# the Garden compares, and if it stopped doing so - a Hermod change to the
# routing, to the response builder, to anything - every differential would
# read as "hermod disagrees with everyone", which is the same output a real
# and interesting finding would produce.
# ---------------------------------------------------------------------------

echo
echo "  -- contract --"

docker rm -f h1-garden-contract > /dev/null 2>&1
CID="$(cd "$GARDEN_DIR" && docker compose run -d --rm --name h1-garden-contract -p 14480:80 hermod 2>/dev/null)"

if [ -z "$CID" ]; then
    failure "the hermod target starts" "docker compose run failed — has it been built? (--build)"
    exit 1
fi

ready=0
for _ in $(seq 1 40); do
    if curl -s --max-time 1 --http1.1 http://127.0.0.1:14480/ > /dev/null 2>&1; then
        ready=1
        break
    fi
    sleep 0.5
done

if [ "$ready" -eq 0 ]; then
    failure "the hermod target starts" "no answer on :14480 — $(docker logs h1-garden-contract 2>&1 | tail -3 | tr '\n' ' ')"
    docker rm -f h1-garden-contract > /dev/null 2>&1
    exit 1
fi

pass "the hermod target starts and answers on :80"

CONTRACT="$(curl -s --http1.1 -X POST --data 'garden' -H 'X-Probe: 1' http://127.0.0.1:14480/a/b 2>/dev/null)"

if python3 - "$CONTRACT" <<'PYEOF'
import base64, json, sys
try:
    d = json.loads(sys.argv[1])
except Exception as e:
    print("        not JSON: %s" % e)
    sys.exit(1)

for key in ("headers", "body", "method", "version", "uri"):
    if key not in d:
        print("        missing key: %s" % key)
        sys.exit(1)

got = {
    "method":  base64.b64decode(d["method"]),
    "uri":     base64.b64decode(d["uri"]),
    "version": base64.b64decode(d["version"]),
    "body":    base64.b64decode(d["body"]),
}
want = {"method": b"POST", "uri": b"/a/b", "version": b"HTTP/1.1", "body": b"garden"}

for key, value in want.items():
    if got[key] != value:
        print("        %s: got %r, want %r" % (key, got[key], value))
        sys.exit(1)

names = [base64.b64decode(n).decode("latin-1") for n, _ in d["headers"]]
if "X-Probe" not in names:
    print("        header list does not carry X-Probe: %r" % names)
    sys.exit(1)
PYEOF
then
    pass "it answers the Garden's parse-tree contract (method, uri, version, body, headers)"
else
    failure "it answers the Garden's parse-tree contract" "$(printf '%s' "$CONTRACT" | head -c 200)"
    docker rm -f h1-garden-contract > /dev/null 2>&1
    exit 1
fi

docker rm -f h1-garden-contract > /dev/null 2>&1

if [ "$CONTRACT_ONLY" -eq 1 ]; then
    echo
    note "--contract: stopping here"
    exit 0
fi

# ---------------------------------------------------------------------------
# The differential
# ---------------------------------------------------------------------------

echo
echo "  -- differential over: $TARGETS --"

# garden.sh drives the repl with "uv run", and uv is a separate install this
# script is not going to make on somebody's machine. Its pyproject names three
# packages, so a venv does the same job with pip, which is already here. uv is
# preferred when it happens to be present.
if command -v uv > /dev/null 2>&1; then
    REPL=(./garden.sh repl)
else

    VENV="$GARDEN_DIR/.venv-garden"

    if ! "$VENV/bin/python" -c 'import docker, yaml, tqdm' > /dev/null 2>&1; then
        note "creating a venv for the repl (docker, pyyaml, tqdm) …"
        python3 -m venv "$VENV" > /dev/null 2>&1
        "$VENV/bin/pip" install --quiet --disable-pip-version-check docker pyyaml tqdm > /dev/null 2>&1
    fi

    if ! "$VENV/bin/python" -c 'import docker, yaml, tqdm' > /dev/null 2>&1; then
        echo
        echo "  ${YELLOW}SKIP${OFF}  the repl needs docker, pyyaml and tqdm, and they could not be installed"
        note "or install uv: curl -LsSf https://astral.sh/uv/install.sh | sh"
        exit 0
    fi

    REPL=("$VENV/bin/python" ./tools/repl.py)

fi

( cd "$GARDEN_DIR" && docker compose up -d $TARGETS > /dev/null 2>&1 )

# Wait for each target to actually accept a connection. "docker compose up -d"
# returns when the container exists, not when the server inside it has bound
# its socket, and the repl answers a refused connection with a stack trace
# rather than a retry - a .NET start takes a second or two, which was exactly
# long enough to lose a run to "Connection to hermod refused".
python3 - "$GARDEN_DIR" $TARGETS <<'PYWAIT'
import json, socket, subprocess, sys, time

garden, targets = sys.argv[1], sys.argv[2:]
deadline = time.time() + 60

for target in targets:
    ips = []
    while time.time() < deadline and not ips:
        try:
            cid = subprocess.run(["docker", "compose", "ps", "-q", target],
                                 cwd=garden, capture_output=True, text=True).stdout.strip()
            if cid:
                attrs = json.loads(subprocess.run(["docker", "inspect", cid],
                                                  capture_output=True, text=True).stdout)[0]
                ips = [n["IPAddress"] for n in attrs["NetworkSettings"]["Networks"].values() if n.get("IPAddress")]
        except Exception:
            pass
        if not ips:
            time.sleep(0.5)

    if not ips:
        print("  %s: no container address after 60 s" % target)
        continue

    while time.time() < deadline:
        try:
            socket.create_connection((ips[0], 80), timeout=1).close()
            break
        except OSError:
            time.sleep(0.5)
    else:
        print("  %s: %s:80 never accepted a connection" % (target, ips[0]))
PYWAIT


# The Garden's repl reads its commands with input(), so a pipe works. Each
# line fans one payload out to every running origin and clusters the targets
# by what they made of it: one cluster means they agree, two or more is a
# gadget for any chain that puts one in front of the other.
{
    echo "payload 'POST /echo HTTP/1.1\r\nHost: a\r\nContent-Length: 6\r\nTransfer-Encoding: chunked\r\n\r\n0\r\n\r\nGET /smuggled HTTP/1.1\r\nHost: a\r\n\r\n' | fanout | cluster"
    echo "payload 'POST /echo HTTP/1.1\r\nHost: a\r\nTransfer-Encoding: chunked\r\nTransfer-Encoding: chunked\r\n\r\n0\r\n\r\n' | fanout | cluster"
    echo "payload 'POST /echo HTTP/1.1\r\nHost: a\r\nTransfer-Encoding: chunked, chunked\r\n\r\n0\r\n\r\n' | fanout | cluster"
    echo "payload 'POST /echo HTTP/1.1\r\nHost: a\r\nContent-Length: 6\r\nContent-Length: 6\r\n\r\nhello!' | fanout | cluster"
    echo "payload 'POST /echo HTTP/1.1\r\nHost: a\r\nTransfer-Encoding: chunked\r\n\r\n5 \r\nhello\r\n0\r\n\r\n' | fanout | cluster"
    echo "exit"
} | ( cd "$GARDEN_DIR" && "${REPL[@]}" 2>&1 )

echo
note "targets left running — tests/http-garden.sh --stop when done"
