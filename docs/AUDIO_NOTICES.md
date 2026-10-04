# Audio dependency inventory and notices

F03b development dependency inventory, not a release SBOM, project license
grant, or clean-machine qualification. No NAudio binary is committed.
`Martlet.Audio` lockfiles pin resolved packages/content hashes; the portable
target does not reference these packages.

| Package | Resolved version | Upstream license metadata |
| --- | --- | --- |
| NAudio.Wasapi (direct, Windows target) | 3.1.0 | MIT, Mark Heath; official `v3.1.0`, repository commit `0aaef29d04bec9567bdf2f669036fabecc33a2e2` |
| NAudio.Core (transitive) | 3.1.0 | MIT, Mark Heath; same repository commit |
| System.Numerics.Tensors (transitive) | 9.0.0 | MIT, Microsoft; runtime repository commit `9d5a6a9aa463d6d10b0b0ba6d5982cc82f363dc3` |
| NVorbis (direct, Windows target) | 0.10.5 | MIT, Andrew Ward; Ogg Vorbis decoding for *Add a voice* |
| Concentus (direct, Windows target) | 2.2.2 | BSD-3-Clause, the Opus contributors and Logan Stromberg; managed Ogg Opus decoding for *Add a voice* (its native opus.dll path is never used) |

NuGet metadata: [NAudio.Wasapi](https://api.nuget.org/v3-flatcontainer/naudio.wasapi/3.1.0/naudio.wasapi.nuspec),
[NAudio.Core](https://api.nuget.org/v3-flatcontainer/naudio.core/3.1.0/naudio.core.nuspec),
[System.Numerics.Tensors](https://api.nuget.org/v3-flatcontainer/system.numerics.tensors/9.0.0/system.numerics.tensors.nuspec),
[NVorbis](https://api.nuget.org/v3-flatcontainer/nvorbis/0.10.5/nvorbis.nuspec),
[Concentus](https://api.nuget.org/v3-flatcontainer/concentus/2.2.2/concentus.nuspec).
The packaged notices carry each archive's own LICENSE file (`NVorbis-LICENSE.txt`,
`Concentus-LICENSE.txt`).
Test-only dependencies retain the foundation pins/notices; no broad upgrades.

## NAudio MIT notice

From [the pinned upstream LICENSE](https://github.com/naudio/NAudio/blob/v3.1.0/LICENSE):

Copyright 2008-2026 Mark Heath

Permission is hereby granted, free of charge, to any person obtaining a copy of
this software and associated documentation files (the "Software"), to deal in
the Software without restriction, including without limitation the rights to
use, copy, modify, merge, publish, distribute, sublicense, and/or sell copies
of the Software, and to permit persons to whom the Software is furnished to
do so, subject to the following conditions:

The above copyright notice and this permission notice shall be included in all
copies or substantial portions of the Software.

THE SOFTWARE IS PROVIDED "AS IS", WITHOUT WARRANTY OF ANY KIND, EXPRESS OR
IMPLIED, INCLUDING BUT NOT LIMITED TO THE WARRANTIES OF MERCHANTABILITY,
FITNESS FOR A PARTICULAR PURPOSE AND NONINFRINGEMENT. IN NO EVENT SHALL THE
AUTHORS OR COPYRIGHT HOLDERS BE LIABLE FOR ANY CLAIM, DAMAGES OR OTHER
LIABILITY, WHETHER IN AN ACTION OF CONTRACT, TORT OR OTHERWISE, ARISING FROM,
OUT OF OR IN CONNECTION WITH THE SOFTWARE OR THE USE OR OTHER DEALINGS IN
THE SOFTWARE.

## Internal packaging integration

The internal payload now retains the NAudio MIT notice and
`packaging\windows\NAudio-THIRD-PARTY-NOTICES.txt`, covering the shipped untrimmed
Core/Wasapi attributions: Ray Molenkamp, Vannatech, Cockos WDL, Stephan Bernsee/
Michael Knight, Steve Underwood/CMU, EarLevel and EQ cookbook work.
System.Numerics.Tensors 9.0.0's full license and third-party notices are
extracted unmodified from the exact package, separately from .NET 10 notices.
The actual raw archives and published DLL bytes are verified by the existing
packaging pipeline, with independent raw SHA-512 pins in `toolchain.json`.
Normal NuGet content hashes and all existing root version pins remain intact.

The shared `SyntheticTone` generator produces the same owned 200 ms test PCM
for SpeakerSmoke and explicit fixture playback; no person's recording, voice,
model or licensed sound asset was introduced.

## Parakeet speech-to-text models (downloaded on request)

Martlet bundles no speech-to-text model. When the owner chooses one in
Companion › Listening › *Parakeet in Martlet* (one confirmation per download),
Martlet downloads that model alone from Hugging Face at a pinned revision into
the data folder's `speech\models\`, checks every file's exact size and SHA-256
(`ParakeetModels` in Martlet.Sherpa) and writes its NOTICE beside it. All three
run on the bundled sherpa-onnx 1.13.8 runtime (see
`src\Martlet.Sherpa\VOICE-RECOGNITION-NOTICES.txt`).

| Model | Weights licence | ONNX export (Apache-2.0) | Revision | NOTICE written |
| --- | --- | --- | --- | --- |
| NVIDIA Parakeet TDT-CTC 110M, its TDT transducer branch (English) | CC BY 4.0, [nvidia/parakeet-tdt_ctc-110m](https://huggingface.co/nvidia/parakeet-tdt_ctc-110m) | fp32, [csukuangfj/sherpa-onnx-nemo-parakeet_tdt_transducer_110m-en-36000](https://huggingface.co/csukuangfj/sherpa-onnx-nemo-parakeet_tdt_transducer_110m-en-36000) | `e9bea5a06247dc3f55319ff23d34b0328f2f5ddf` | `Parakeet-TDT-110M-NOTICE.txt` |
| NVIDIA Parakeet TDT 0.6B v2 (English) | CC BY 4.0, [nvidia/parakeet-tdt-0.6b-v2](https://huggingface.co/nvidia/parakeet-tdt-0.6b-v2) | int8, [csukuangfj/sherpa-onnx-nemo-parakeet-tdt-0.6b-v2-int8](https://huggingface.co/csukuangfj/sherpa-onnx-nemo-parakeet-tdt-0.6b-v2-int8) | `1ab9323565ddb038682214b292f588070a538ce2` | `Parakeet-TDT-0.6B-v2-NOTICE.txt` |
| NVIDIA Parakeet TDT 0.6B v3 (25 European languages) | CC BY 4.0, [nvidia/parakeet-tdt-0.6b-v3](https://huggingface.co/nvidia/parakeet-tdt-0.6b-v3) | int8, [csukuangfj/sherpa-onnx-nemo-parakeet-tdt-0.6b-v3-int8](https://huggingface.co/csukuangfj/sherpa-onnx-nemo-parakeet-tdt-0.6b-v3-int8) | `2bda32ec70b097a55adaa07d9a7173915b43cc78` | `Parakeet-NOTICE.txt` |

The weights are used as converted by sherpa-onnx (int8 quantization for the
0.6B models), with no further change. [DEPENDENCIES](../packaging/windows/DEPENDENCIES.txt)
carries the same attribution for the installer.

## Distribution handoff

The Windows system audio engine performs conversion; no external resampler DLL,
driver, service or model is downloaded. Before any distribution, packaging must
include applicable licenses and bundled third-party notices for the **actual**
resolved NAudio/Core/Tensors binaries and self-contained .NET runtime. In
particular, NAudio.Core contains algorithms beyond the WASAPI adapter used here;
an unused API is not a reason to drop its bundled notices. Use the pinned
package artifacts and [upstream sources](https://github.com/naudio/NAudio/tree/v3.1.0),
plus [runtime license](https://github.com/dotnet/runtime/blob/v9.0.0/LICENSE.TXT)
and [runtime third-party notices](https://github.com/dotnet/runtime/blob/v9.0.0/THIRD-PARTY-NOTICES.TXT),
when assembling that inventory. This audio-local notice does not grant a
license over Martlet source/assets or authorize publication.
