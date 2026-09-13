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

NuGet metadata: [NAudio.Wasapi](https://api.nuget.org/v3-flatcontainer/naudio.wasapi/3.1.0/naudio.wasapi.nuspec),
[NAudio.Core](https://api.nuget.org/v3-flatcontainer/naudio.core/3.1.0/naudio.core.nuspec),
[System.Numerics.Tensors](https://api.nuget.org/v3-flatcontainer/system.numerics.tensors/9.0.0/system.numerics.tensors.nuspec).
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
