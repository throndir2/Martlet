# Martlet Chatterbox Turbo host role image. Built by "martlet-host add chatterbox" from workers/chatterbox.
FROM python:3.12.10-slim-bookworm@sha256:fd95fa221297a88e1cf49c55ec1828edd7c5a428187e67b5d1805692d11588db

ENV PYTHONDONTWRITEBYTECODE=1 PIP_NO_CACHE_DIR=1 PIP_DISABLE_PIP_VERSION_CHECK=1 PIP_ROOT_USER_ACTION=ignore
ENV HF_HUB_OFFLINE=1 TRANSFORMERS_OFFLINE=1 HF_DATASETS_OFFLINE=1 NUMBA_CACHE_DIR=/tmp/numba MPLCONFIGDIR=/tmp/matplotlib MARTLET_CHATTERBOX_PORT=50083

COPY host/requirements.lock /tmp/requirements.lock
RUN grep -A200 '^setuptools==' /tmp/requirements.lock | sed '/^    # via/q' > /tmp/setuptools.lock \
 && python3.12 -m pip install --no-deps --require-hashes --no-compile -r /tmp/setuptools.lock \
 && python3.12 -m pip install --no-deps --require-hashes --no-compile --no-build-isolation -r /tmp/requirements.lock \
 && rm -f /tmp/requirements.lock /tmp/setuptools.lock \
 && find /usr/local/lib/python3.12/site-packages -name __pycache__ -type d -prune -exec rm -rf {} + \
 && find /usr/local/lib/python3.12/site-packages -name '*.pyc' -type f -exec rm -f {} +

COPY martlet_chatterbox_host.py /opt/martlet-chatterbox/host/martlet_chatterbox_host.py
ENV MARTLET_CHATTERBOX_ROOT=/opt/martlet-chatterbox PYTHONPATH=/opt/martlet-chatterbox/host
RUN printf '#!/bin/sh\nexec /usr/local/bin/python3.12 /opt/martlet-chatterbox/host/martlet_chatterbox_host.py "$@"\n' > /usr/local/bin/martlet-chatterbox \
 && chmod 0755 /usr/local/bin/martlet-chatterbox \
 && useradd --uid 10051 --user-group --no-create-home --home-dir /tmp --shell /usr/sbin/nologin martlet-chatterbox \
 && mkdir -p /opt/martlet-chatterbox/models /opt/martlet-chatterbox/requests \
 && chown martlet-chatterbox:martlet-chatterbox /opt/martlet-chatterbox/models /opt/martlet-chatterbox/requests \
 && chmod 0700 /opt/martlet-chatterbox/requests \
 && chmod -R go-w /usr/local/lib/python3.12/site-packages /opt/martlet-chatterbox/host

USER 10051:10051
WORKDIR /tmp
EXPOSE 50083
ENTRYPOINT ["/usr/local/bin/python3.12", "/opt/martlet-chatterbox/host/martlet_chatterbox_host.py"]
CMD ["serve"]
