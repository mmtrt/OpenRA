#!/usr/bin/env bash
set -euo pipefail
ROOT="${1:-.}"
OUT="${2:-OpenRA.AndroidLauncher/Assets}"
mkdir -p "$OUT/mods" "$OUT/glsl" "$OUT/assemblies" "$OUT/Content"
for f in VERSION AUTHORS COPYING "global mix database.dat"; do
  [[ -f "$ROOT/$f" ]] && cp -f "$ROOT/$f" "$OUT/"
done
[[ -d "$ROOT/glsl" ]] && cp -a "$ROOT/glsl/." "$OUT/glsl/"
for mod in common common-content ra ra-content cnc d2k; do
  if [[ -d "$ROOT/mods/$mod" ]]; then
    rm -rf "$OUT/mods/$mod"
    cp -a "$ROOT/mods/$mod" "$OUT/mods/"
  fi
done
BIN="${OPENRA_BIN:-$ROOT/bin}"
for dll in OpenRA.Mods.Common OpenRA.Mods.Cnc OpenRA.Mods.D2k \
           Eluant Newtonsoft.Json ICSharpCode.SharpZipLib \
           Linguini.Bundle Linguini.Shared Linguini.Syntax \
           BeaconLib DiscordRPC FuzzyLogicLibrary MP3Sharp Mono.Nat \
           NVorbis Pfim TagLibSharp Microsoft.Extensions.DependencyModel; do
  found=$(find "$BIN" "$ROOT" -name "${dll}.dll" 2>/dev/null | head -1 || true)
  [[ -n "$found" ]] && cp -f "$found" "$OUT/assemblies/"
done
echo "Packaged into $OUT"
find "$OUT/mods" -name 'mod.yaml' | sort
