# Roadmap

**Status:** Partial
**Last Updated:** 2026-11-13
**Audience:** Users
**Last verified:** 2026-11-13 against b1688a8de
**Code:** `src/Martlet.Core/Platforms/PlatformCatalog.cs`, `src/Martlet.Companion`, `src/Martlet.Desktop`, `packaging`

The platform catalog describes what works, what is planned, and what is not planned or impossible. A catalog entry does not prove a feature was tested on your hardware.

## Built and partial

- The Windows desktop app and a Linux and macOS companion have source projects. The companion is an early app with a smaller feature set.
- The release scripts package the Windows app and the Linux and macOS companion. See [Packaging](Packaging).
- The platform catalog distinguishes supported engines from planned engines. Missing device information can produce an unknown result rather than a verified yes.

See [Linux and Mac](Linux-and-Mac) and [Platforms](https://github.com/throndir2/Martlet/blob/main/docs/PLATFORMS.md) for current platform detail and limits.

## Planned

The catalog marks Windows speech and local whisper conversation support as planned (PL02). It also marks local whisper and hands-free listening on Linux and macOS as planned (DX01).

The catalog marks iOS and Android companion capabilities as planned. Apple Intelligence and Apple voice capabilities are also planned for applicable Apple devices. These entries are plans, not proof that the apps or engines work today.

The platform and research plans have more detail: [Platforms](https://github.com/throndir2/Martlet/blob/main/docs/PLATFORMS.md), [iOS](https://github.com/throndir2/Martlet/blob/main/docs/IOS.md), [Android](https://github.com/throndir2/Martlet/blob/main/docs/ANDROID.md), and [Deep research](https://github.com/throndir2/Martlet/blob/main/docs/DEEP_RESEARCH.md).
