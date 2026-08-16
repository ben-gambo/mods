#!/bin/bash
# Builds a mod and stages the result into <Mod>/release/ - the folder that gets
# committed and that CI zips into a GitHub release.
#
#   ./build.sh                  build every mod
#   ./build.sh ImpatientGambit  build one
#   ./build.sh --install        also copy into your game's Mods/ folder
#
# CI cannot do this step: mods compile against the game's own assemblies, which
# are copyrighted and are in nobody's repository. So the build happens here, on
# a machine with the game installed, and the output is committed. See README.md.

set -euo pipefail

ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"

INSTALL=0
MODS=()
for arg in "$@"; do
    case "$arg" in
        --install) INSTALL=1 ;;
        -*) echo "unknown option: $arg" >&2; exit 2 ;;
        *) MODS+=("$arg") ;;
    esac
done

if [ "${#MODS[@]}" -eq 0 ]; then
    for dir in "$ROOT"/*/; do
        [ -f "$dir/mod.json" ] && MODS+=("$(basename "$dir")")
    done
fi
[ "${#MODS[@]}" -gt 0 ] || { echo "no mods found in $ROOT" >&2; exit 1; }

find_game_mods_dir() {
    local candidates=(
        "$HOME/Library/Application Support/Steam/steamapps/common/Gambonanza"
        "$HOME/.local/share/Steam/steamapps/common/Gambonanza"
        "$HOME/.steam/steam/steamapps/common/Gambonanza"
        "/c/Program Files (x86)/Steam/steamapps/common/Gambonanza"
    )
    [ -n "${GAMBONANZA_DIR:-}" ] && candidates=("$GAMBONANZA_DIR" "${candidates[@]}")
    for c in "${candidates[@]}"; do
        [ -d "$c/Mods" ] && { printf '%s\n' "$c/Mods"; return; }
    done
    echo "could not find your Gambonanza install - set GAMBONANZA_DIR" >&2
    return 1
}

[ "$INSTALL" -eq 1 ] && LIVE="$(find_game_mods_dir)"

for mod in "${MODS[@]}"; do
    src="$ROOT/$mod"
    [ -f "$src/mod.json" ] || { echo "no mod.json in $src" >&2; exit 1; }
    csproj="$src/$mod.csproj"
    [ -f "$csproj" ] || { echo "no $mod.csproj in $src" >&2; exit 1; }

    asm="$(sed -n 's:.*<AssemblyName>\(.*\)</AssemblyName>.*:\1:p' "$csproj" | head -n 1)"
    [ -n "$asm" ] || asm="$mod"

    echo "==> Building $mod"
    dotnet build "$csproj" -c Release --nologo -v minimal

    out="$src/release"
    rm -rf "$out" && mkdir -p "$out"
    cp "$src/bin/Release/$asm.dll" "$out/"
    cp "$src/mod.json" "$out/"
    # Any loose asset beside mod.json (art, data) ships with the mod.
    find "$src" -maxdepth 1 -type f \
        ! -name 'mod.json' ! -name '*.csproj' ! -name '*.md' ! -name 'LICENSE' \
        -exec cp {} "$out/" \;

    version="$(sed -n 's/.*"version"[[:space:]]*:[[:space:]]*"\([^"]*\)".*/\1/p' "$src/mod.json" | head -n 1)"
    echo "    staged $mod $version -> $out"

    if [ "$INSTALL" -eq 1 ]; then
        rm -rf "${LIVE:?}/$mod" && mkdir -p "$LIVE/$mod"
        cp -R "$out/." "$LIVE/$mod/"
        echo "    installed -> $LIVE/$mod"
    fi
done

echo
echo "Commit the release/ folders. Pushing a mod.json version that has no"
echo "matching release yet is what makes CI publish one."
