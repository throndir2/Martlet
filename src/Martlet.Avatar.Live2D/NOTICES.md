# SDK and asset notices

This package contains Martlet adapter and animator code. It does not commit any
Cubism Framework, Core or model files. At build time `scripts/sdk.mjs` downloads
the official Cubism SDK for Web 5-r.4 archive from Live2D and verifies its
pinned SHA-256; the renderer build then bundles:

- **Live2D Cubism Core** (`live2dcubismcore.min.js`, Core 05.01.0000), listed in
  the SDK's `Core/RedistributableFiles.txt`, redistributed unmodified inside
  Martlet under the Live2D Proprietary Software License Agreement. End users
  may not modify, reverse engineer, extract or separately redistribute it.
- **Cubism Web Framework**, compiled into `sdk.js` under the Live2D Open
  Software License.
- **Hiyori Momose** sample model (Live2D Original Character), unmodified, under
  the Free Material License Agreement and Terms of Use for Live2D Cubism Sample
  Data. Required notice: *This content uses sample data owned and copyrighted
  by Live2D Inc. The sample data are utilized in accordance with terms and
  conditions set by Live2D Inc. This content itself is created at the author's
  sole discretion.*

Live2D and Cubism are trademarks of Live2D Inc. No rights in user-supplied
models, textures, motions or voices are granted by this package.

Publication: individuals and small-scale enterprises (annual sales under
10 million JPY) are exempt from the Publication License Agreement for ordinary
content, but an app that loads an indefinite number of user models is an
Expandable Application, which requires Live2D's prior review and a special
Publication License Agreement for every publisher. The project owner has applied.

Authoritative references (inspected 2026-09-23 and 2026-09-28):

- [SDK license overview and release/Expandable Application gates](https://www.live2d.com/en/sdk/license/)
- [Expandable Applications](https://www.live2d.com/en/sdk/license/expandable/)
- [Live2D Proprietary Software License Agreement](https://www.live2d.com/eula/live2d-proprietary-software-license-agreement_en.html)
- [Live2D Open Software License Agreement](https://www.live2d.com/eula/live2d-open-software-license-agreement_en.html)
- [Free Material License Agreement](https://www.live2d.com/eula/live2d-free-material-license-agreement_en.html)
- [Terms of Use for Live2D Cubism Sample Data](https://www.live2d.com/eula/live2d-sample-model-terms_en.html)
- [Official Framework 5-r.4 revision](https://github.com/Live2D/CubismWebFramework/tree/8df84780f2aa1298f3b30965cdae143e049f3c8e)
- [Official SDK 5-r.4 Core changelog: 05.01.0000](https://github.com/Live2D/CubismWebSamples/blob/5-r.4/Core/CHANGELOG.md)
- [Upstream public advisories](https://github.com/Live2D/CubismWebFramework/security/advisories)
- [SDK JSON hang report](https://github.com/Live2D/CubismWebFramework/pull/36)

Pinned development dependencies: TypeScript (Apache-2.0) and esbuild (MIT).
Their licenses remain in the installed packages. This is not legal advice.
