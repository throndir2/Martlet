# Martlet Dia host role image: Nari Labs' Dia (pinned source, Apache-2.0) with hash-pinned PyTorch 2.6.0 (CUDA 12.4) and
# descript-audio-codec, the Dia worker process and its loopback HTTP front (martlet_dia). Built on the host by
# "martlet-host add dia" (compose build, context workers/dia). Models are not in the image: the role's provisioning
# step downloads the pinned Dia-1.6B-0626 weights and the DAC codec into the martlet-dia-models volume, and nothing is
# downloaded at run time (HF_HUB_OFFLINE; the worker loads both from local paths).
FROM python:3.12.10-slim-bookworm@sha256:fd95fa221297a88e1cf49c55ec1828edd7c5a428187e67b5d1805692d11588db

ENV PYTHONDONTWRITEBYTECODE=1 PIP_NO_CACHE_DIR=1 PIP_DISABLE_PIP_VERSION_CHECK=1 PIP_ROOT_USER_ACTION=ignore

COPY host/requirements.lock /tmp/requirements.lock
RUN grep -A1 '^setuptools==' /tmp/requirements.lock | sed 's/ \\$//; s/^ *//' | tr '\n' ' ' > /tmp/setuptools.lock \
 && python3.12 -m pip install --no-deps --require-hashes --no-compile -r /tmp/setuptools.lock \
 && python3.12 -m pip install --no-deps --require-hashes --no-compile --no-build-isolation \
      --extra-index-url https://download.pytorch.org/whl/cu124 -r /tmp/requirements.lock \
 && rm -f /tmp/requirements.lock /tmp/setuptools.lock \
 && find /usr/local/lib/python3.12/site-packages -name __pycache__ -type d -prune -exec rm -rf {} +

COPY martlet_dia /opt/martlet-dia/app/martlet_dia
ENV PYTHONPATH=/opt/martlet-dia/app:/opt/martlet-dia/dia-src MARTLET_DIA_ROOT=/opt/martlet-dia MARTLET_DIA_PORT=50084 \
    HF_HUB_OFFLINE=1 TRANSFORMERS_OFFLINE=1 MPLCONFIGDIR=/tmp/matplotlib NUMBA_CACHE_DIR=/tmp/numba
RUN python3.12 -m martlet_dia.host fetch-source /opt/martlet-dia/dia-src \
 && python3.12 -c "import dac, dia.model, torchaudio; print('dia', dia.model.DEFAULT_SAMPLE_RATE)" \
 && find /opt/martlet-dia /usr/local/lib/python3.12/site-packages -name __pycache__ -type d -prune -exec rm -rf {} + \
 && printf '#!/bin/sh\nexec /usr/local/bin/python3.12 -m martlet_dia.host "$@"\n' > /usr/local/bin/martlet-dia \
 && chmod 0755 /usr/local/bin/martlet-dia \
 && useradd --uid 10054 --user-group --no-create-home --home-dir /tmp --shell /usr/sbin/nologin martlet-dia \
 && mkdir -p /opt/martlet-dia/models && chown martlet-dia:martlet-dia /opt/martlet-dia/models \
 && chmod -R go-w /usr/local/lib/python3.12/site-packages /opt/martlet-dia/app /opt/martlet-dia/dia-src

USER 10054:10054
WORKDIR /tmp
EXPOSE 50084
ENTRYPOINT ["/usr/local/bin/python3.12", "-m", "martlet_dia.host"]
CMD ["serve"]
