#!/usr/bin/env bash
# Downloads every model HealthTech needs into ./models (the "../models" path in HealthTech/appsettings.json),
# including the two GGUF files of the local LLM (Qwen2.5 3B for term correction, 7B for the minutes).
# Already downloaded files are skipped; interrupted downloads resume.
set -euo pipefail

cd "$(dirname "$0")"
mkdir -p models
cd models

SHERPA=https://github.com/k2-fsa/sherpa-onnx/releases/download
WHISPER=https://huggingface.co/ggerganov/whisper.cpp/resolve/main
QWEN=https://huggingface.co/Qwen

fetch() { # <file name> <url>
    if [[ -s "$1" ]]; then
        echo "✓ $1 (already present)"
        return
    fi
    echo "↓ $1"
    curl -fL --retry 3 -C - -o "$1.partial" "$2"
    mv "$1.partial" "$1"
}

# Whisper (whisper.cpp ggml format). large-v3 is the configured model; turbo is ~2x faster, slightly less accurate.
fetch ggml-large-v3.bin        "$WHISPER/ggml-large-v3.bin"
fetch ggml-large-v3-turbo.bin  "$WHISPER/ggml-large-v3-turbo.bin"

# Voice activity detection.
fetch silero_vad.onnx          "$SHERPA/asr-models/silero_vad.onnx"

# Speaker embedding models (the release tag really is spelled "recongition"). CAM++ is the configured one:
# a fraction of the compute of ResNet34 per segment, and diarization is CPU-bound.
fetch 3dspeaker_speech_campplus_sv_zh_en_16k-common_advanced.onnx \
    "$SHERPA/speaker-recongition-models/3dspeaker_speech_campplus_sv_zh_en_16k-common_advanced.onnx"
fetch wespeaker_en_voxceleb_resnet34_LM.onnx \
    "$SHERPA/speaker-recongition-models/wespeaker_en_voxceleb_resnet34_LM.onnx"
fetch 3dspeaker_speech_eres2net_base_sv_zh-cn_3dspeaker_16k.onnx \
    "$SHERPA/speaker-recongition-models/3dspeaker_speech_eres2net_base_sv_zh-cn_3dspeaker_16k.onnx"

# Speaker segmentation: only published as an archive; its model.onnx is renamed to the configured name.
if [[ -s pyannote-segmentation-3.0.onnx ]]; then
    echo "✓ pyannote-segmentation-3.0.onnx (already present)"
else
    echo "↓ pyannote-segmentation-3.0.onnx"
    curl -fL --retry 3 "$SHERPA/speaker-segmentation-models/sherpa-onnx-pyannote-segmentation-3-0.tar.bz2" \
        | tar -xjO sherpa-onnx-pyannote-segmentation-3-0/model.onnx > pyannote-segmentation-3.0.onnx.partial
    mv pyannote-segmentation-3.0.onnx.partial pyannote-segmentation-3.0.onnx
fi

# Local LLM (LLamaSharp). 3B (2.1 GB) corrects terms, 7B (4.7 GB) extracts and verifies the minutes.
fetch qwen2.5-3b-instruct-q4_k_m.gguf "$QWEN/Qwen2.5-3B-Instruct-GGUF/resolve/main/qwen2.5-3b-instruct-q4_k_m.gguf"
fetch qwen2.5-7b-instruct-q4_k_m.gguf "$QWEN/Qwen2.5-7B-Instruct-GGUF/resolve/main/qwen2.5-7b-instruct-q4_k_m.gguf"

echo
ls -lh
