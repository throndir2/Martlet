# Martlet host image: the martlet-host engine, the Linux gateway and a pinned Docker CLI.
# The same image runs as:
#   martlet-host-net      a long-lived network holder that publishes the gateway port (roles share its loopback)
#   martlet-host-gateway  the gateway ("serve"), no Docker socket
#   engine sessions       "docker run --rm -it ... martlet-host <command>" with the Docker socket, for setup only
# Build from a checkout:   docker build -t martlet-host -f deploy/host/Dockerfile .
# Build from GitHub:       docker build -t martlet-host -f deploy/host/Dockerfile https://github.com/throndir2/Martlet.git#<ref>

# Each host builds this image for itself. No --platform=$BUILDPLATFORM here: the legacy builder (Docker without buildx,
# for example Ubuntu's docker.io) leaves it empty and stops with "failed to parse platform".
FROM mcr.microsoft.com/dotnet/sdk:10.0.401 AS build
WORKDIR /src
COPY . .
ENV CI=true DOTNET_CLI_TELEMETRY_OPTOUT=1 DOTNET_NOLOGO=1 DOTNET_GENERATE_ASPNET_CERTIFICATE=false
RUN dotnet publish src/Martlet.Gateway.Host.Linux/Martlet.Gateway.Host.Linux.csproj -c Release -o /out -p:UseAppHost=false

FROM mcr.microsoft.com/dotnet/aspnet:10.0.12
# x86_64 and arm64 (Apple-silicon Macs with Docker Desktop, CPU only; ARM64 Linux): the Docker CLI and Compose plugin
# for the architecture this image is built for (BuildKit sets TARGETARCH; the legacy builder uses this machine's), each
# checked against its pinned SHA-256.
ARG TARGETARCH
ARG DOCKER_CLI_VERSION=27.3.1
ARG COMPOSE_VERSION=2.29.7
RUN arch="${TARGETARCH:-$(dpkg --print-architecture)}"; case "$arch" in \
      amd64) cli_arch=x86_64; cli_sum=9b4f6fe406e50f9085ee474c451e2bb5adb119a03591f467922d3b4e2ddf31d3; \
             compose_arch=x86_64; compose_sum=383ce6698cd5d5bbf958d2c8489ed75094e34a77d340404d9f32c4ae9e12baf0 ;; \
      arm64) cli_arch=aarch64; cli_sum=4da6a6c7502b7ab561675a5ff5ac192d9b49d76d0b8847cf17ade246122279f4; \
             compose_arch=aarch64; compose_sum=6e9fbd5daa20dca5d7d89145081ae8155d68ef2928b497d9f85b54fe0f9dbb2c ;; \
      *) echo "Unsupported architecture: $arch (x86_64 and arm64 only)" >&2; exit 1 ;; \
    esac \
 && apt-get update \
 && apt-get install -y --no-install-recommends bash ca-certificates curl tar iproute2 procps \
 && rm -rf /var/lib/apt/lists/* \
 && curl -fsSL "https://download.docker.com/linux/static/stable/${cli_arch}/docker-${DOCKER_CLI_VERSION}.tgz" -o /tmp/docker.tgz \
 && echo "$cli_sum  /tmp/docker.tgz" | sha256sum -c - \
 && tar -xzf /tmp/docker.tgz -C /tmp && install -m 0755 /tmp/docker/docker /usr/local/bin/docker && rm -rf /tmp/docker* \
 && mkdir -p /usr/local/lib/docker/cli-plugins \
 && curl -fsSL "https://github.com/docker/compose/releases/download/v${COMPOSE_VERSION}/docker-compose-linux-${compose_arch}" \
      -o /usr/local/lib/docker/cli-plugins/docker-compose \
 && echo "$compose_sum  /usr/local/lib/docker/cli-plugins/docker-compose" | sha256sum -c - \
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
# gpt-sovits: workers/gpt-sovits/host/Dockerfile; dia: workers/dia/host/Dockerfile; singing: workers/singing/host/Dockerfile;
# pictures: workers/pictures/host/Dockerfile; ocr: workers/ocr/host/Dockerfile;
# audio2face's local engine: workers/audio2face/Dockerfile; stt's parakeet engine: workers/parakeet/host/Dockerfile).
COPY workers/f5 /opt/martlet/source/workers/f5
COPY workers/xtts /opt/martlet/source/workers/xtts
COPY workers/chatterbox /opt/martlet/source/workers/chatterbox
COPY workers/gpt-sovits /opt/martlet/source/workers/gpt-sovits
COPY workers/dia /opt/martlet/source/workers/dia
COPY workers/singing /opt/martlet/source/workers/singing
COPY workers/pictures /opt/martlet/source/workers/pictures
COPY workers/ocr /opt/martlet/source/workers/ocr
COPY workers/audio2face /opt/martlet/source/workers/audio2face
COPY workers/parakeet /opt/martlet/source/workers/parakeet
RUN chmod 0755 /opt/martlet/host/martlet-host && ln -s /opt/martlet/host/martlet-host /usr/local/bin/martlet-host
ENV MARTLET_HOST_MODE=docker TERM=xterm-256color
USER 1000:1000
WORKDIR /home/martlet
ENTRYPOINT ["martlet-host"]
CMD ["help"]
