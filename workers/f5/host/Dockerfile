# Martlet F5 host role image: pinned F5-TTS runtime, the bounded stdio worker (martlet_f5_worker) and its loopback
# HTTP front (host/martlet_f5_host.py). Built on the host by "martlet-host add f5" (compose build, context workers/f5).
# Package trees are root-owned and read-only for the unprivileged runtime user, without bytecode caches, as the
# worker's runtime-inventory check requires. Models are not in the image: the role's provisioning step downloads the
# pinned files into the martlet-f5-models volume.
FROM python:3.12.10-slim-bookworm@sha256:fd95fa221297a88e1cf49c55ec1828edd7c5a428187e67b5d1805692d11588db

ENV PYTHONDONTWRITEBYTECODE=1 PIP_NO_CACHE_DIR=1 PIP_DISABLE_PIP_VERSION_CHECK=1 PIP_ROOT_USER_ACTION=ignore

COPY host/requirements.lock /tmp/requirements.lock
# urllib3 probes IPv6 at import time by binding a loopback socket; the worker's audit hook denies every socket and would
# latch a failure, so the probe is replaced with its result for this offline worker (verified below).
RUN grep -A1 '^setuptools @' /tmp/requirements.lock > /tmp/setuptools.lock \
 && python3.12 -m pip install --no-deps --require-hashes --no-compile -r /tmp/setuptools.lock \
 && python3.12 -m pip install --no-deps --require-hashes --no-compile --no-build-isolation -r /tmp/requirements.lock \
 && rm -f /tmp/requirements.lock /tmp/setuptools.lock \
 && sed -i 's/^HAS_IPV6 = _has_ipv6("::1")$/HAS_IPV6 = False  # Martlet: no import-time socket probe in the F5 worker/' \
      /usr/local/lib/python3.12/site-packages/urllib3/util/connection.py \
 && grep -q '^HAS_IPV6 = False' /usr/local/lib/python3.12/site-packages/urllib3/util/connection.py \
 && find /usr/local/lib/python3.12/site-packages -name __pycache__ -type d -prune -exec rm -rf {} + \
 && find /usr/local/lib/python3.12/site-packages -name '*.pyc' -type f -exec rm -f {} +

COPY martlet_f5_worker /opt/martlet-f5/worker/martlet_f5_worker
COPY host/martlet_f5_host.py /opt/martlet-f5/host/martlet_f5_host.py
ENV PYTHONPATH=/opt/martlet-f5/worker MARTLET_F5_ROOT=/opt/martlet-f5 MARTLET_F5_PORT=50080 \
    HF_HUB_OFFLINE=1 TRANSFORMERS_OFFLINE=1 HF_DATASETS_OFFLINE=1 MPLCONFIGDIR=/tmp/matplotlib
# The worker verifies sys.executable byte-for-byte, so it must be the real interpreter file, not a symlink.
# f5-tts ships f5_tts as a namespace package; the worker's origin checks need a regular package, so it gets an empty
# __init__.py (recorded in the runtime inventory like every other file).
RUN test -f /usr/local/bin/python3.12 && ! test -L /usr/local/bin/python3.12 \
 && site=/usr/local/lib/python3.12/site-packages && { test -f "$site/f5_tts/__init__.py" || : > "$site/f5_tts/__init__.py"; } \
 && python3.12 /opt/martlet-f5/host/martlet_f5_host.py inventory /opt/martlet-f5/runtime/runtime-inventory.json \
 && MPLCONFIGDIR=/opt/martlet-f5/matplotlib python3.12 -c "import matplotlib.font_manager" \
 && printf '#!/bin/sh\nexec /usr/local/bin/python3.12 /opt/martlet-f5/host/martlet_f5_host.py "$@"\n' > /usr/local/bin/martlet-f5 \
 && chmod 0755 /usr/local/bin/martlet-f5 \
 && useradd --uid 10050 --user-group --no-create-home --home-dir /tmp --shell /usr/sbin/nologin martlet-f5 \
 && mkdir -p /opt/martlet-f5/models && chown martlet-f5:martlet-f5 /opt/martlet-f5/models \
 && chmod -R go-w /usr/local/lib/python3.12/site-packages /opt/martlet-f5/worker /opt/martlet-f5/host /opt/martlet-f5/runtime

USER 10050:10050
WORKDIR /tmp
EXPOSE 50080
ENTRYPOINT ["/usr/local/bin/python3.12", "/opt/martlet-f5/host/martlet_f5_host.py"]
CMD ["serve"]
