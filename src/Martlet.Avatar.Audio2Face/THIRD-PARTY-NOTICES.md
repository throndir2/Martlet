# Audio2Face adapter third-party notices

## NVIDIA ACE protocol definitions

The eight unmodified `Protos\nvidia_ace.*.proto` files are from:

- Repository: <https://github.com/NVIDIA/Audio2Face-3D-Samples>
- Tag: `v2.0`
- Exact commit: `a2d0150043be7dc15db2fad8193a78b660e1100f`
- Directory: `proto/protobuf_files`
- Copyright: Copyright (c) 2024 NVIDIA CORPORATION & AFFILIATES. All rights reserved.
- SPDX license: `Apache-2.0`

Original copyright and license headers are retained. The Apache 2.0 license
text is included at `Protos\LICENSE-2.0.txt`. Generated C# is produced locally by
Grpc.Tools from these definitions. No NVIDIA model, SDK binary, NIM container,
audio example, or generated NVIDIA animation is distributed.

Upstream documentation:

- [NIM product overview](https://docs.nvidia.com/nim/digital-human/a2f-3d/latest/index.html)
- [NIM overview and usage restrictions](https://docs.nvidia.com/ace/audio2face-3d-microservice/latest/text/getting-started/overview.html)
- [Bidirectional RPC contract](https://docs.nvidia.com/ace/audio2face-3d-microservice/latest/text/interacting/a2f-rpc.html)
- [v2 sample, protocol version, service port example and resampling](https://docs.nvidia.com/ace/audio2face-3d-microservice/latest/text/interacting/sample-app.html)
- [Separate native SDK](https://github.com/NVIDIA/Audio2Face-3D-SDK)

Protocol-file licensing is not a grant of NIM product or model rights. The
separate native SDK's MIT license likewise does not replace NIM/model terms.
Operators must review the terms for the exact independently provisioned runtime
and model. This adapter does not accept terms or fetch gated artifacts.

## NuGet packages

Exact centrally pinned direct dependencies:

| Package | Version | Package license | Use |
| --- | --- | --- | --- |
| Google.Protobuf | 3.36.2 | BSD-3-Clause | Protocol messages |
| Grpc.Net.Client | 2.84.0 | Apache-2.0 | HTTP/2 client |
| Grpc.Tools | 2.84.0 | Apache-2.0 | Private build-time code generator |
| Grpc.AspNetCore.Server | 2.84.0 | Apache-2.0 | Test fixture only |

Package license expressions were verified from the exact NuGet version
manifests. Transitive versions/content hashes are recorded in project lock files.
