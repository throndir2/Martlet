# Martlet motion host role image: HY-Motion 1.0 (Tencent HY-Motion 1.0 Community License) with pinned PyTorch 2.10 CUDA 12.8.
# Built on the host by "martlet-host add motion" (compose build, context workers/motion) after the owner accepted the terms.
# The HY-Motion code is fetched at a pinned commit checked during the build, and its Git LFS files (normalization statistics
# and the wooden body model) are downloaded and checked by size and SHA-256. Models are not in the image: "martlet-motion
# provision" downloads the pinned HY-Motion, Qwen3-8B and CLIP files into the martlet-motion-models volume, and nothing is
# downloaded at run time (HF_HUB_OFFLINE, TRANSFORMERS_OFFLINE, USE_HF_MODELS=0).
FROM python:3.12-slim-bookworm AS sources

RUN apt-get update && apt-get install -y --no-install-recommends git ca-certificates && rm -rf /var/lib/apt/lists/*
COPY martlet_motion/__init__.py martlet_motion/pins.py martlet_motion/host.py /app/martlet_motion/
RUN set -eu; \
    REPO="$(PYTHONPATH=/app python3 -c 'from martlet_motion import pins; print(pins.CODE_REPOSITORY)')"; \
    COMMIT="$(PYTHONPATH=/app python3 -c 'from martlet_motion import pins; print(pins.CODE_COMMIT)')"; \
    git init -q /src/HY-Motion-1.0; \
    git -C /src/HY-Motion-1.0 fetch -q --depth 1 "$REPO" "$COMMIT"; \
    git -C /src/HY-Motion-1.0 checkout -q FETCH_HEAD; \
    test "$(git -C /src/HY-Motion-1.0 rev-parse HEAD)" = "$COMMIT"; \
    rm -rf /src/HY-Motion-1.0/.git /src/HY-Motion-1.0/assets /src/HY-Motion-1.0/ssae /src/HY-Motion-1.0/examples \
      /src/HY-Motion-1.0/ckpts; \
    PYTHONPATH=/app python3 -m martlet_motion.host code-assets /src/HY-Motion-1.0

FROM python:3.12-slim-bookworm

ENV PYTHONDONTWRITEBYTECODE=1 PIP_NO_CACHE_DIR=1 PIP_DISABLE_PIP_VERSION_CHECK=1 PIP_ROOT_USER_ACTION=ignore \
    PIP_DEFAULT_TIMEOUT=60 PIP_RETRIES=10 PIP_RESUME_RETRIES=10

COPY host/requirements.txt /tmp/requirements.txt
RUN python3.12 -m pip install --upgrade 'pip==26.2.1' \
 && python3.12 -m pip install --no-compile --extra-index-url https://download.pytorch.org/whl/cu128 'torch==2.10.0' \
 && python3.12 -m pip install --no-compile -r /tmp/requirements.txt \
 && rm /tmp/requirements.txt \
 && find /usr/local/lib/python3.12/site-packages -name __pycache__ -type d -prune -exec rm -rf {} +

COPY --from=sources /src/HY-Motion-1.0 /opt/martlet-motion/HY-Motion-1.0
COPY martlet_motion /opt/martlet-motion/app/martlet_motion
ENV PYTHONPATH=/opt/martlet-motion/app \
    MARTLET_MOTION_ROOT=/opt/martlet-motion MARTLET_MOTION_PORT=50090 \
    HF_HUB_OFFLINE=1 TRANSFORMERS_OFFLINE=1 USE_HF_MODELS=0 HF_HOME=/tmp/huggingface \
    TORCH_HOME=/tmp/torch TRITON_CACHE_DIR=/tmp/triton
# HY-Motion reads ckpts/Qwen3-8B and ckpts/clip-vit-large-patch14 relative to its folder: they live in the models volume.
RUN mkdir -p /opt/martlet-motion/models/ckpts \
 && ln -s /opt/martlet-motion/models/ckpts /opt/martlet-motion/HY-Motion-1.0/ckpts \
 && python3.12 -c "import torch, transformers, torchdiffeq, scipy, einops, yaml; print('motion imports ok', torch.__version__)" \
 && printf '#!/bin/sh\nexec /usr/local/bin/python3.12 -m martlet_motion.host \"$@\"\n' > /usr/local/bin/martlet-motion \
 && chmod 0755 /usr/local/bin/martlet-motion \
 && useradd --uid 10058 --user-group --no-create-home --home-dir /tmp --shell /usr/sbin/nologin martlet-motion \
 && chown -R martlet-motion:martlet-motion /opt/martlet-motion/models \
 && chmod -R go-w /usr/local/lib/python3.12/site-packages /opt/martlet-motion/app /opt/martlet-motion/HY-Motion-1.0

USER 10058:10058
WORKDIR /opt/martlet-motion/HY-Motion-1.0
EXPOSE 50090
ENTRYPOINT ["/usr/local/bin/python3.12", "-m", "martlet_motion.host"]
CMD ["serve"]
