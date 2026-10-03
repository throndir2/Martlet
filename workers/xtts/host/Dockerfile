# Martlet XTTS-v2 host role image: pinned coqui-tts (Idiap fork) runtime and the loopback voice service
# (host/martlet_xtts_host.py). Built on the host by "martlet-host add xtts" (compose build, context workers/xtts).
# Package trees are root-owned and read-only for the unprivileged runtime user. Models are not in the image: the role's
# provisioning step downloads the pinned XTTS-v2 files into the martlet-xtts-models volume.
FROM python:3.12.10-slim-bookworm@sha256:fd95fa221297a88e1cf49c55ec1828edd7c5a428187e67b5d1805692d11588db

ENV PYTHONDONTWRITEBYTECODE=1 PIP_NO_CACHE_DIR=1 PIP_DISABLE_PIP_VERSION_CHECK=1 PIP_ROOT_USER_ACTION=ignore

COPY host/requirements.lock /tmp/requirements.lock
RUN grep -A1 '^setuptools @' /tmp/requirements.lock > /tmp/setuptools.lock \
 && python3.12 -m pip install --no-deps --require-hashes --no-compile -r /tmp/setuptools.lock \
 && python3.12 -m pip install --no-deps --require-hashes --no-compile --no-build-isolation -r /tmp/requirements.lock \
 && rm -f /tmp/requirements.lock /tmp/setuptools.lock \
 && python3.12 -c "import TTS.tts.models.xtts, importlib.metadata as m; assert m.version('coqui-tts') == '0.27.5'" \
 && find /usr/local/lib/python3.12/site-packages -name __pycache__ -type d -prune -exec rm -rf {} + \
 && find /usr/local/lib/python3.12/site-packages -name '*.pyc' -type f -exec rm -f {} +

COPY host/martlet_xtts_host.py /opt/martlet-xtts/host/martlet_xtts_host.py
ENV MARTLET_XTTS_ROOT=/opt/martlet-xtts MARTLET_XTTS_PORT=50081 \
    HF_HUB_OFFLINE=1 TRANSFORMERS_OFFLINE=1 HF_DATASETS_OFFLINE=1 MPLCONFIGDIR=/tmp/matplotlib NUMBA_CACHE_DIR=/tmp/numba
RUN printf '#!/bin/sh\nexec /usr/local/bin/python3.12 /opt/martlet-xtts/host/martlet_xtts_host.py "$@"\n' > /usr/local/bin/martlet-xtts \
 && chmod 0755 /usr/local/bin/martlet-xtts \
 && useradd --uid 10051 --user-group --no-create-home --home-dir /tmp --shell /usr/sbin/nologin martlet-xtts \
 && mkdir -p /opt/martlet-xtts/models && chown martlet-xtts:martlet-xtts /opt/martlet-xtts/models \
 && chmod -R go-w /usr/local/lib/python3.12/site-packages /opt/martlet-xtts/host

USER 10051:10051
WORKDIR /tmp
EXPOSE 50081
ENTRYPOINT ["/usr/local/bin/python3.12", "/opt/martlet-xtts/host/martlet_xtts_host.py"]
CMD ["serve"]
