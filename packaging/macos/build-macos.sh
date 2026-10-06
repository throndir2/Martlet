#!/usr/bin/env bash
# Packages the Martlet desktop companion for macOS: a self-contained .NET publish in a
# Martlet.app bundle, ad-hoc signed and wrapped in Martlet-<version>-macos-<arch>.dmg.
# The bundle is not Developer ID signed or notarized; first launch needs Open Anyway once.
# Both architectures build on either Mac. On other systems (or with --no-dmg) it stops after
# the bundle and Info.plist check, because codesign and hdiutil exist only on macOS.
#
#   packaging/macos/build-macos.sh --version 0.50.0 --arch arm64 [--arch x64]
#       [--project src/Martlet.Companion/Martlet.Companion.csproj] [--host-project <Mac host .csproj>]
#       [--output artifacts/macos-release] [--dotnet dotnet] [--no-dmg]
set -euo pipefail

here=$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)
root=$(cd "$here/../.." && pwd)
version=''
arches=()
project="$root/src/Martlet.Companion/Martlet.Companion.csproj"
host_project=''
output="$root/artifacts/macos-release"
dotnet=${DOTNET:-dotnet}
dmg=1

while [ $# -gt 0 ]; do
  case "$1" in
    --version) version=$2; shift 2 ;;
    --arch) arches+=("$2"); shift 2 ;;
    --project) project=$2; shift 2 ;;
    --host-project) host_project=$2; shift 2 ;;
    --output) output=$2; shift 2 ;;
    --dotnet) dotnet=$2; shift 2 ;;
    --no-dmg) dmg=0; shift ;;
    *) echo "Unknown argument: $1" >&2; exit 2 ;;
  esac
done
[[ "$version" =~ ^(0|[1-9][0-9]{0,8})\.(0|[1-9][0-9]{0,8})\.(0|[1-9][0-9]{0,8})$ ]] || { echo 'Use --version with a three-part numeric version such as 0.50.0.' >&2; exit 2; }
[ ${#arches[@]} -gt 0 ] || arches=(arm64)
[ -f "$project" ] || { echo "Project not found: $project" >&2; exit 1; }
[ -z "$host_project" ] || [ -f "$host_project" ] || { echo "Host project not found: $host_project" >&2; exit 1; }
command -v python3 >/dev/null || { echo 'python3 is required.' >&2; exit 1; }
if [ "$(uname -s)" != Darwin ] && [ $dmg = 1 ]; then
  echo 'Not macOS: building the app bundles only (codesign and hdiutil need a Mac).' >&2
  dmg=0
fi

assembly_name() { # project
  local name
  name=$(sed -n 's:.*<AssemblyName>\(.*\)</AssemblyName>.*:\1:p' "$1" | head -n 1)
  [ -n "$name" ] && echo "$name" || basename "$1" .csproj
}
exe=$(assembly_name "$project")
mkdir -p "$output"
output=$(cd "$output" && pwd)
node_arg=()
if command -v node >/dev/null; then node_arg=("-p:NodeExecutable=$(command -v node)"); fi
publish() { # project rid destination [extra msbuild arguments]
  "$dotnet" publish "$1" -c Release -r "$2" --self-contained true -p:Version="$version" \
    -p:DebugType=None -p:DebugSymbols=false ${node_arg[@]+"${node_arg[@]}"} -o "$3" -nologo -v:minimal "${@:4}"
}

for arch in "${arches[@]}"; do
  case "$arch" in
    arm64|x64) rid="osx-$arch" ;;
    *) echo "Unsupported --arch $arch (use arm64 or x64)." >&2; exit 2 ;;
  esac
  work="$output/work-$arch"
  rm -rf "$work"
  mkdir -p "$work"
  echo "Publishing $rid..."
  publish "$project" "$rid" "$work/publish"
  [ -f "$work/publish/$exe" ] || { echo "Publish did not produce $exe." >&2; exit 1; }

  app="$work/Martlet.app"
  mkdir -p "$app/Contents/MacOS" "$app/Contents/Resources"
  cp -a "$work/publish/." "$app/Contents/MacOS/"
  chmod +x "$app/Contents/MacOS/$exe"
  # codesign takes dotted folders under Contents/MacOS (Live2D's Hiyori.2048) for nested bundles,
  # so the publish's folders live in Resources and MacOS links to them.
  for dir in "$app/Contents/MacOS"/*/; do
    [ -d "$dir" ] || continue
    name=$(basename "$dir")
    mv "$app/Contents/MacOS/$name" "$app/Contents/Resources/$name"
    ln -s "../Resources/$name" "$app/Contents/MacOS/$name"
  done
  host="$app/Contents/Resources/host"
  if [ -n "$host_project" ]; then
    # The Mac host ships inside the app; users run "<host>/Martlet.Gateway.Host.Linux macos-setup" once.
    publish "$host_project" "$rid" "$host" -p:UseAppHost=true
    host_exe=$(assembly_name "$host_project")
    [ -f "$host/$host_exe" ] || { echo "Host publish did not produce $host_exe." >&2; exit 1; }
    chmod +x "$host/$host_exe"
  fi
  printf 'APPL????' > "$app/Contents/PkgInfo"
  python3 "$here/make-bundle.py" --app "$app" --executable "$exe" --version "$version" \
    --icon "$root/src/Martlet.Desktop/Assets/Martlet.png"
  if command -v plutil >/dev/null; then plutil -lint "$app/Contents/Info.plist"; fi

  if [ $dmg = 1 ]; then
    # Mach-O files under Resources are not nested code for --deep, so the host is signed first.
    if [ -d "$host" ]; then
      find "$host" -type f \( -perm -u+x -o -name '*.dylib' \) -exec codesign --force --sign - {} \;
    fi
    codesign --force --deep --sign - "$app"
    codesign --verify --deep --strict "$app"
    stage="$work/dmg"
    mkdir -p "$stage"
    cp -R "$app" "$stage/"
    ln -s /Applications "$stage/Applications"
    image="$output/Martlet-$version-macos-$arch.dmg"
    rm -f "$image"
    hdiutil create -volname "Martlet $version" -srcfolder "$stage" -fs HFS+ -format UDZO -ov "$image"
    echo "Built $image"
  else
    echo "Built $app (no .dmg)"
  fi
done
