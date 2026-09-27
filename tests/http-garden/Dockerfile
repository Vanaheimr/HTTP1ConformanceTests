# Hermod as an HTTP Garden target.
#
# Copied into <garden>/images/hermod/ by tests/http-garden.sh, which also adds
# the docker-compose.yml service that supplies the ARGs below. It follows the
# Garden's own idiom - FROM http-garden-soil, everything parametrized by
# APP_REPO / APP_BRANCH / APP_VERSION so several versions of a target can be
# built and compared - with one departure: the other images compile their
# server from source with clang and ASan, and there is no ASan for managed
# code. What this image gets instead is the .NET runtime's own bounds
# checking, which is not the same instrument and is written down here rather
# than left to be assumed.

FROM http-garden-soil:latest

# --- .NET ------------------------------------------------------------------
#
# Debian trixie has no .NET 10, so it comes from Microsoft's install script,
# the way images/go_stdlib fetches its own Go tarball.
#
# libicu is not optional here, and not only for the app. The SDK itself
# aborts on startup without it (CultureInfo..cctor, from DateTime.Now, from
# Program.Main), so a build fails before it compiles anything. Setting
# DOTNET_SYSTEM_GLOBALIZATION_INVARIANT would also get the build through, and
# is the wrong fix for a conformance target: it changes how the RUNNING
# server compares strings, and a target that parses differently from a normal
# Hermod deployment is measuring the wrong thing.
ARG DOTNET_CHANNEL=10.0
RUN apt -y update \
 && apt -y install --no-install-recommends wget ca-certificates libicu-dev \
 && wget -q https://dot.net/v1/dotnet-install.sh -O /tmp/dotnet-install.sh \
 && bash /tmp/dotnet-install.sh --channel "$DOTNET_CHANNEL" --install-dir /usr/share/dotnet \
 && ln -sf /usr/share/dotnet/dotnet /usr/bin/dotnet \
 && rm /tmp/dotnet-install.sh \
 && rm -rf /var/lib/apt/lists/*

ENV DOTNET_CLI_TELEMETRY_OPTOUT=1
ENV DOTNET_NOLOGO=1
ENV DOTNET_SKIP_FIRST_TIME_EXPERIENCE=1

# --- The stack under test --------------------------------------------------
#
# Two repositories, because Hermod.csproj references ..\..\Styx\Styx\Styx.csproj
# and expects the two checkouts to be siblings. Styx is pinned separately for
# the same reason Hermod is: "the bug is in Hermod at <sha>" is only a
# statement if everything under it is fixed too.

ARG STYX_REPO=https://github.com/Vanaheimr/Styx
ARG STYX_VERSION
RUN mkdir -p /app/src \
 && git clone --quiet "$STYX_REPO" /app/src/Styx \
 && git -C /app/src/Styx checkout --quiet "$STYX_VERSION"

ARG APP_REPO=https://github.com/Vanaheimr/Hermod
ARG APP_BRANCH=master
ARG APP_VERSION
RUN git clone --quiet "$APP_REPO" /app/src/Hermod \
 && git -C /app/src/Hermod fetch --quiet origin "$APP_BRANCH" \
 && git -C /app/src/Hermod checkout --quiet "$APP_VERSION"

# --- The target itself -----------------------------------------------------

COPY app /app/garden-app
RUN dotnet publish /app/garden-app/HermodGarden.csproj \
        -c Release \
        -o /app/target \
        -p:HermodPath=/app/src/Hermod/Hermod/Hermod.csproj

ARG START_SCRIPT=start.sh
COPY $START_SCRIPT ./start.sh
RUN chmod +x ./start.sh

CMD ["./start.sh"]
