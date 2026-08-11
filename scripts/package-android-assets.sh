#!/usr/bin/env bash
# Package full mod trees + managed assemblies for OpenRA.AndroidLauncher/Assets
# Must use the SAME source tree that built the mod DLLs (fixes ruleset YAML/DLL mismatch).
set -euo pipefail

ROOT="$(cd "${1:-.}" && pwd)"
OUT="${2:-$ROOT/OpenRA.AndroidLauncher/Assets}"
CONFIG="${3:-Release}"
OPENRA_MOD="${4:-${OPENRA_MOD:-ra}}"
OPENRA_BIN="${OPENRA_BIN:-}"

cd "$ROOT"
mkdir -p "$OUT/mods" "$OUT/glsl" "$OUT/assemblies" "$OUT/Content"

echo "=== Package Android assets ==="
echo "ROOT=$ROOT"
echo "OUT=$OUT"
echo "CONFIG=$CONFIG"
echo "OPENRA_MOD=$OPENRA_MOD"

# --- engine root files (official install_data list) ---
for f in VERSION AUTHORS COPYING "global mix database.dat"; do
  if [[ -f "$ROOT/$f" ]]; then
    cp -f "$ROOT/$f" "$OUT/"
    echo "  root: $f"
  fi
done
# Ensure VERSION exists
if [[ ! -f "$OUT/VERSION" ]]; then
  echo "android-port-dev" > "$OUT/VERSION"
fi

# --- glsl ---
if [[ -d "$ROOT/glsl" ]]; then
  rm -rf "$OUT/glsl"
  mkdir -p "$OUT/glsl"
  cp -a "$ROOT/glsl/." "$OUT/glsl/"
  echo "  glsl: $(find "$OUT/glsl" -type f | wc -l) files"
else
  echo "WARNING: glsl/ missing"
fi

# --- full mod trees (rules YAML must match built assemblies) ---
MISSING_MOD=0
# Shared packages always; primary mod + optional *-content companion.
MOD_LIST="common common-content ${OPENRA_MOD}"
case "$OPENRA_MOD" in
  ra|cnc|d2k|ts) MOD_LIST="$MOD_LIST ${OPENRA_MOD}-content" ;;
esac
for mod in $MOD_LIST; do
  if [[ -d "$ROOT/mods/$mod" ]]; then
    rm -rf "$OUT/mods/$mod"
    cp -a "$ROOT/mods/$mod" "$OUT/mods/"
    # Count critical YAML
    rules=$(find "$OUT/mods/$mod" -path '*/rules/*.yaml' 2>/dev/null | wc -l || echo 0)
    echo "  mod $mod: rules yaml=$rules"
  else
    echo "WARNING: mods/$mod not found"
    # ra + common are required for Red Alert
    if [[ "$mod" == "$OPENRA_MOD" ]]; then
      MISSING_MOD=1
    fi
    if [[ "$mod" == "common" ]]; then
      echo "ERROR: mods/common is required (shared packages/fonts)"
      MISSING_MOD=1
    fi
  fi
done

if [[ "$MISSING_MOD" -ne 0 ]]; then
  echo "ERROR: required mods missing (${OPENRA_MOD} and/or common)"
  exit 1
fi

# --- managed assemblies from mod build outputs ---
copy_dll() {
  local f="$1"
  [[ -f "$f" ]] || return 0
  local base
  base=$(basename "$f")
  case "$base" in
    OpenRA.Game.dll|OpenRA.Platforms.*.dll|System.*.dll|Microsoft.*.dll|netstandard.dll|mscorlib.dll) return 0 ;;
  esac
  # Skip pure runtime facades
  case "$base" in
    WindowsBase.dll|Presentation*.dll) return 0 ;;
  esac
  cp -f "$f" "$OUT/assemblies/"
  echo "  assembly: $base"
}

find_mod_out() {
  local proj="$1"
  local candidate
  for candidate in \
    "$ROOT/bin-android" \
    "$proj/bin-android" \
    "$proj/bin/$CONFIG/net10.0" \
    "$proj/bin/$CONFIG" \
    "bin/net10.0" \
    "bin" \
    ${OPENRA_BIN:+"$OPENRA_BIN"}
  do
    if [[ -d "$candidate" ]] && ls "$candidate"/*.dll >/dev/null 2>&1; then
      # Prefer dir that actually contains the project dll
      if ls "$candidate"/OpenRA.Mods.*.dll >/dev/null 2>&1 || ls "$candidate"/"$proj".dll >/dev/null 2>&1; then
        echo "$candidate"
        return 0
      fi
    fi
  done
  # Fallback: search bin-android specifically. Do NOT fall back to a bare "any dir with
  # Mods.Common.dll" scan across $ROOT — that can silently pick up a desktop (net10.0)
  # build sitting in bin/, which looks fine but is missing the #if ANDROID guards and
  # will reproduce Android-specific crashes (e.g. DiscordService TypeLoadException) even
  # though the source is patched correctly.
  local found
  found=$(find "$ROOT/bin-android" -maxdepth 2 -name 'OpenRA.Mods.Common.dll' -print 2>/dev/null | head -1 || true)
  if [[ -n "$found" ]]; then
    dirname "$found"
    return 0
  fi
  return 1
}

COMMON_OUT=""
if COMMON_OUT=$(find_mod_out OpenRA.Mods.Common); then
  echo "Using assembly source: $COMMON_OUT"
  case "$COMMON_OUT" in
    "$ROOT/bin-android"*) ;;
    *) echo "WARNING: assembly source is NOT bin-android/ ($COMMON_OUT)." \
            "This is almost certainly a desktop (net10.0) build lacking the" \
            "#if ANDROID guards — build with 'dotnet build -p:OpenRAAndroid=true'" \
            "first so bin-android/ exists." ;;
  esac
  while IFS= read -r f; do
    copy_dll "$f"
  done < <(find "$COMMON_OUT" -maxdepth 1 -name '*.dll' -type f | sort)
else
  echo "WARNING: could not locate Mods.Common output"
fi

for proj in OpenRA.Mods.Cnc; do
  if out=$(find_mod_out "$proj"); then
    while IFS= read -r f; do
      case "$(basename "$f")" in
        OpenRA.Mods.*.dll) copy_dll "$f" ;;
      esac
    done < <(find "$out" -maxdepth 1 -name 'OpenRA.Mods.*.dll' -type f)
  fi
done

# Explicit dep fill (AppImage checklist)
for dep in \
  TagLibSharp.dll MP3Sharp.dll NVorbis.dll Pfim.dll \
  BeaconLib.dll rix0rrr.BeaconLib.dll FuzzyLogicLibrary.dll \
  ICSharpCode.SharpZipLib.dll Mono.Nat.dll Newtonsoft.Json.dll \
  Linguini.Bundle.dll Linguini.Shared.dll Linguini.Syntax.dll \
  Microsoft.Extensions.DependencyModel.dll Eluant.dll \
  OpenRA.Mods.Common.dll OpenRA.Mods.Cnc.dll
do
  if [[ ! -f "$OUT/assemblies/$dep" ]]; then
    found=$(find "$ROOT" -path '*/OpenRA.AndroidLauncher/*' -prune -o -name "$dep" -print 2>/dev/null | head -1 || true)
    if [[ -n "$found" ]]; then
      cp -f "$found" "$OUT/assemblies/"
      echo "  dep fill: $dep"
    fi
  fi
done

echo ""
echo "=== Ruleset integrity checks (Chronoshiftable / Mobile) ==="
FAIL=0

require_file() {
  local f="$1"
  if [[ -f "$f" ]]; then
    echo "  OK $f"
  else
    echo "  MISSING $f"
    FAIL=1
  fi
}

# ra is a full mod; common/common-content are shared packages (no mod.yaml upstream)
require_file "$OUT/mods/${OPENRA_MOD}/mod.yaml"
if [[ "$OPENRA_MOD" == "ra" ]]; then
  require_file "$OUT/mods/ra/rules/vehicles.yaml"
  require_file "$OUT/mods/ra/rules/defaults.yaml"
fi
require_file "$OUT/assemblies/OpenRA.Mods.Common.dll"
require_file "$OUT/assemblies/OpenRA.Mods.Cnc.dll"

# Shared package dirs (OpenRA bleed: common has fonts/chrome/scripts — no mod.yaml)
if [[ ! -d "$OUT/mods/common" ]]; then
  echo "  MISSING mods/common/ directory"
  FAIL=1
else
  # Need at least fonts used by SpriteFont
  if [[ -f "$OUT/mods/common/FreeSans.ttf" ]] || [[ -f "$OUT/mods/common/FreeSansBold.ttf" ]]; then
    echo "  OK mods/common/ (shared package + fonts)"
  else
    echo "  WARNING: mods/common/ missing FreeSans fonts"
  fi
fi

if [[ ! -d "$OUT/mods/common-content" ]]; then
  echo "  WARNING: mods/common-content/ missing (content installer chrome)"
else
  echo "  OK mods/common-content/"
fi

if [[ -f "$OUT/mods/ra-content/mod.yaml" ]]; then
  echo "  OK mods/ra-content/mod.yaml"
else
  echo "  WARNING: mods/ra-content/mod.yaml missing"
fi

# V2RL must define Mobile in the packaged YAML (upstream does)
if [[ "$OPENRA_MOD" == "ra" ]]; then
if [[ -f "$OUT/mods/ra/rules/vehicles.yaml" ]]; then
  if grep -q '^V2RL:' "$OUT/mods/ra/rules/vehicles.yaml" || grep -q $'\tV2RL:' "$OUT/mods/ra/rules/vehicles.yaml" || grep -q 'V2RL:' "$OUT/mods/ra/rules/vehicles.yaml"; then
    # Extract a window around V2RL and require Mobile:
    if awk '/^V2RL:|^[A-Za-z0-9]+:/{if(/^V2RL:/){p=1;next} if(p&&/^[A-Za-z0-9]+:/){exit}} p' "$OUT/mods/ra/rules/vehicles.yaml" \
      | grep -q 'Mobile:'; then
      echo "  OK V2RL has Mobile: in vehicles.yaml"
    else
      # Fallback: nearby Mobile after V2RL within 40 lines
      if grep -A40 '^V2RL:' "$OUT/mods/ra/rules/vehicles.yaml" | head -40 | grep -q 'Mobile:'; then
        echo "  OK V2RL block contains Mobile:"
      else
        echo "  ERROR: V2RL in vehicles.yaml lacks Mobile: (ruleset will fail Chronoshiftable check)"
        FAIL=1
      fi
    fi
  else
    echo "  ERROR: V2RL actor missing from vehicles.yaml"
    FAIL=1
  fi
fi

# Defaults must define ^Vehicle with Mobile (inheritance chain)
if [[ -f "$OUT/mods/ra/rules/defaults.yaml" ]]; then
  if grep -q '\^Vehicle' "$OUT/mods/ra/rules/defaults.yaml" && grep -q 'Mobile:' "$OUT/mods/ra/rules/defaults.yaml"; then
    echo "  OK defaults.yaml has ^Vehicle / Mobile"
  else
    echo "  WARNING: defaults.yaml may be incomplete (^Vehicle/Mobile)"
  fi
fi
fi  # OPENRA_MOD == ra

# Assemblies: Common must be non-trivial size
if [[ -f "$OUT/assemblies/OpenRA.Mods.Common.dll" ]]; then
  sz=$(stat -c%s "$OUT/assemblies/OpenRA.Mods.Common.dll" 2>/dev/null || stat -f%z "$OUT/assemblies/OpenRA.Mods.Common.dll")
  if [[ "$sz" -lt 100000 ]]; then
    echo "  ERROR: OpenRA.Mods.Common.dll too small ($sz bytes) — likely stub"
    FAIL=1
  else
    echo "  OK OpenRA.Mods.Common.dll size=$sz"
  fi
fi

echo ""
echo "=== Summary ==="
du -sh "$OUT"/* 2>/dev/null || true
echo "mod.yaml files:"
find "$OUT/mods" -name mod.yaml | sort
echo "assemblies:"
ls -la "$OUT/assemblies" | head -40
echo "glsl files: $(find "$OUT/glsl" -type f 2>/dev/null | wc -l)"

if [[ "$FAIL" -ne 0 ]]; then
  echo "ERROR: package-android-assets integrity checks failed"
  exit 1
fi

echo "Package complete."