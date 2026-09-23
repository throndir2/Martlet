# SDK and asset notices

This package contains Martlet adapter code and structural declarations derived
from public API inspection, not a copy of Cubism Framework/Core implementations.
Live2D and Cubism belong to Live2D Inc. No rights in models, textures, motion,
voice or other creator content are granted by this package.

Official SDK development/evaluation is subject to the applicable Live2D Open
Software and Proprietary Software agreements. Cubism Core is not supplied in the
public Framework repository; the user must acquire it through an authorized SDK
distribution and decide whether to accept its terms. This implementation does
not automate acceptance or obtain Core on the user's behalf.

The local development bundle, if built with user-supplied SDK files, contains
licensed code and is ignored by git. Its creation does not authorize publication,
redistribution, a release, or an Expandable Application. Retain all SDK notices
in any authorized distribution and obtain the necessary review/permission first.
Do not assume the Framework's source availability permits redistribution of Core.

Authoritative references inspected 2026-09-23:

- [SDK license overview and release/Expandable Application gates](https://www.live2d.com/en/sdk/license/)
- [Official Framework 5-r.4 revision](https://github.com/Live2D/CubismWebFramework/tree/8df84780f2aa1298f3b30965cdae143e049f3c8e)
- [Framework license](https://github.com/Live2D/CubismWebFramework/blob/5-r.4/LICENSE.md)
- [Official SDK 5-r.4 Core changelog: 05.01.0000](https://github.com/Live2D/CubismWebSamples/blob/5-r.4/Core/CHANGELOG.md)
- [Core acquisition instructions](https://github.com/Live2D/CubismWebSamples/blob/5-r.4/Core/README.md)
- [Model parameter APIs](https://docs.live2d.com/en/cubism-sdk-manual/parameters/)
- [Authored LipSync/EyeBlink groups](https://docs.live2d.com/en/cubism-sdk-manual/lipsync/)
- [Current 5-r.5 shader lifecycle source](https://github.com/Live2D/CubismWebFramework/blob/198a3769c26ca3d7b600e932590433badd392edd/src/rendering/cubismshader_webgl.ts)
- [Upstream public advisories](https://github.com/Live2D/CubismWebFramework/security/advisories)
- [SDK JSON hang report](https://github.com/Live2D/CubismWebFramework/pull/36)
- [5.3 offscreen hierarchy report](https://github.com/Live2D/CubismWebFramework/pull/37)
- [5.3 pooled GPU resource report](https://github.com/Live2D/CubismWebFramework/pull/38)

Pinned development dependencies: TypeScript (Apache-2.0) and esbuild (MIT).
Their licenses remain in the installed packages. This is not a substitute for
legal review of a proposed Martlet release.
