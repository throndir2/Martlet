# shellcheck shell=bash
# Shared steps for the Linux and macOS packaging scripts (sourced, not run).
# They make the character renderer (VRM and Live2D) required, and collect the license and notices.

# Builds the character renderer from scratch with the locked npm packages; the Live2D SDK download must succeed.
prepare_character() { # repository root
  command -v node >/dev/null || { echo 'Node.js 20.11 or newer is required for the character renderer.' >&2; exit 1; }
  command -v npm >/dev/null || { echo 'npm is required for the character renderer.' >&2; exit 1; }
  rm -rf "$1/src/Martlet.Avatar.RendererHost/web/dist" "$1/src/Martlet.Avatar.RendererHost/web/live2d-dist"
  npm ci --prefix "$1/src/Martlet.Avatar.Vrm" --ignore-scripts --no-audit --no-fund
  export MARTLET_REQUIRE_LIVE2D=1
}

# Stops when the published app has no character renderer or no Live2D runtime.
assert_character() { # publish folder
  local file
  for file in character/app.js character/THIRD-PARTY-NOTICES.txt character/live2d/sdk/core.js \
              character/live2d/LIVE2D-NOTICES.txt; do
    [ -s "$1/$file" ] || { echo "The package has no $file: the character renderer or Live2D did not build." >&2; exit 1; }
  done
}

# Writes Martlet's LICENSE and every third-party notice into a folder (packaging/collect-notices.py).
collect_notices() { # repository root, dotnet command, destination, publish folder...
  local root=$1 dotnet_root folder file
  local args=()
  dotnet_root=$(dirname "$(readlink -f "$(command -v "$2")")")
  for folder in "${@:4}"; do args+=(--publish "$folder"); done
  python3 "$root/packaging/collect-notices.py" --dotnet-root "$dotnet_root" --dest "$3" "${args[@]}"
  for file in LICENSE.txt README.txt THIRD-PARTY-NOTICES.txt; do
    [ -s "$3/$file" ] || { echo "Notice file missing: $3/$file" >&2; exit 1; }
  done
}
