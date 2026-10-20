# Martlet singing host role image: ACE-Step 1.5 (MIT) with its nano-vllm planner backend, SoulX-Singer-SVC (Apache-2.0),
# Demucs 4.0.1 (MIT), Amphion's code (MIT) for the optional VevoSing and Vevo2, and the other models the owner may add
# (HeartMuLa, DiffRhythm, YuE2, Seed-VC: each only when chosen, with its own packages), with hash-pinned PyTorch 2.10
# (CUDA 12.8) and the singing worker and its loopback HTTP front (martlet_singing). Built on the host by
# "martlet-host add singing" (compose build, context workers). Sources are fetched at pinned commits (the commit
# ID is checked); models are not in the image: the role's provisioning step downloads the pinned files into the
# martlet-singing-models volume, and nothing is downloaded at run time (HF_HUB_OFFLINE; every model loads from local paths).
FROM python:3.11-slim-bookworm@sha256:2333bd330d12de02514770b3585cad313644316047cdee24a7acfdece6de6efb AS sources

RUN apt-get update && apt-get install -y --no-install-recommends git ca-certificates && rm -rf /var/lib/apt/lists/*
COPY singing/martlet_singing/pins.py /tmp/pins.py
# Each source at its pinned commit; the checkout is refused unless HEAD is exactly that commit.
RUN set -eu; \
    fetch() { git init -q "$1" && git -C "$1" fetch -q --depth 1 "$2" "$3" && git -C "$1" checkout -q FETCH_HEAD \
      && test "$(git -C "$1" rev-parse HEAD)" = "$3" && rm -rf "$1/.git"; }; \
    eval "$(python3 -c 'import importlib.util as u; s=u.spec_from_file_location("p","/tmp/pins.py"); m=u.module_from_spec(s); s.loader.exec_module(m); \
print(f"fetch /src/ace-step {m.ACE_STEP_REPOSITORY} {m.ACE_STEP_COMMIT}; fetch /src/soulx-singer {m.SOULX_REPOSITORY} {m.SOULX_COMMIT}; fetch /src/amphion {m.AMPHION_REPOSITORY} {m.AMPHION_COMMIT}")')"; \
    rm -rf /src/amphion/egs /src/amphion/imgs /src/amphion/bins /src/soulx-singer/example /src/soulx-singer/assets \
      /src/ace-step/docs /src/ace-step/assets
# The models the owner added (role choices SINGING_HEARTMULA, SINGING_DIFFRHYTHM, SINGING_SEEDVC: yes or no): their sources
# at pinned commits, fetched only when chosen. Vevo2's code is in the Amphion checkout above; YuE2's is its wheel below.
ARG SINGING_HEARTMULA=no
ARG SINGING_DIFFRHYTHM=no
ARG SINGING_SEEDVC=no
RUN set -eu; \
    fetch() { git init -q "$1" && git -C "$1" fetch -q --depth 1 "$2" "$3" && git -C "$1" checkout -q FETCH_HEAD \
      && test "$(git -C "$1" rev-parse HEAD)" = "$3" && rm -rf "$1/.git"; }; \
    eval "$(python3 -c 'import importlib.util as u; s=u.spec_from_file_location("p","/tmp/pins.py"); m=u.module_from_spec(s); s.loader.exec_module(m); \
print(f"HEARTLIB=\"{m.HEARTLIB_REPOSITORY} {m.HEARTLIB_COMMIT}\"; DIFFRHYTHM=\"{m.DIFFRHYTHM_REPOSITORY} {m.DIFFRHYTHM_COMMIT}\"; SEEDVC=\"{m.SEEDVC_REPOSITORY} {m.SEEDVC_COMMIT}\"")')"; \
    if [ "$SINGING_HEARTMULA" = yes ]; then fetch /src/heartlib $HEARTLIB && rm -rf /src/heartlib/assets; fi; \
    if [ "$SINGING_DIFFRHYTHM" = yes ]; then fetch /src/diffrhythm $DIFFRHYTHM; fi; \
    if [ "$SINGING_SEEDVC" = yes ]; then fetch /src/seed-vc $SEEDVC && rm -rf /src/seed-vc/examples /src/seed-vc/assets; fi

FROM python:3.11-slim-bookworm@sha256:2333bd330d12de02514770b3585cad313644316047cdee24a7acfdece6de6efb

ENV PYTHONDONTWRITEBYTECODE=1 PIP_NO_CACHE_DIR=1 PIP_DISABLE_PIP_VERSION_CHECK=1 PIP_ROOT_USER_ACTION=ignore \
    PIP_DEFAULT_TIMEOUT=60 PIP_RETRIES=10 PIP_RESUME_RETRIES=10

# The base image's pip fails the whole build when a download stalls or the connection drops; pip 26.2.1 resumes it.
RUN echo 'pip==26.2.1 --hash=sha256:71138adf1f4ca900cdb7d289c21b7494329f2332b6d85f0e1c42108c0384ed3e' > /tmp/pip.lock \
 && python3.11 -m pip install --no-deps --require-hashes --no-compile -r /tmp/pip.lock && rm -f /tmp/pip.lock

COPY singing/host/requirements.lock /tmp/requirements.lock
RUN grep -A1 '^setuptools==' /tmp/requirements.lock | sed 's/ \\$//; s/^ *//' | tr '\n' ' ' > /tmp/setuptools.lock \
 && python3.11 -m pip install --no-deps --require-hashes --no-compile -r /tmp/setuptools.lock \
 && python3.11 -m pip install --no-deps --require-hashes --no-compile --no-build-isolation \
      --extra-index-url https://download.pytorch.org/whl/cu128 -r /tmp/requirements.lock \
 && rm -f /tmp/requirements.lock /tmp/setuptools.lock \
 && find /usr/local/lib/python3.11/site-packages -name __pycache__ -type d -prune -exec rm -rf {} +

# The voice-match stage's own Transformers (4.46.3, for SoulX-Singer's and VevoSing's Llama code), first on that process's path
# only (martlet_singing.match; ACE-Step keeps 4.57).
COPY singing/host/requirements-match.lock /tmp/requirements-match.lock
RUN python3.11 -m pip install --no-deps --require-hashes --no-compile --target /opt/martlet-singing/match-site \
      -r /tmp/requirements-match.lock \
 && rm -rf /opt/martlet-singing/match-site/bin /tmp/requirements-match.lock

# Each added model's own packages (host/requirements-<name>.lock: only what it adds to or pins differently from the base
# image), in /opt/martlet-singing/extras/<name>, first on its own process's path only. DiffRhythm's lyrics front end needs
# espeak-ng.
ARG SINGING_HEARTMULA=no
ARG SINGING_DIFFRHYTHM=no
ARG SINGING_SEEDVC=no
ARG SINGING_YUE=no
COPY singing/host/requirements-heartmula.lock singing/host/requirements-diffrhythm.lock singing/host/requirements-seedvc.lock \
     singing/host/requirements-yue.lock /tmp/extras/
RUN set -eu; mkdir -p /opt/martlet-singing/extras; \
    for name in heartmula diffrhythm seedvc yue; do \
      variable="SINGING_$(echo "$name" | tr a-z A-Z)"; eval "chosen=\${$variable}"; \
      if [ "$chosen" = yes ]; then \
        python3.11 -m pip install --no-deps --require-hashes --no-compile --no-build-isolation \
          --target "/opt/martlet-singing/extras/$name" -r "/tmp/extras/requirements-$name.lock"; \
        rm -rf "/opt/martlet-singing/extras/$name/bin"; \
      fi; \
    done; \
    if [ "$SINGING_DIFFRHYTHM" = yes ]; then \
      apt-get update && apt-get install -y --no-install-recommends espeak-ng && rm -rf /var/lib/apt/lists/*; \
    fi; \
    rm -rf /tmp/extras

COPY --from=sources /src /opt/martlet-singing/src
COPY singing/martlet_singing /opt/martlet-singing/app/martlet_singing
COPY common/martlet_worker_host.py /opt/martlet-singing/app/martlet_worker_host.py
ENV PYTHONPATH=/opt/martlet-singing/app:/opt/martlet-singing/src/ace-step:/opt/martlet-singing/src/ace-step/acestep/third_parts/nano-vllm:/opt/martlet-singing/src/soulx-singer \
    MARTLET_SINGING_ROOT=/opt/martlet-singing MARTLET_SINGING_SOURCES=/opt/martlet-singing/src MARTLET_SINGING_PORT=50085 \
    MARTLET_SINGING_JOBS=/tmp/martlet-singing-jobs MARTLET_SINGING_MATCH_SITE=/opt/martlet-singing/match-site \
    MARTLET_SINGING_EXTRAS=/opt/martlet-singing/extras \
    HF_HUB_OFFLINE=1 TRANSFORMERS_OFFLINE=1 HF_HOME=/opt/martlet-singing/models/huggingface \
    XDG_CACHE_HOME=/opt/martlet-singing/models TORCH_HOME=/tmp/torch TRITON_CACHE_DIR=/tmp/triton \
    MPLCONFIGDIR=/tmp/matplotlib NUMBA_CACHE_DIR=/tmp/numba
# The import checks write their caches to /tmp, and the models folder is left empty and owned by the service, so a new
# martlet-singing-models volume (Docker copies the image's folder into it) has nothing the service can't write.
RUN export HF_HOME=/tmp/hf-build XDG_CACHE_HOME=/tmp/cache-build \
 && python3.11 -c "import acestep.handler, acestep.inference, acestep.llm_inference, nanovllm, demucs.apply, demucs.states, \
librosa, soundfile, martlet_singing.engines; print('singing imports ok')" \
 && PYTHONPATH=/opt/martlet-singing/match-site:$PYTHONPATH python3.11 -c "import transformers, martlet_singing.match, \
soulxsinger.models.soulxsinger_svc, preprocess.tools.f0_extraction; assert transformers.__version__ == '4.46.3'; \
print('voice match imports ok')" \
 && rm -rf /tmp/hf-build /tmp/cache-build /opt/martlet-singing/models \
 && find /opt/martlet-singing /usr/local/lib/python3.11/site-packages -name __pycache__ -type d -prune -exec rm -rf {} + \
 && printf '#!/bin/sh\nexec /usr/local/bin/python3.11 -m martlet_singing.host "$@"\n' > /usr/local/bin/martlet-singing \
 && chmod 0755 /usr/local/bin/martlet-singing \
 && useradd --uid 10055 --user-group --no-create-home --home-dir /tmp --shell /usr/sbin/nologin martlet-singing \
 && mkdir -p /opt/martlet-singing/models && chown martlet-singing:martlet-singing /opt/martlet-singing/models \
 && chmod -R go-w /usr/local/lib/python3.11/site-packages /opt/martlet-singing/app /opt/martlet-singing/src \
      /opt/martlet-singing/match-site /opt/martlet-singing/extras

USER 10055:10055
WORKDIR /tmp
EXPOSE 50085
ENTRYPOINT ["/usr/local/bin/python3.11", "-m", "martlet_singing.host"]
CMD ["serve"]
