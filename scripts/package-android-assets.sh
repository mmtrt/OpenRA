#!/usr/bin/env bash
# Package full mod trees + managed assemblies for OpenRA.AndroidLauncher/Assets
# Must use the SAME source tree that built the mod DLLs (fixes ruleset YAML/DLL mismatch).
set -euo pipefail

ROOT="$(cd "${1:-.}" && pwd)"
OUT="${2:-$ROOT/OpenRA.AndroidLauncher/Assets}"
CONFIG="${3:-Release}"
OPENRA_MOD="${4:-${OPENRA_MOD:-ra}}"
# Match desktop install_assemblies flags (AppImage always ships Cnc; D2k only for d2k)
COPY_CNC_DLL=True
COPY_D2K_DLL=False
case "$OPENRA_MOD" in
  d2k) COPY_D2K_DLL=True ;;
esac
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

# --- managed assemblies ---
# Default: COMPILE_IN_ASSEMBLIES=1 — OpenRA.AndroidLauncher ProjectReferences
# OpenRA.Mods.Common/Cnc/(D2k) so DLLs ship inside the APK managed payload.
# Assets/assemblies is NOT required at runtime (Assembly.Load + resolve hook).
# Set COMPILE_IN_ASSEMBLIES=0 to also stage DLLs under Assets for disk-load fallback.
SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
ASM_OUT="$OUT/assemblies"
mkdir -p "$ASM_OUT"

COMPILE_IN_ASSEMBLIES="${COMPILE_IN_ASSEMBLIES:-1}"

if [[ "$COMPILE_IN_ASSEMBLIES" == "1" ]]; then
  echo "COMPILE_IN_ASSEMBLIES=1 — skip packaging DLLs into Assets/assemblies"
  echo "  (launcher ProjectReference builds Common+Cnc${COPY_D2K_DLL:+; D2k when OpenRAMod=d2k})"
  # Remove any stale staged DLLs so we never ship a 38KB stub from an old run
  rm -f "$ASM_OUT"/OpenRA.Mods.*.dll 2>/dev/null || true
  # Keep directory so extract paths do not throw; optional marker
  echo "compile-in" > "$ASM_OUT/.compile_in"
else
  echo "COMPILE_IN_ASSEMBLIES=0 — stage mod DLLs into Assets (legacy disk load)"
  if [[ "${SKIP_ASSEMBLY_BUILD:-}" == "1" ]]; then
    echo "SKIP_ASSEMBLY_BUILD=1 — copy from bin-android/bin only"
    for search in "$ROOT/bin-android" "$ROOT/bin"; do
      [[ -d "$search" ]] || continue
      find "$search" -maxdepth 2 -type f -name 'OpenRA.Mods.*.dll' 2>/dev/null | while read -r f; do
        base=$(basename "$f")
        if [[ "$base" == "OpenRA.Mods.D2k.dll" && "$COPY_D2K_DLL" != "True" ]]; then
          continue
        fi
        cp -f "$f" "$ASM_OUT/"
        echo "  assembly: $base ($(stat -c%s "$f" 2>/dev/null || stat -f%z "$f") bytes)"
      done
      find "$search" -maxdepth 2 -type f \( \
        -name 'Eluant.dll' -o -name 'Newtonsoft.Json.dll' -o -name 'Linguini.*.dll' \
        -o -name 'MP3Sharp.dll' -o -name 'NVorbis.dll' -o -name 'Pfim.dll' \
        -o -name 'TagLibSharp.dll' -o -name 'BeaconLib.dll' -o -name 'Mono.Nat.dll' \
        -o -name 'FuzzyLogicLibrary.dll' -o -name 'ICSharpCode.SharpZipLib.dll' \
        -o -name 'Microsoft.Extensions.DependencyModel.dll' \
      \) 2>/dev/null | while read -r f; do
        cp -f "$f" "$ASM_OUT/"
        echo "  dep: $(basename "$f")"
      done
    done
  else
    INSTALL="$SCRIPT_DIR/install-assemblies-android.sh"
    [[ -f "$INSTALL" ]] || INSTALL="$ROOT/scripts/install-assemblies-android.sh"
    if [[ ! -f "$INSTALL" ]]; then
      echo "ERROR: install-assemblies-android.sh not found (required when COMPILE_IN_ASSEMBLIES=0)"
      exit 1
    fi
    bash "$INSTALL" "$ROOT" "$ASM_OUT" "$CONFIG" "$COPY_CNC_DLL" "$COPY_D2K_DLL"
  fi
  min_size_ok() {
    local f="$1" min="$2"
    [[ -f "$f" ]] || return 1
    local sz; sz=$(stat -c%s "$f" 2>/dev/null || stat -f%z "$f")
    [[ "$sz" -ge "$min" ]]
  }
  if ! min_size_ok "$ASM_OUT/OpenRA.Mods.Common.dll" 100000; then
    echo "ERROR: OpenRA.Mods.Common.dll missing or too small under Assets"
    ls -la "$ASM_OUT" || true
    exit 1
  fi
  if [[ "$COPY_CNC_DLL" == "True" ]] && ! min_size_ok "$ASM_OUT/OpenRA.Mods.Cnc.dll" 50000; then
    echo "ERROR: OpenRA.Mods.Cnc.dll missing or too small"
    exit 1
  fi
  if [[ "$COPY_D2K_DLL" == "True" ]] && ! min_size_ok "$ASM_OUT/OpenRA.Mods.D2k.dll" 50000; then
    echo "ERROR: OpenRA.Mods.D2k.dll missing or too small"
    exit 1
  fi
  if [[ "$COPY_D2K_DLL" != "True" ]]; then
    rm -f "$ASM_OUT/OpenRA.Mods.D2k.dll"
  fi
fi

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
if [[ "${COMPILE_IN_ASSEMBLIES:-1}" == "1" ]]; then
  echo "  OK compile-in mode — Assets/assemblies DLLs not required"
else
  require_file "$OUT/assemblies/OpenRA.Mods.Common.dll"
  require_file "$OUT/assemblies/OpenRA.Mods.Cnc.dll"
  if [[ "$OPENRA_MOD" == "d2k" ]]; then
    require_file "$OUT/assemblies/OpenRA.Mods.D2k.dll"
  fi
fi

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
