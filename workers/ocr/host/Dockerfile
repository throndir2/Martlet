# Martlet OCR host image: RapidOCR with PP-OCRv4 ONNX models from rapidocr_onnxruntime 1.4.4.
# Built on the host by "martlet-host add ocr" with context workers/ocr. Package trees are root-owned and read-only for
# the unprivileged runtime user. Images are read in memory and never stored.
FROM python:3.12.10-slim-bookworm@sha256:fd95fa221297a88e1cf49c55ec1828edd7c5a428187e67b5d1805692d11588db

ENV PYTHONDONTWRITEBYTECODE=1 PIP_NO_CACHE_DIR=1 PIP_DISABLE_PIP_VERSION_CHECK=1 PIP_ROOT_USER_ACTION=ignore     PIP_DEFAULT_TIMEOUT=60 PIP_RETRIES=10 PIP_RESUME_RETRIES=10

RUN mkdir -p /opt/martlet-ocr/build  && echo 'pip==26.2.1 --hash=sha256:71138adf1f4ca900cdb7d289c21b7494329f2332b6d85f0e1c42108c0384ed3e' > /opt/martlet-ocr/build/pip.lock  && python3.12 -m pip install --no-deps --require-hashes --no-compile -r /opt/martlet-ocr/build/pip.lock  && rm -f /opt/martlet-ocr/build/pip.lock

COPY host/requirements.lock /opt/martlet-ocr/build/requirements.lock
RUN python3.12 -m pip install --no-deps --require-hashes --no-compile -r /opt/martlet-ocr/build/requirements.lock  && rm -f /opt/martlet-ocr/build/requirements.lock  && python3.12 -c "from rapidocr_onnxruntime import RapidOCR; import cv2, numpy; RapidOCR()"  && find /usr/local/lib/python3.12/site-packages -name __pycache__ -type d -prune -exec rm -rf {} +  && find /usr/local/lib/python3.12/site-packages -name '*.pyc' -type f -exec rm -f {} +

COPY host/martlet_ocr_host.py /opt/martlet-ocr/host/martlet_ocr_host.py
ENV MARTLET_OCR_PORT=50087
RUN useradd --uid 10057 --user-group --no-create-home --home-dir /nonexistent --shell /usr/sbin/nologin martlet-ocr  && chmod -R go-w /usr/local/lib/python3.12/site-packages /opt/martlet-ocr/host

USER 10057:10057
WORKDIR /opt/martlet-ocr
EXPOSE 50087
ENTRYPOINT ["/usr/local/bin/python3.12", "/opt/martlet-ocr/host/martlet_ocr_host.py"]
CMD ["serve"]
