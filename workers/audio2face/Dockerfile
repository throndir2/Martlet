# Martlet local Audio2Face service image: NVIDIA's open-source Audio2Face-3D SDK (MIT) at a pinned commit, built
# against CUDA 12.8 and TensorRT 10.9 from NVIDIA's public CUDA images and apt repository (no NVIDIA account or NGC
# key), with Martlet's gRPC front (server/) speaking the Audio2Face-3D NIM's A2FControllerService contract.
# The models are not in the image: "martlet-a2f serve" downloads the chosen one from Hugging Face on first start
# (pinned revisions, SHA-256 checked, NVIDIA Open Model License) and builds its TensorRT engine for this GPU.
# Build: docker build -t martlet-audio2face:1 workers/audio2face
ARG TRT=10.9.0.34-1+cuda12.8

FROM nvidia/cuda:12.8.1-devel-ubuntu24.04 AS build
ARG TRT
ARG SDK_COMMIT=1ca0f02535ed774f5dbcd724a31cd486368dc783
# Turing (RTX 20) and newer, like the SDK's own list without Jetson.
ARG CUDA_ARCHITECTURES=75;80;86;89;90;100;120
ENV DEBIAN_FRONTEND=noninteractive
# TensorRT's runtime libraries, headers and trtexec only: its -dev packages add 2 GB of static libraries, so the
# unversioned link names are made here instead.
RUN apt-get update && apt-get install -y --no-install-recommends \
      git ca-certificates curl python3 build-essential \
      libnvinfer10=${TRT} libnvinfer-plugin10=${TRT} libnvonnxparsers10=${TRT} libnvinfer-lean10=${TRT} \
      libnvinfer-vc-plugin10=${TRT} libnvinfer-dispatch10=${TRT} libnvinfer-bin=${TRT} \
      libnvinfer-headers-dev=${TRT} libnvinfer-headers-plugin-dev=${TRT} \
 && rm -rf /var/lib/apt/lists/* \
 && cd /usr/lib/x86_64-linux-gnu \
 && ln -s libnvinfer.so.10 libnvinfer.so && ln -s libnvinfer_plugin.so.10 libnvinfer_plugin.so \
 && ln -s libnvonnxparser.so.10 libnvonnxparser.so
# fetch_deps.sh pulls the SDK's pinned build dependencies (CMake, Ninja, Eigen, nlohmann/json, ...) with NVIDIA's
# packman from its public package server; its test data is not needed.
RUN git clone https://github.com/NVIDIA/Audio2Face-3D-SDK.git /sdk \
 && cd /sdk && git checkout --quiet "${SDK_COMMIT}" \
 && sed -i '/audio2x-sdk-test-data/,/<\/dependency>/d' deps/target-deps.packman.xml \
 && ./fetch_deps.sh release \
 && _deps/build-deps/cmake/bin/cmake -B _build -G Ninja -S . -DCMAKE_BUILD_TYPE=Release \
      -DCMAKE_MAKE_PROGRAM=/sdk/_deps/build-deps/ninja/ninja "-DCMAKE_CUDA_ARCHITECTURES=${CUDA_ARCHITECTURES}" \
 && _deps/build-deps/cmake/bin/cmake --build _build --target audio2x --parallel
# gRPC comes after the SDK build: its zlib headers would otherwise shadow the SDK's pinned zlib 1.3.1.
RUN apt-get update && apt-get install -y --no-install-recommends \
      cmake ninja-build pkg-config libgrpc++-dev libprotobuf-dev protobuf-compiler protobuf-compiler-grpc \
 && rm -rf /var/lib/apt/lists/*
COPY protos /src/protos
COPY server /src/server
RUN cmake -S /src/server -B /src/build -G Ninja -DCMAKE_BUILD_TYPE=Release -DAUDIO2X_SDK_DIR=/sdk/_build/audio2x-sdk \
 && cmake --build /src/build \
 && mkdir -p /out/lib /out/martlet-a2f/sdk \
 && cp -a /usr/lib/x86_64-linux-gnu/libnvinfer*.so.10* /usr/lib/x86_64-linux-gnu/libnvonnxparser.so.10* /out/lib/ \
 && cp /sdk/_build/audio2x-sdk/lib/libaudio2x.so /src/build/martlet-a2f-server /usr/src/tensorrt/bin/trtexec /out/martlet-a2f/ \
 && cp /sdk/LICENSE.txt /out/martlet-a2f/sdk/

FROM nvidia/cuda:12.8.1-runtime-ubuntu24.04
ARG TRT
ENV DEBIAN_FRONTEND=noninteractive
RUN apt-get update && apt-get install -y --no-install-recommends ca-certificates curl libgrpc++1.51t64 libprotobuf32t64 \
 && rm -rf /var/lib/apt/lists/*
# TensorRT's runtime libraries come from the build stage, so its 2 GB package is downloaded once.
COPY --from=build /out/lib/ /usr/lib/x86_64-linux-gnu/
COPY --from=build /out/martlet-a2f/ /opt/martlet-a2f/
COPY models.lock martlet-a2f THIRD-PARTY-NOTICES.txt /opt/martlet-a2f/
RUN ldconfig \
 && echo "${TRT}" > /opt/martlet-a2f/tensorrt-version \
 && chmod 0755 /opt/martlet-a2f/martlet-a2f /opt/martlet-a2f/martlet-a2f-server /opt/martlet-a2f/trtexec \
 && ln -s /opt/martlet-a2f/martlet-a2f /usr/local/bin/martlet-a2f
ENV NVIDIA_DRIVER_CAPABILITIES=compute,utility
VOLUME /models
ENTRYPOINT ["martlet-a2f"]
CMD ["serve"]