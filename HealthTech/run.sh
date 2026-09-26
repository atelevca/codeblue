#!/usr/bin/env bash
# Runs HealthTech outside the IDE on macOS (Apple Silicon): Whisper runs on the GPU via Metal.
# Metal exposes a single GPU, so WHISPER_GPU_DEVICE=0 (set by the "mac" launch profile).
# Prerequisites: `brew install ffmpeg` and ../download-models.sh.
set -euo pipefail
cd "$(dirname "$0")"
export ASPNETCORE_ENVIRONMENT="${ASPNETCORE_ENVIRONMENT:-Development}"
exec dotnet run --project HealthTech.csproj --launch-profile mac "$@"
