#!/usr/bin/env bash
set -euo pipefail

ROOT_DIR="$(cd "$(dirname "$0")/.." && pwd -P)"
OUTPUT_DIR="$ROOT_DIR/dist/unity-cli"

rm -rf "$OUTPUT_DIR"

dotnet publish \
  "$ROOT_DIR/cli/UnityCli.Cli/UnityCli.Cli.csproj" \
  -c Release \
  -r osx-arm64 \
  --self-contained true \
  -p:PublishSingleFile=true \
  -p:IncludeNativeLibrariesForSelfExtract=true \
  -p:PublishReadyToRun=true \
  -p:TieredCompilation=false \
  -p:TieredPGO=false \
  -o "$OUTPUT_DIR"

codesign -f -s - "$OUTPUT_DIR/unity-cli"

echo "published to $OUTPUT_DIR"
