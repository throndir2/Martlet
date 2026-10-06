# Martlet pictures host role image: ComfyUI v0.39.0 (GPL-3.0) with pinned PyTorch 2.10 CUDA 12.8.
# Built on the host by "martlet-host add pictures" (compose build, context workers/pictures). ComfyUI is fetched at a pinned
# commit checked during the build. Models are not in the image: "martlet-pictures provision" downloads the pinned Z-Image
# Turbo files into the martlet-pictures-models volume, and nothing is downloaded at run time (HF_HUB_OFFLINE).
FROM python:3.12-slim-bookworm AS sources

RUN apt-get update && apt-get install -y --no-install-recommends git ca-certificates && rm -rf /var/lib/apt/lists/*
COPY martlet_pictures/pins.py /pins.py
RUN set -eu; \
    COMFY_REPO="$(python3 -c 'import importlib.util as u; s=u.spec_from_file_location("p","/pins.py"); m=u.module_from_spec(s); s.loader.exec_module(m); print(m.COMFYUI_REPOSITORY)')"; \
    COMFY_COMMIT="$(python3 -c 'import importlib.util as u; s=u.spec_from_file_location("p","/pins.py"); m=u.module_from_spec(s); s.loader.exec_module(m); print(m.COMFYUI_COMMIT)')"; \
    git init -q /src/ComfyUI; \
    git -C /src/ComfyUI fetch -q --depth 1 "$COMFY_REPO" "$COMFY_COMMIT"; \
    git -C /src/ComfyUI checkout -q FETCH_HEAD; \
    test "$(git -C /src/ComfyUI rev-parse HEAD)" = "$COMFY_COMMIT"; \
    rm -rf /src/ComfyUI/.git /src/ComfyUI/input /src/ComfyUI/output /src/ComfyUI/temp

FROM python:3.12-slim-bookworm

ENV PYTHONDONTWRITEBYTECODE=1 PIP_NO_CACHE_DIR=1 PIP_DISABLE_PIP_VERSION_CHECK=1 PIP_ROOT_USER_ACTION=ignore \
    PIP_DEFAULT_TIMEOUT=60 PIP_RETRIES=10 PIP_RESUME_RETRIES=10

RUN apt-get update && apt-get install -y --no-install-recommends git ffmpeg libgl1 libglib2.0-0 && rm -rf /var/lib/apt/lists/*

COPY --from=sources /src/ComfyUI /opt/martlet-pictures/ComfyUI
RUN python3.12 -m pip install --upgrade 'pip==26.2.1' \
 && python3.12 -m pip install --no-compile --extra-index-url https://download.pytorch.org/whl/cu128 \
      'torch==2.10.0' 'torchvision==0.25.0' 'torchaudio==2.10.0' \
 && python3.12 -m pip install --no-compile -r /opt/martlet-pictures/ComfyUI/requirements.txt \
 && find /usr/local/lib/python3.12/site-packages -name __pycache__ -type d -prune -exec rm -rf {} +

COPY martlet_pictures /opt/martlet-pictures/app/martlet_pictures
ENV PYTHONPATH=/opt/martlet-pictures/app \
    MARTLET_PICTURES_ROOT=/opt/martlet-pictures MARTLET_PICTURES_PORT=50086 \
    HF_HUB_OFFLINE=1 TRANSFORMERS_OFFLINE=1 HF_HOME=/opt/martlet-pictures/models/huggingface \
    XDG_CACHE_HOME=/opt/martlet-pictures/models TORCH_HOME=/tmp/torch TRITON_CACHE_DIR=/tmp/triton
RUN rm -rf /opt/martlet-pictures/ComfyUI/models \
 && mkdir -p /opt/martlet-pictures/models /opt/martlet-pictures/output /opt/martlet-pictures/temp \
 && ln -s /opt/martlet-pictures/models /opt/martlet-pictures/ComfyUI/models \
 && python3.12 -c "import torch; print('pictures imports ok', torch.__version__)" \
 && printf '#!/bin/sh\nexec /usr/local/bin/python3.12 -m martlet_pictures.host \"$@\"\n' > /usr/local/bin/martlet-pictures \
 && chmod 0755 /usr/local/bin/martlet-pictures \
 && useradd --uid 10056 --user-group --no-create-home --home-dir /tmp --shell /usr/sbin/nologin martlet-pictures \
 && chown -R martlet-pictures:martlet-pictures /opt/martlet-pictures/models /opt/martlet-pictures/output /opt/martlet-pictures/temp \
 && chmod -R go-w /usr/local/lib/python3.12/site-packages /opt/martlet-pictures/app /opt/martlet-pictures/ComfyUI

USER 10056:10056
WORKDIR /tmp
EXPOSE 50086
ENTRYPOINT ["/usr/local/bin/python3.12", "-m", "martlet_pictures.host"]
CMD ["serve"]
