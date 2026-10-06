#!/usr/bin/env bash
# Packages the Martlet desktop companion for Linux: a self-contained .NET publish as
# Martlet-<version>-linux-<arch>.AppImage and martlet_<version>_<debarch>.deb.
# Runs on any x86_64 or aarch64 Linux with bash, curl, sha256sum, file and dpkg-deb; both
# architectures can be built on either (the AppImage runtime is chosen per target).
#
#   packaging/linux/build-linux.sh --version 0.50.0 --arch x64 [--arch arm64]
#       [--project src/Martlet.Companion/Martlet.Companion.csproj] [--output artifacts/linux-release] [--dotnet dotnet]
set -euo pipefail

here=$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)
root=$(cd "$here/../.." && pwd)
version=''
arches=()
project="$root/src/Martlet.Companion/Martlet.Companion.csproj"
output="$root/artifacts/linux-release"
dotnet=${DOTNET:-dotnet}

while [ $# -gt 0 ]; do
  case "$1" in
    --version) version=$2; shift 2 ;;
    --arch) arches+=("$2"); shift 2 ;;
    --project) project=$2; shift 2 ;;
    --output) output=$2; shift 2 ;;
    --dotnet) dotnet=$2; shift 2 ;;
    *) echo "Unknown argument: $1" >&2; exit 2 ;;
  esac
done
[[ "$version" =~ ^(0|[1-9][0-9]{0,8})\.(0|[1-9][0-9]{0,8})\.(0|[1-9][0-9]{0,8})$ ]] || { echo 'Use --version with a three-part numeric version such as 0.50.0.' >&2; exit 2; }
[ ${#arches[@]} -gt 0 ] || arches=(x64)
[ -f "$project" ] || { echo "Project not found: $project" >&2; exit 1; }
for tool in curl sha256sum dpkg-deb file; do command -v "$tool" >/dev/null || { echo "$tool is required." >&2; exit 1; }; done

# The apphost is named after <AssemblyName>, or the project file name without one.
exe=$(sed -n 's:.*<AssemblyName>\(.*\)</AssemblyName>.*:\1:p' "$project" | head -n 1)
[ -n "$exe" ] || exe=$(basename "$project" .csproj)
icon="$root/src/Martlet.Desktop/Assets/Martlet.png"
# The desktop portals (GlobalShortcuts, ScreenCast) identify Martlet by this app id.
appid=io.github.throndir2.Martlet
mkdir -p "$output"
output=$(cd "$output" && pwd)
tools="$output/tools"
mkdir -p "$tools"

# Pinned AppImage tooling (AppImage/appimagetool 1.9.1, AppImage/type2-runtime 20251108), checked by SHA-256.
fetch() { # url sha256 destination
  if [ ! -f "$3" ] || ! echo "$2  $3" | sha256sum -c --status; then
    curl -fsSL --retry 3 -o "$3.tmp" "$1"
    echo "$2  $3.tmp" | sha256sum -c --status || { echo "SHA-256 mismatch for $1" >&2; rm -f "$3.tmp"; exit 1; }
    mv "$3.tmp" "$3"
  fi
  chmod +x "$3"
}
case "$(uname -m)" in
  x86_64) fetch https://github.com/AppImage/appimagetool/releases/download/1.9.1/appimagetool-x86_64.AppImage \
            ed4ce84f0d9caff66f50bcca6ff6f35aae54ce8135408b3fa33abfc3cb384eb0 "$tools/appimagetool" ;;
  aarch64) fetch https://github.com/AppImage/appimagetool/releases/download/1.9.1/appimagetool-aarch64.AppImage \
            f0837e7448a0c1e4e650a93bb3e85802546e60654ef287576f46c71c126a9158 "$tools/appimagetool" ;;
  *) echo "Unsupported build machine: $(uname -m)" >&2; exit 1 ;;
esac

node_arg=()
if command -v node >/dev/null; then node_arg=("-p:NodeExecutable=$(command -v node)"); fi

for arch in "${arches[@]}"; do
  case "$arch" in
    x64) rid=linux-x64; debarch=amd64; aiarch=x86_64
         runtime_sha=2fca8b443c92510f1483a883f60061ad09b46b978b2631c807cd873a47ec260d ;;
    arm64) rid=linux-arm64; debarch=arm64; aiarch=aarch64
           runtime_sha=00cbdfcf917cc6c0ff6d3347d59e0ca1f7f45a6df1a428a0d6d8a78664d87444 ;;
    *) echo "Unsupported --arch $arch (use x64 or arm64)." >&2; exit 2 ;;
  esac
  fetch "https://github.com/AppImage/type2-runtime/releases/download/20251108/runtime-$aiarch" "$runtime_sha" "$tools/runtime-$aiarch"

  work="$output/work-$arch"
  rm -rf "$work"
  mkdir -p "$work"
  echo "Publishing $rid..."
  "$dotnet" publish "$project" -c Release -r "$rid" --self-contained true -p:Version="$version" \
    -p:DebugType=None -p:DebugSymbols=false ${node_arg[@]+"${node_arg[@]}"} -o "$work/publish" -nologo -v:minimal
  [ -f "$work/publish/$exe" ] || { echo "Publish did not produce $exe." >&2; exit 1; }
  chmod +x "$work/publish/$exe"

  # AppImage: the publish output under usr/lib/martlet, started by AppRun.
  appdir="$work/AppDir"
  mkdir -p "$appdir/usr/lib/martlet"
  cp -a "$work/publish/." "$appdir/usr/lib/martlet/"
  printf '#!/bin/sh\nhere="$(dirname "$(readlink -f "$0")")"\nexec "$here/usr/lib/martlet/%s" "$@"\n' "$exe" > "$appdir/AppRun"
  chmod +x "$appdir/AppRun"
  install -m 0644 "$here/$appid.desktop" "$appdir/$appid.desktop"
  install -m 0644 "$icon" "$appdir/$appid.png"
  ln -s "$appid.png" "$appdir/.DirIcon"
  appimage="$output/Martlet-$version-linux-$arch.AppImage"
  rm -f "$appimage"
  ARCH="$aiarch" APPIMAGE_EXTRACT_AND_RUN=1 "$tools/appimagetool" --no-appstream \
    --runtime-file "$tools/runtime-$aiarch" "$appdir" "$appimage"

  # .deb: /opt/martlet with a martlet command, menu entry and icon.
  pkg="$work/deb"
  mkdir -p "$pkg/DEBIAN" "$pkg/opt/martlet" "$pkg/usr/bin" "$pkg/usr/share/applications" \
    "$pkg/usr/share/icons/hicolor/256x256/apps"
  cp -a "$work/publish/." "$pkg/opt/martlet/"
  ln -s "/opt/martlet/$exe" "$pkg/usr/bin/martlet"
  install -m 0644 "$here/$appid.desktop" "$pkg/usr/share/applications/$appid.desktop"
  install -m 0644 "$icon" "$pkg/usr/share/icons/hicolor/256x256/apps/$appid.png"
  find "$pkg" -type d -exec chmod 0755 {} +
  cat > "$pkg/DEBIAN/control" <<EOF
Package: martlet
Version: $version
Architecture: $debarch
Maintainer: Martlet <throndir2@users.noreply.github.com>
Installed-Size: $(du -sk --exclude=DEBIAN "$pkg" | cut -f1)
Depends: libc6, libgcc-s1, libstdc++6, zlib1g, libssl3t64 | libssl3, libicu76 | libicu74 | libicu72 | libicu70, libfontconfig1, libx11-6, libxext6, libice6, libsm6, libwebkit2gtk-4.1-0, libsecret-1-0
Recommends: libpipewire-0.3-0
Section: utils
Priority: optional
Homepage: https://github.com/throndir2/Martlet
Description: Martlet desktop companion
 Talk with your Martlet companion and see the character on your screen.
EOF
  deb="$output/martlet_${version}_${debarch}.deb"
  rm -f "$deb"
  dpkg-deb --root-owner-group -Zxz --build "$pkg" "$deb"
  echo "Built $appimage"
  echo "Built $deb"
done
