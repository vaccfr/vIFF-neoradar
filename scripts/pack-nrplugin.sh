#!/usr/bin/env bash
# Builds the plugin and packs it as a .nrplugin for a package, for Windows, macOS and Linux.
#
#   scripts/pack-nrplugin.sh [--with-key] <output dir> [archive name]      (name defaults to vIFF-CDM-alpha)
#
# Without --with-key the build carries no vIFF API key: it reads vIFF, and controllers given the key set it with
# .cdm apikey. With --with-key it carries the key from VIFF_API_KEY or .env (see the README), as the EuroScope CDM
# plugin's DLL carries its key: anyone with the archive can read the key back out of it.
# Needs the .NET SDK, Python and neoradar-cli (or NEORADAR_CLI="node <cli>/dist/index.js").
set -euo pipefail

with_key=false
if [[ "${1:-}" == "--with-key" ]]; then
  with_key=true
  shift
fi

out="${1:?usage: pack-nrplugin.sh [--with-key] <output dir> [archive name]}"
name="${2:-vIFF-CDM-alpha}"
repo="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
cli="${NEORADAR_CLI:-neoradar-cli}"
stage="$(mktemp -d)"
trap 'rm -rf "$stage"' EXIT

if $with_key; then
  dotnet build "$repo/src/Viff.NeoRadar/Viff.NeoRadar.csproj" -c Release -o "$stage/bin" | tee "$stage/build.log"
  if ! grep -q "vIFF API key: [0-9]* characters built in" "$stage/build.log"; then
    echo "The build found no API key (VIFF_API_KEY or .env); nothing was packed." >&2
    exit 1
  fi
else
  dotnet build "$repo/src/Viff.NeoRadar/Viff.NeoRadar.csproj" -c Release -p:ViffEmbedKey=false -o "$stage/bin" | tee "$stage/build.log"
  if ! grep -q "vIFF API key: none built in" "$stage/build.log"; then
    echo "The build did not confirm a keyless plugin; nothing was packed." >&2
    exit 1
  fi
fi

# The key in .env, if any, must be in the DLL with --with-key and nowhere in it without. Only the result is printed.
python -I - "$repo/.env" "$stage/bin/Viff.NeoRadar.dll" "$with_key" <<'PY'
import os
import sys
from pathlib import Path
env, dll, want = Path(sys.argv[1]), Path(sys.argv[2]).read_bytes(), sys.argv[3] == "true"
key = os.environ.get("VIFF_API_KEY", "").strip().strip("\"'")
if not key and env.exists():
    for line in env.read_text(encoding="utf-8").splitlines():
        if line.strip().startswith("VIFF_API_KEY="):
            key = line.split("=", 1)[1].strip().strip("\"'")
found = bool(key) and (key.encode("utf-16-le") in dll or key.encode("utf-8") in dll)
if want and not found:
    sys.exit("The API key is not in the DLL; nothing was packed.")
if not want and found:
    sys.exit("The vIFF API key was found in the DLL; nothing was packed.")
print("Key check: key built in" if want else "Key check: clean")
PY

mkdir -p "$stage/plugins/$name" "$out"
# The plugin is one managed assembly with no native code, so the same file serves every platform. The archive keys
# select the binary by platform; any architecture matches a bare platform key.
for target in windows.dll mac.dylib linux.so; do
  cp "$stage/bin/Viff.NeoRadar.dll" "$stage/plugins/$name/$target"
done
out="$(cd "$out" && pwd)"
(cd "$stage" && $cli create-plugin-archives "$stage/plugins" -o "$out" --no-confirmation)
echo "Packed $out/$name.nrplugin ($($with_key && echo "API key built in" || echo "no API key"))"
