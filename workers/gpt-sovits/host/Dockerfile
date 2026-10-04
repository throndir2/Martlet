# Martlet GPT-SoVITS host role image: GPT-SoVITS 20250606v2pro (source pinned by commit and SHA-256) in its own Python
# environment (its dependencies conflict with other voice engines), hash-locked inference dependencies with PyTorch 2.6.0
# CUDA 12.4, its English (NLTK) and Japanese (OpenJTalk) text data, and Martlet's worker and loopback HTTP front
# (martlet_gpt_sovits). Built on the host by "martlet-host add gpt-sovits" (compose build, context workers/gpt-sovits).
# Models are not in the image: the role's provisioning step downloads the pinned files into the
# martlet-gpt-sovits-models volume, mounted at GPT_SoVITS/pretrained_models.
FROM python:3.11.13-slim-bookworm@sha256:86adf8dbadc3d6e82ee5dd2c74bec2e1c2467cdad47886280501df722372d2e1

ENV PYTHONDONTWRITEBYTECODE=1 PIP_NO_CACHE_DIR=1 PIP_DISABLE_PIP_VERSION_CHECK=1 PIP_ROOT_USER_ACTION=ignore \
    PIP_DEFAULT_TIMEOUT=60 PIP_RETRIES=10 PIP_RESUME_RETRIES=10
# The base image's pip fails the whole build when a download stalls or the connection drops; pip 26.2.1 resumes it.
RUN echo 'pip==26.2.1 --hash=sha256:71138adf1f4ca900cdb7d289c21b7494329f2332b6d85f0e1c42108c0384ed3e' > /tmp/pip.lock \
 && python3.11 -m pip install --no-deps --require-hashes --no-compile -r /tmp/pip.lock && rm -f /tmp/pip.lock
ARG GSV_COMMIT=d7c2210da8c013e81a94bfc7b811a477c99fd506
ARG GSV_SHA256=467fc3baf29e9423d8741270b243cc78510773843db811eeafd47215c1e56d9c
ARG NLTK_COMMIT=550b6625bcef1f2abff2ff770a5a0d272c9c6b2a
ARG OPENJTALK_URL=https://github.com/r9y9/open_jtalk/releases/download/v1.11.1/open_jtalk_dic_utf_8-1.11.tar.gz
ARG OPENJTALK_SHA256=fe6ba0e43542cef98339abdffd903e062008ea170b04e7e2a35da805902f382a

COPY host/requirements.lock /tmp/requirements.lock
# jieba_fast and pyopenjtalk build from source; the compilers are removed again afterwards.
RUN apt-get update \
 && apt-get install -y --no-install-recommends build-essential cmake curl ca-certificates libsndfile1 \
 && python3.11 -m pip install --require-hashes -r /tmp/requirements.lock \
 && curl -fsSL "https://codeload.github.com/RVC-Boss/GPT-SoVITS/tar.gz/${GSV_COMMIT}" -o /tmp/gsv.tgz \
 && echo "${GSV_SHA256}  /tmp/gsv.tgz" | sha256sum -c - \
 && mkdir -p /opt/gpt-sovits && tar -xzf /tmp/gsv.tgz -C /opt/gpt-sovits \
 && mv "/opt/gpt-sovits/GPT-SoVITS-${GSV_COMMIT}" /opt/gpt-sovits/GPT-SoVITS \
 && curl -fsSL "${OPENJTALK_URL}" -o /tmp/openjtalk.tgz \
 && echo "${OPENJTALK_SHA256}  /tmp/openjtalk.tgz" | sha256sum -c - \
 && tar -xzf /tmp/openjtalk.tgz -C /opt/gpt-sovits \
 && mkdir -p /usr/local/share/nltk_data/taggers /usr/local/share/nltk_data/corpora \
 && for item in \
      "taggers/averaged_perceptron_tagger e1f13cf2532daadfd6f3bc481a49859f0b8ea6432ccdcd83e6a49a5f19008de9" \
      "taggers/averaged_perceptron_tagger_eng 6025f530624335c67d6547d44757b357b4e79bae030a0383e9887a92c1718f0b" \
      "corpora/cmudict d07cca47fd72ad32ea9d8ad1219f85301eeaf4568f8b6b73747506a71fb5afd6"; do \
      set -- $item; \
      curl -fsSL "https://raw.githubusercontent.com/nltk/nltk_data/${NLTK_COMMIT}/packages/$1.zip" -o "/usr/local/share/nltk_data/$1.zip" \
      && echo "$2  /usr/local/share/nltk_data/$1.zip" | sha256sum -c - \
      && python3.11 -m zipfile -e "/usr/local/share/nltk_data/$1.zip" "$(dirname "/usr/local/share/nltk_data/$1")" || exit 1; \
    done \
 && apt-get purge -y build-essential cmake curl && apt-get autoremove -y && rm -rf /var/lib/apt/lists/* /tmp/*

COPY martlet_gpt_sovits /opt/gpt-sovits/martlet/martlet_gpt_sovits
ENV PYTHONPATH=/opt/gpt-sovits/martlet MARTLET_GPT_SOVITS_ROOT=/opt/gpt-sovits MARTLET_GPT_SOVITS_PORT=50082 \
    OPEN_JTALK_DICT_DIR=/opt/gpt-sovits/open_jtalk_dic_utf_8-1.11 NLTK_DATA=/usr/local/share/nltk_data \
    HF_HUB_OFFLINE=1 TRANSFORMERS_OFFLINE=1 HF_DATASETS_OFFLINE=1 MPLCONFIGDIR=/tmp/matplotlib
# GPT-SoVITS writes caches and its config next to its sources (text/engdict_cache.pickle, configs/tts_infer.yaml,
# TEMP/ja), so the tree belongs to the runtime user; the English and Japanese front ends are loaded once here to check them.
RUN cd /opt/gpt-sovits/GPT-SoVITS \
 && python3.11 -c "import sys; sys.path += ['.', 'GPT_SoVITS']; import text.english, text.japanese, pyopenjtalk; pyopenjtalk.g2p('こんにちは')" \
 && printf '#!/bin/sh\nexec /usr/local/bin/python3.11 -m martlet_gpt_sovits.host "$@"\n' > /usr/local/bin/martlet-gpt-sovits \
 && chmod 0755 /usr/local/bin/martlet-gpt-sovits \
 && useradd --uid 10051 --user-group --no-create-home --home-dir /tmp --shell /usr/sbin/nologin martlet-gpt-sovits \
 && mkdir -p /opt/gpt-sovits/GPT-SoVITS/GPT_SoVITS/pretrained_models \
 && chown -R martlet-gpt-sovits:martlet-gpt-sovits /opt/gpt-sovits/GPT-SoVITS \
 && chmod -R go-w /opt/gpt-sovits/martlet

USER 10051:10051
WORKDIR /opt/gpt-sovits/GPT-SoVITS
EXPOSE 50082
ENTRYPOINT ["/usr/local/bin/python3.11", "-m", "martlet_gpt_sovits.host"]
CMD ["serve"]
