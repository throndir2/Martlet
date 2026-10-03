# Martlet host image: the martlet-host engine, the Linux gateway and a pinned Docker CLI.
# The same image runs as:
#   martlet-host-net      a long-lived network holder that publishes the gateway port (roles share its loopback)
#   martlet-host-gateway  the gateway ("serve"), no Docker socket
#   engine sessions       "docker run --rm -it ... martlet-host <command>" with the Docker socket, for setup only
# Build from a checkout:   docker build -t martlet-host -f deploy/host/Dockerfile .
# Build from GitHub:       docker build -t martlet-host -f deploy/host/Dockerfile https://github.com/throndir2/Martlet.git#<ref>

FROM mcr.microsoft.com/dotnet/sdk:10.0.401 AS build
WORKDIR /src
COPY . .
ENV CI=true DOTNET_CLI_TELEMETRY_OPTOUT=1 DOTNET_NOLOGO=1 DOTNET_GENERATE_ASPNET_CERTIFICATE=false
RUN dotnet publish src/Martlet.Gateway.Host.Linux/Martlet.Gateway.Host.Linux.csproj -c Release -o /out -p:UseAppHost=false

FROM mcr.microsoft.com/dotnet/aspnet:10.0.12
ARG DOCKER_CLI_URL=https://download.docker.com/linux/static/stable/x86_64/docker-27.3.1.tgz
ARG DOCKER_CLI_SHA256=9b4f6fe406e50f9085ee474c451e2bb5adb119a03591f467922d3b4e2ddf31d3
ARG COMPOSE_URL=https://github.com/docker/compose/releases/download/v2.29.7/docker-compose-linux-x86_64
ARG COMPOSE_SHA256=383ce6698cd5d5bbf958d2c8489ed75094e34a77d340404d9f32c4ae9e12baf0
RUN apt-get update \
 && apt-get install -y --no-install-recommends bash ca-certificates curl tar iproute2 procps \
 && rm -rf /var/lib/apt/lists/* \
 && curl -fsSL "$DOCKER_CLI_URL" -o /tmp/docker.tgz && echo "$DOCKER_CLI_SHA256  /tmp/docker.tgz" | sha256sum -c - \
 && tar -xzf /tmp/docker.tgz -C /tmp && install -m 0755 /tmp/docker/docker /usr/local/bin/docker && rm -rf /tmp/docker* \
 && mkdir -p /usr/local/lib/docker/cli-plugins \
 && curl -fsSL "$COMPOSE_URL" -o /usr/local/lib/docker/cli-plugins/docker-compose \
 && echo "$COMPOSE_SHA256  /usr/local/lib/docker/cli-plugins/docker-compose" | sha256sum -c - \
 && chmod 0755 /usr/local/lib/docker/cli-plugins/docker-compose
# Stable non-root identity required by the gateway's storage custody; named volumes inherit these owners/modes.
RUN useradd --uid 1000 --user-group --create-home --shell /bin/bash martlet 2>/dev/null || \
    (usermod -l martlet -d /home/martlet -m "$(getent passwd 1000 | cut -d: -f1)" && groupmod -n martlet "$(getent group 1000 | cut -d: -f1)") \
 && mkdir -p /var/lib/martlet/config /var/lib/martlet/private \
 && chown 1000:1000 /var/lib/martlet /var/lib/martlet/config /var/lib/martlet/private \
 && chmod 0700 /var/lib/martlet/config /var/lib/martlet/private
COPY --from=build /out /opt/martlet/gateway
COPY deploy/host /opt/martlet/host
# Sources that host roles build their images from (f5: workers/f5/host/Dockerfile; xtts: workers/xtts/host/Dockerfile;
# gpt-sovits: workers/gpt-sovits/host/Dockerfile; audio2face's local engine: workers/audio2face/Dockerfile).
COPY workers/f5 /opt/martlet/source/workers/f5
COPY workers/xtts /opt/martlet/source/workers/xtts
COPY workers/chatterbox /opt/martlet/source/workers/chatterbox
COPY workers/gpt-sovits /opt/martlet/source/workers/gpt-sovits
COPY workers/audio2face /opt/martlet/source/workers/audio2face
RUN chmod 0755 /opt/martlet/host/martlet-host && ln -s /opt/martlet/host/martlet-host /usr/local/bin/martlet-host
ENV MARTLET_HOST_MODE=docker TERM=xterm-256color
USER 1000:1000
WORKDIR /home/martlet
ENTRYPOINT ["martlet-host"]
CMD ["help"]
