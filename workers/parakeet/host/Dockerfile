# Martlet Parakeet host image: the stt role's parakeet engine (workers/parakeet/host/martlet_parakeet_host.py) with the
# hash-pinned sherpa-onnx 1.13.8 runtime. Built on the host by "martlet-host add stt" with the parakeet engine (compose
# build, context workers/parakeet). Package trees are root-owned and read-only for the unprivileged runtime user. Models are
# not in the image: the service downloads the chosen Parakeet model (pinned revision, SHA-256 checked) into the
# martlet-stt-parakeet-models volume on its first start (or martlet-host's prepare step).
FROM python:3.12.10-slim-bookworm@sha256:fd95fa221297a88e1cf49c55ec1828edd7c5a428187e67b5d1805692d11588db

ENV PYTHONDONTWRITEBYTECODE=1 PIP_NO_CACHE_DIR=1 PIP_DISABLE_PIP_VERSION_CHECK=1 PIP_ROOT_USER_ACTION=ignore \
    PIP_DEFAULT_TIMEOUT=60 PIP_RETRIES=10 PIP_RESUME_RETRIES=10

# The base image's pip fails the whole build when a download stalls or the connection drops; pip 26.2.1 resumes it.
RUN echo 'pip==26.2.1 --hash=sha256:71138adf1f4ca900cdb7d289c21b7494329f2332b6d85f0e1c42108c0384ed3e' > /tmp/pip.lock \
 && python3.12 -m pip install --no-deps --require-hashes --no-compile -r /tmp/pip.lock && rm -f /tmp/pip.lock

COPY host/requirements.lock /tmp/requirements.lock
RUN python3.12 -m pip install --no-deps --require-hashes --no-compile -r /tmp/requirements.lock \
 && rm -f /tmp/requirements.lock \
 && python3.12 -c "import sherpa_onnx, numpy, importlib.metadata as m; assert m.version('sherpa-onnx') == '1.13.8'" \
 && find /usr/local/lib/python3.12/site-packages -name __pycache__ -type d -prune -exec rm -rf {} + \
 && find /usr/local/lib/python3.12/site-packages -name '*.pyc' -type f -exec rm -f {} +

COPY host/martlet_parakeet_host.py /opt/martlet-parakeet/host/martlet_parakeet_host.py
ENV MARTLET_PARAKEET_ROOT=/opt/martlet-parakeet MARTLET_PARAKEET_PORT=8178
RUN useradd --uid 10056 --user-group --no-create-home --home-dir /tmp --shell /usr/sbin/nologin martlet-parakeet \
 && mkdir -p /opt/martlet-parakeet/models && chown martlet-parakeet:martlet-parakeet /opt/martlet-parakeet/models \
 && chmod -R go-w /usr/local/lib/python3.12/site-packages /opt/martlet-parakeet/host

USER 10056:10056
WORKDIR /tmp
EXPOSE 8178
ENTRYPOINT ["/usr/local/bin/python3.12", "/opt/martlet-parakeet/host/martlet_parakeet_host.py"]
CMD ["serve"]
