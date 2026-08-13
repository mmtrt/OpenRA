#!/usr/bin/env bash
# Android counterpart to packaging/functions.sh install_assemblies().
#
# Desktop:
#   dotnet publish -p:CopyCncDll=… -p:CopyD2kDll=… -r <rid> --self-contained
#
# Android cannot use that publish (self-contained desktop RID). Instead:
#   1. Build OpenRA.Mods.* with OpenRAAndroid=true → bin-android/
#   2. Copy required mod DLLs + deps into DEST (APK Assets/assemblies/)
#
# Usage:
#   install-assemblies-android.sh SRC_PATH DEST_PATH CONFIG COPY_CNC_DLL COPY_D2K_DLL
#     COPY_* = True|False (same meaning as desktop install_assemblies)
#
# Example (Dune APK):
#   ./scripts/install-assemblies-android.sh . OpenRA.AndroidLauncher/Assets/assemblies Release True True
#
set -euo pipefail

SRC_PATH="${1:?SRC_PATH}"
DEST_PATH="${2:?DEST_PATH}"
CONFIG="${3:-Release}"
COPY_CNC_DLL="${4:-True}"
COPY_D2K_DLL="${5:-False}"

cd "$SRC_PATH"
SRC_PATH="$(pwd)"

mkdir -p "$DEST_PATH"
DEST_PATH="$(cd "$DEST_PATH" && pwd)"

echo "=== install-assemblies-android ==="
echo "  SRC=$SRC_PATH"
echo "  DEST=$DEST_PATH"
echo "  CONFIG=$CONFIG CopyCnc=$COPY_CNC_DLL CopyD2k=$COPY_D2K_DLL"

# Minimum sizes (bytes) — reject stubs like the 38KB D2k DLL
MIN_COMMON=100000
MIN_CNC=50000
MIN_D2K=50000

build_mod() {
  local proj="$1"
  if [[ ! -f "$SRC_PATH/$proj/$proj.csproj" ]]; then
    echo "ERROR: missing $proj/$proj.csproj"
    return 1
  fi
  if [[ -d "$SRC_PATH/$proj/obj" ]]; then
    # Avoid stale desktop TFM assets.json
    rm -rf "$SRC_PATH/$proj/obj"
  fi
  echo "--- restore $proj (net10.0-android) ---"
  dotnet restore "$SRC_PATH/$proj/$proj.csproj" \
    /p:OpenRAAndroid=true \
    --force \
    --verbosity minimal
  echo "--- build $proj ---"
  dotnet build "$SRC_PATH/$proj/$proj.csproj" \
    -c "$CONFIG" \
    /p:OpenRAAndroid=true \
    /p:CopyLocalLockFileAssemblies=true \
    --no-restore \
    --verbosity minimal
}

# Always build Common; Cnc/D2k per flags (Common is required by both).
build_mod OpenRA.Mods.Common
if [[ "$COPY_CNC_DLL" == "True" || "$COPY_CNC_DLL" == "true" ]]; then
  build_mod OpenRA.Mods.Cnc
fi
if [[ "$COPY_D2K_DLL" == "True" || "$COPY_D2K_DLL" == "true" ]]; then
  build_mod OpenRA.Mods.D2k
fi

# Locate a built DLL by name under bin-android (preferred) then bin/
find_dll() {
  local name="$1"
  local f
  for f in \
    "$SRC_PATH/bin-android/$name" \
    "$SRC_PATH/bin/$name" \
    "$SRC_PATH/OpenRA.Mods.Common/bin-android/$name" \
    "$SRC_PATH/OpenRA.Mods.Cnc/bin-android/$name" \
    "$SRC_PATH/OpenRA.Mods.D2k/bin-android/$name"
  do
    if [[ -f "$f" ]]; then
      echo "$f"
      return 0
    fi
  done
  # Last resort: search (skip AndroidLauncher)
  f=$(find "$SRC_PATH" -path '*/OpenRA.AndroidLauncher/*' -prune -o -name "$name" -type f -print 2>/dev/null | head -1 || true)
  if [[ -n "$f" && -f "$f" ]]; then
    echo "$f"
    return 0
  fi
  return 1
}

copy_checked() {
  local name="$1"
  local min="$2"
  local src
  if ! src=$(find_dll "$name"); then
    echo "ERROR: $name not found after build"
    exit 1
  fi
  local sz
  sz=$(stat -c%s "$src" 2>/dev/null || stat -f%z "$src")
  if [[ "$sz" -lt "$min" ]]; then
    echo "ERROR: $name is only $sz bytes (min $min) — refusing stub: $src"
    exit 1
  fi
  cp -f "$src" "$DEST_PATH/$name"
  echo "  OK $name ($sz bytes) from $src"
}

copy_checked "OpenRA.Mods.Common.dll" "$MIN_COMMON"

if [[ "$COPY_CNC_DLL" == "True" || "$COPY_CNC_DLL" == "true" ]]; then
  copy_checked "OpenRA.Mods.Cnc.dll" "$MIN_CNC"
fi

if [[ "$COPY_D2K_DLL" == "True" || "$COPY_D2K_DLL" == "true" ]]; then
  copy_checked "OpenRA.Mods.D2k.dll" "$MIN_D2K"
fi

# Dependency DLLs that Common pulls in (same set AppImage stages next to mods)
for dep in \
  TagLibSharp.dll MP3Sharp.dll NVorbis.dll Pfim.dll \
  BeaconLib.dll rix0rrr.BeaconLib.dll FuzzyLogicLibrary.dll \
  ICSharpCode.SharpZipLib.dll Mono.Nat.dll Newtonsoft.Json.dll \
  Linguini.Bundle.dll Linguini.Shared.dll Linguini.Syntax.dll \
  Microsoft.Extensions.DependencyModel.dll Eluant.dll
do
  if src=$(find_dll "$dep" 2>/dev/null); then
    cp -f "$src" "$DEST_PATH/$dep"
    echo "  dep $dep"
  fi
done

echo "=== install-assemblies-android done → $DEST_PATH ==="
ls -la "$DEST_PATH"
