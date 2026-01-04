#!/usr/bin/env bash
set -euo pipefail

ROOT="$(cd -- "$(dirname -- "${BASH_SOURCE[0]}")" && pwd)"
cd "$ROOT"

# Your mod folder is nested
MODDIR="$ROOT/ReceiverSpeedometer"
MODNAME="$(basename "$MODDIR")"

if [[ ! -d "$MODDIR" ]]; then
  echo "error: expected mod dir at $MODDIR"
  exit 1
fi

if [[ ! -f "$MODDIR/ReceiverSpeedometer.cs" ]]; then
  echo "error: expected $MODDIR/ReceiverSpeedometer.cs (RML source entrypoint)"
  exit 1
fi

if [[ ! -f "$MODDIR/modinfo.json" ]]; then
  echo "error: expected $MODDIR/modinfo.json"
  exit 1
fi

BUILD_DIR="$ROOT/build"
OUT="$ROOT/${MODNAME}.rmod"

cleanup() {
  rm -rf "$BUILD_DIR"
}
trap cleanup EXIT

mkdir -p "$BUILD_DIR"

# Copy mod files, excluding build artifacts and project files
rsync -a \
  --exclude 'bin/' \
  --exclude 'obj/' \
  --exclude '*.csproj' \
  --exclude '*.csproj.user' \
  --exclude '*.sln' \
  --exclude '*.rmod' \
  "$MODDIR/" "$BUILD_DIR/"

# Remove any existing output first
rm -f "$OUT"

# Create .rmod (zip) from BUILD_DIR contents (no extra top-level folder)
(
  cd "$BUILD_DIR"
  command -v zip >/dev/null 2>&1 || { echo "error: zip not installed"; exit 1; }
  zip -qr "$OUT" .
)

echo "ok: $OUT"
