# Martlet voice recognition host image: the voice-recognition role (workers/voice-recognition/host/martlet_voice_recognition_host.py)
# with the hash-pinned sherpa-onnx 1.13.8 runtime and the desktop build's own voice models (WeSpeaker ResNet34-LM and pyannote
# segmentation 3.0, each checked for its pinned size and SHA-256 and baked in, so the container downloads nothing when it runs).
# ACCELERATOR=gpu (the role's compose.gpu.yaml) installs sherpa-onnx's CUDA 12 build with NVIDIA's CUDA and cuDNN libraries
# from PyPI; it needs an NVIDIA driver for CUDA 12. Built on the host by "martlet-host add voice-recognition" with context
# workers (compose build). Package trees and models are root-owned and read-only for the unprivileged runtime user. Audio is
# analyzed in memory and never stored. ORT_DISABLE_TELEMETRY turns off ONNX Runtime's usage telemetry to Microsoft.
FROM python:3.12.10-slim-bookworm@sha256:fd95fa221297a88e1cf49c55ec1828edd7c5a428187e67b5d1805692d11588db

ARG ACCELERATOR=cpu
ENV ORT_DISABLE_TELEMETRY=1 PYTHONDONTWRITEBYTECODE=1 PIP_NO_CACHE_DIR=1 PIP_DISABLE_PIP_VERSION_CHECK=1 PIP_ROOT_USER_ACTION=ignore \
    PIP_DEFAULT_TIMEOUT=60 PIP_RETRIES=10 PIP_RESUME_RETRIES=10

# The base image's pip fails the whole build when a download stalls or the connection drops; pip 26.2.1 resumes it.
RUN echo 'pip==26.2.1 --hash=sha256:71138adf1f4ca900cdb7d289c21b7494329f2332b6d85f0e1c42108c0384ed3e' > /tmp/pip.lock \
 && python3.12 -m pip install --no-deps --require-hashes --no-compile -r /tmp/pip.lock && rm -f /tmp/pip.lock

COPY voice-recognition/host/requirements.lock voice-recognition/host/requirements-cuda.lock /opt/martlet-voice-recognition/build/
# sherpa-onnx's CUDA build links ALSA (libasound2) for its microphone tools; the service never opens a sound device.
RUN if [ "${ACCELERATOR}" = gpu ]; then \
      apt-get update && apt-get install -y --no-install-recommends libasound2 && rm -rf /var/lib/apt/lists/*; \
    fi
RUN case "${ACCELERATOR}" in \
      cpu) lock=/opt/martlet-voice-recognition/build/requirements.lock ;; \
      gpu) lock=/opt/martlet-voice-recognition/build/requirements-cuda.lock ;; \
      *) echo "ACCELERATOR must be cpu or gpu" >&2; exit 1 ;; \
    esac \
 && python3.12 -m pip install --no-deps --require-hashes --no-compile -r "${lock}" \
 && rm -rf /opt/martlet-voice-recognition/build \
 && python3.12 -c "import sherpa_onnx, importlib.metadata as m; assert m.version('sherpa-onnx').startswith('1.13.8')" \
 && find /usr/local/lib/python3.12/site-packages -name __pycache__ -type d -prune -exec rm -rf {} + \
 && find /usr/local/lib/python3.12/site-packages -name '*.pyc' -type f -exec rm -f {} + \
 && chmod -R go-w /usr/local/lib/python3.12/site-packages

# NVIDIA's libraries from PyPI (only in the gpu build) for ONNX Runtime's CUDA provider inside sherpa-onnx.
ENV LD_LIBRARY_PATH=/usr/local/lib/python3.12/site-packages/nvidia/cuda_runtime/lib:/usr/local/lib/python3.12/site-packages/nvidia/cublas/lib:/usr/local/lib/python3.12/site-packages/nvidia/cudnn/lib:/usr/local/lib/python3.12/site-packages/nvidia/cufft/lib:/usr/local/lib/python3.12/site-packages/nvidia/curand/lib:/usr/local/lib/python3.12/site-packages/nvidia/cuda_nvrtc/lib:/usr/local/lib/python3.12/site-packages/nvidia/nvjitlink/lib

COPY voice-recognition/host/martlet_voice_recognition_host.py common/martlet_worker_host.py /opt/martlet-voice-recognition/host/
# The models come from the same pinned URLs as the desktop build's; the build fails when one doesn't match its SHA-256. The
# self-test then analyzes generated sound with them on the processor (no graphics card while building).
RUN python3.12 /opt/martlet-voice-recognition/host/martlet_voice_recognition_host.py fetch-models /opt/martlet-voice-recognition/models \
 && python3.12 /opt/martlet-voice-recognition/host/martlet_voice_recognition_host.py selftest --models /opt/martlet-voice-recognition/models \
 && find /usr/local/lib/python3.12/site-packages /opt/martlet-voice-recognition -name __pycache__ -type d -prune -exec rm -rf {} + \
 && chmod -R go-w /opt/martlet-voice-recognition

ENV MARTLET_VOICE_RECOGNITION_ROOT=/opt/martlet-voice-recognition MARTLET_VOICE_RECOGNITION_PORT=50091 \
    MARTLET_VOICE_RECOGNITION_ACCELERATOR=${ACCELERATOR}
RUN useradd --uid 10059 --user-group --no-create-home --home-dir /nonexistent --shell /usr/sbin/nologin martlet-voices

USER 10059:10059
WORKDIR /opt/martlet-voice-recognition
EXPOSE 50091
ENTRYPOINT ["/usr/local/bin/python3.12", "/opt/martlet-voice-recognition/host/martlet_voice_recognition_host.py"]
CMD ["serve"]
