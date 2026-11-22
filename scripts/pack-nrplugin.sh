#!/usr/bin/env bash
# Builds the plugin WITHOUT any vIFF API key and packs it as a .nrplugin for a package.
#
#   scripts/pack-nrplugin.sh <output dir> [archive name]      (name defaults to vIFF-CDM-alpha)
#
# A published package is downloaded by everyone and a key in a DLL can be read back out of it, so a package build
# never carries the key: controllers set it with .cdm apikey. Local builds read it from .env (see the README).
# Needs the .NET SDK, Python and neoradar-cli (or NEORADAR_CLI="node <cli>/dist/index.js").
set -euo pipefail

out="${1:?usage: pack-nrplugin.sh <output dir> [archive name]}"
name="${2:-vIFF-CDM-alpha}"
repo="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
cli="${NEORADAR_CLI:-neoradar-cli}"
stage="$(mktemp -d)"
trap 'rm -rf "$stage"' EXIT

dotnet build "$repo/src/Viff.NeoRadar/Viff.NeoRadar.csproj" -c Release -p:ViffEmbedKey=false -o "$stage/bin" | tee "$stage/build.log"
if ! grep -q "vIFF API key: none built in" "$stage/build.log"; then
  echo "The build did not confirm a keyless plugin; nothing was packed." >&2
  exit 1
fi

# Belt and braces: the key in .env, if any, must appear nowhere in the DLL. Only "clean" or "found" is printed.
python -I - "$repo/.env" "$stage/bin/Viff.NeoRadar.dll" <<'PY'
import sys
from pathlib import Path
env, dll = Path(sys.argv[1]), Path(sys.argv[2]).read_bytes()
key = ""
if env.exists():
    for line in env.read_text(encoding="utf-8").splitlines():
        if line.strip().startswith("VIFF_API_KEY="):
            key = line.split("=", 1)[1].strip().strip("\"'")
if key and (key.encode("utf-16-le") in dll or key.encode("utf-8") in dll):
    sys.exit("The vIFF API key was found in the DLL; nothing was packed.")
print("Key check: clean")
PY

mkdir -p "$stage/plugins/$name" "$out"
# The plugin is one managed assembly with no native code, so the same file serves every platform. The archive keys
# select the binary by platform; any architecture matches a bare platform key.
for target in windows.dll mac.dylib linux.so; do
  cp "$stage/bin/Viff.NeoRadar.dll" "$stage/plugins/$name/$target"
done
out="$(cd "$out" && pwd)"
(cd "$stage" && $cli create-plugin-archives "$stage/plugins" -o "$out" --no-confirmation)
echo "Packed $out/$name.nrplugin (no API key)"
