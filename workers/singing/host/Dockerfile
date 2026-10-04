# Martlet singing host role image: ACE-Step 1.5 (MIT) with its nano-vllm planner backend, SoulX-Singer-SVC (Apache-2.0),
# Demucs 4.0.1 (MIT) and, for the optional VevoSing engine, Amphion's Vevo1.5 code (MIT), with hash-pinned PyTorch 2.10
# (CUDA 12.8) and the singing worker and its loopback HTTP front (martlet_singing). Built on the host by
# "martlet-host add singing" (compose build, context workers/singing). Sources are fetched at pinned commits (the commit
# ID is checked); models are not in the image: the role's provisioning step downloads the pinned files into the
# martlet-singing-models volume, and nothing is downloaded at run time (HF_HUB_OFFLINE; every model loads from local paths).
FROM python:3.11-slim-bookworm@sha256:2333bd330d12de02514770b3585cad313644316047cdee24a7acfdece6de6efb AS sources

RUN apt-get update && apt-get install -y --no-install-recommends git ca-certificates && rm -rf /var/lib/apt/lists/*
COPY martlet_singing/pins.py /tmp/pins.py
# Each source at its pinned commit; the checkout is refused unless HEAD is exactly that commit.
RUN set -eu; \
    fetch() { git init -q "$1" && git -C "$1" fetch -q --depth 1 "$2" "$3" && git -C "$1" checkout -q FETCH_HEAD \
      && test "$(git -C "$1" rev-parse HEAD)" = "$3" && rm -rf "$1/.git"; }; \
    eval "$(python3 -c 'import importlib.util as u; s=u.spec_from_file_location("p","/tmp/pins.py"); m=u.module_from_spec(s); s.loader.exec_module(m); \
print(f"fetch /src/ace-step {m.ACE_STEP_REPOSITORY} {m.ACE_STEP_COMMIT}; fetch /src/soulx-singer {m.SOULX_REPOSITORY} {m.SOULX_COMMIT}; fetch /src/amphion {m.AMPHION_REPOSITORY} {m.AMPHION_COMMIT}")')"; \
    rm -rf /src/amphion/egs /src/amphion/imgs /src/amphion/bins /src/soulx-singer/example /src/soulx-singer/assets \
      /src/ace-step/docs /src/ace-step/assets

FROM python:3.11-slim-bookworm@sha256:2333bd330d12de02514770b3585cad313644316047cdee24a7acfdece6de6efb

ENV PYTHONDONTWRITEBYTECODE=1 PIP_NO_CACHE_DIR=1 PIP_DISABLE_PIP_VERSION_CHECK=1 PIP_ROOT_USER_ACTION=ignore

COPY host/requirements.lock /tmp/requirements.lock
RUN grep -A1 '^setuptools==' /tmp/requirements.lock | sed 's/ \\$//; s/^ *//' | tr '\n' ' ' > /tmp/setuptools.lock \
 && python3.11 -m pip install --no-deps --require-hashes --no-compile -r /tmp/setuptools.lock \
 && python3.11 -m pip install --no-deps --require-hashes --no-compile --no-build-isolation \
      --extra-index-url https://download.pytorch.org/whl/cu128 -r /tmp/requirements.lock \
 && rm -f /tmp/requirements.lock /tmp/setuptools.lock \
 && find /usr/local/lib/python3.11/site-packages -name __pycache__ -type d -prune -exec rm -rf {} +

# The voice-match stage's own Transformers (4.46.3, for SoulX-Singer's and VevoSing's Llama code), first on that process's path
# only (martlet_singing.match; ACE-Step keeps 4.57).
COPY host/requirements-match.lock /tmp/requirements-match.lock
RUN python3.11 -m pip install --no-deps --require-hashes --no-compile --target /opt/martlet-singing/match-site \
      -r /tmp/requirements-match.lock \
 && rm -rf /opt/martlet-singing/match-site/bin /tmp/requirements-match.lock

COPY --from=sources /src /opt/martlet-singing/src
COPY martlet_singing /opt/martlet-singing/app/martlet_singing
ENV PYTHONPATH=/opt/martlet-singing/app:/opt/martlet-singing/src/ace-step:/opt/martlet-singing/src/ace-step/acestep/third_parts/nano-vllm:/opt/martlet-singing/src/soulx-singer \
    MARTLET_SINGING_ROOT=/opt/martlet-singing MARTLET_SINGING_SOURCES=/opt/martlet-singing/src MARTLET_SINGING_PORT=50085 \
    MARTLET_SINGING_JOBS=/tmp/martlet-singing-jobs MARTLET_SINGING_MATCH_SITE=/opt/martlet-singing/match-site \
    HF_HUB_OFFLINE=1 TRANSFORMERS_OFFLINE=1 HF_HOME=/opt/martlet-singing/models/hf-cache \
    XDG_CACHE_HOME=/opt/martlet-singing/models TORCH_HOME=/tmp/torch TRITON_CACHE_DIR=/tmp/triton \
    MPLCONFIGDIR=/tmp/matplotlib NUMBA_CACHE_DIR=/tmp/numba
RUN python3.11 -c "import acestep.handler, acestep.inference, acestep.llm_inference, nanovllm, demucs.apply, demucs.states, \
librosa, soundfile, martlet_singing.engines; print('singing imports ok')" \
 && PYTHONPATH=/opt/martlet-singing/match-site:$PYTHONPATH python3.11 -c "import transformers, martlet_singing.match, \
soulxsinger.models.soulxsinger_svc, preprocess.tools.f0_extraction; assert transformers.__version__ == '4.46.3'; \
print('voice match imports ok')" \
 && find /opt/martlet-singing /usr/local/lib/python3.11/site-packages -name __pycache__ -type d -prune -exec rm -rf {} + \
 && printf '#!/bin/sh\nexec /usr/local/bin/python3.11 -m martlet_singing.host "$@"\n' > /usr/local/bin/martlet-singing \
 && chmod 0755 /usr/local/bin/martlet-singing \
 && useradd --uid 10055 --user-group --no-create-home --home-dir /tmp --shell /usr/sbin/nologin martlet-singing \
 && mkdir -p /opt/martlet-singing/models && chown martlet-singing:martlet-singing /opt/martlet-singing/models \
 && chmod -R go-w /usr/local/lib/python3.11/site-packages /opt/martlet-singing/app /opt/martlet-singing/src \
      /opt/martlet-singing/match-site

USER 10055:10055
WORKDIR /tmp
EXPOSE 50085
ENTRYPOINT ["/usr/local/bin/python3.11", "-m", "martlet_singing.host"]
CMD ["serve"]
