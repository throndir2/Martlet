MARTLET FOR LINUX AND MACOS: LICENSE AND NOTICES

This folder holds the license terms for Martlet and for the third-party
components in this package.

LICENSE.txt grants only narrow personal, noncommercial use of unmodified
official Release binaries. It does not grant source reuse or redistribution.
Third-party rights and notices remain with their original owners.

THIRD-PARTY-NOTICES.txt lists every NuGet package in this package (for example
Avalonia, SkiaSharp, HarfBuzzSharp, SoundFlow and Tmds.DBus), with its
copyright and complete license text. The Microsoft.*.Runtime.* folders hold the
license and the third-party notices of the bundled .NET runtime. Files named
after a Martlet project (Martlet.<project>-...) hold the notices for components
that this project brings.

The character renderer has its own notices in the program's character folder:
character/THIRD-PARTY-NOTICES.txt (the VRM and web components) and
character/live2d/LIVE2D-NOTICES.txt (Live2D).

LIVE2D: The optional desktop character uses Live2D Cubism Core and the Hiyori
sample model by Live2D Inc. This content uses sample data owned and copyrighted
by Live2D Inc. The sample data are utilized in accordance with terms and
conditions set by Live2D Inc. This content itself is created at the author's
sole discretion. By using Martlet you agree not to modify, reverse engineer,
extract or separately redistribute Live2D Cubism Core, consistent with the
Live2D Proprietary Software License Agreement
(https://www.live2d.com/eula/live2d-proprietary-software-license-agreement_en.html).

Where to find this folder:
- .deb package: /usr/share/doc/martlet (the copyright file is LICENSE.txt).
- AppImage: usr/lib/martlet/notices inside the AppImage (run it with
  --appimage-extract to see the files).
- macOS: Martlet.app/Contents/Resources/notices.
