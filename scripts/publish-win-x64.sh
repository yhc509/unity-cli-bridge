#!/usr/bin/env bash
set -euo pipefail

ROOT_DIR="$(cd "$(dirname "$0")/.." && pwd -P)"
OUTPUT_DIR="$ROOT_DIR/dist/unity-cli-win-x64"

rm -rf "$OUTPUT_DIR"

dotnet publish \
  "$ROOT_DIR/cli/UnityCli.Cli/UnityCli.Cli.csproj" \
  -c Release \
  -r win-x64 \
  --self-contained true \
  -p:PublishSingleFile=true \
  -p:IncludeNativeLibrariesForSelfExtract=true \
  -p:PublishReadyToRun=true \
  -p:TieredPGO=false \
  -o "$OUTPUT_DIR"

echo "published to $OUTPUT_DIR"
