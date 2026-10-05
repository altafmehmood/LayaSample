#!/bin/sh
# Downloads model files at pinned revisions and verifies their SHA-256. POSIX sh, so it also runs in Alpine.
#
#   scripts/download-models.sh ocr <OcrModel> [dir]   PP-OCRv6 files into <dir>/v6 (default models/ocr; the build
#                                                     copies them to models/v6 next to the renderer binary)
#   scripts/download-models.sh laya [dir]             Laya classifier (~850 MB) into <dir> (default
#                                                     models/elbruno_laya-onnx, where `dotnet run` looks for it)
#
# <OcrModel> is the DocumentAnalysis:OcrModel value: PPOCRv5Latin (bundled in the NuGet, nothing to download),
# PPOCRv6Tiny, PPOCRv6Small or PPOCRv6Medium.
#
# MODEL_SEED=<dir> names a folder laid out like the target (for example a copy from another machine). A file there
# with the right SHA-256 is copied instead of downloaded, so curl only runs for files that are missing or different.
set -eu

# Sources and hashes: https://github.com/RapidAI/RapidOCR/blob/main/python/rapidocr/default_models.yaml (v3.9.2)
# and https://huggingface.co/elbruno/laya-onnx at the commit below. Dictionary and JSON hashes were taken from
# downloads at those revisions.
OCR_BASE=https://www.modelscope.cn/models/RapidAI/RapidOCR/resolve/v3.9.2
LAYA_BASE=https://huggingface.co/elbruno/laya-onnx/resolve/dea9bba5c04d083652eeb7bc9f6b295f04a04529

sha256() {
    if command -v sha256sum >/dev/null 2>&1; then sha256sum "$1" | cut -d' ' -f1; else shasum -a 256 "$1" | cut -d' ' -f1; fi
}

# fetch <url> <dir> <path> <sha256>: makes <dir>/<path> a file with that hash. Keeps a correct file already there, else
# copies a correct one from $MODEL_SEED/<path>, else downloads it.
fetch() {
    target="$2/$3"
    if [ -f "$target" ] && [ "$(sha256 "$target")" = "$4" ]; then echo "ok        $target"; return; fi
    mkdir -p "$(dirname "$target")"
    if [ -n "${MODEL_SEED:-}" ] && [ -f "$MODEL_SEED/$3" ] && [ "$(sha256 "$MODEL_SEED/$3")" = "$4" ]; then
        cp "$MODEL_SEED/$3" "$target"
        echo "local     $target"
        return
    fi
    echo "download  $target"
    # Retries resume (-C -) where a dropped connection left off, which matters for the 850 MB Laya weights.
    rm -f "$target.part"
    curl -fsSL --retry 5 --retry-delay 2 --retry-all-errors -C - -o "$target.part" "$1"
    actual=$(sha256 "$target.part")
    if [ "$actual" != "$4" ]; then
        rm -f "$target.part"
        echo "checksum mismatch for $1: expected $4, got $actual" >&2
        exit 1
    fi
    mv "$target.part" "$target"
}

ocr() {
    dir=${2:-models/ocr}/v6
    case "$1" in
        PPOCRv5Latin) echo "PPOCRv5Latin ships in the RapidOcrNet NuGet; nothing to download"; return ;;
        PPOCRv6Tiny)
            fetch "$OCR_BASE/onnx/PP-OCRv6/det/PP-OCRv6_det_tiny.onnx" "$dir" "PP-OCRv6_det_tiny.onnx" f42c0fbd294d95eac1a550e131b277dac97462c8025fa4b6c3cec1b7894bd3d5
            fetch "$OCR_BASE/onnx/PP-OCRv6/rec/PP-OCRv6_rec_tiny.onnx" "$dir" "PP-OCRv6_rec_tiny.onnx" e16e242de5937ad92609223f19bc2aff3727ee40b095f996907c24749bad251b
            fetch "$OCR_BASE/paddle/PP-OCRv6/rec/PP-OCRv6_rec_tiny/ppocrv6_tiny_dict.txt" "$dir" "ppocrv6_tiny_dict.txt" c5cbe34ef40c29c4df07ed012bf96569cb69a2d2a01a07027e9f13cb832bd9cd ;;
        PPOCRv6Small)
            fetch "$OCR_BASE/onnx/PP-OCRv6/det/PP-OCRv6_det_small.onnx" "$dir" "PP-OCRv6_det_small.onnx" 090f04abcd9d9a7498bc4ebf677e4cb9bdce1fe4197ddb7e529f1ef44e1ff94f
            fetch "$OCR_BASE/onnx/PP-OCRv6/rec/PP-OCRv6_rec_small.onnx" "$dir" "PP-OCRv6_rec_small.onnx" 6f327246b50388f3c176ae304bd95767ea6dc0c9ae92153ef8cbe210b3c14884
            fetch "$OCR_BASE/paddle/PP-OCRv6/rec/PP-OCRv6_rec_small/ppocrv6_dict.txt" "$dir" "ppocrv6_dict.txt" b5f2bfe2bdd9448429e3e82b51c789775d9b42f2403d082b00662eb77e401c5d ;;
        PPOCRv6Medium)
            fetch "$OCR_BASE/onnx/PP-OCRv6/det/PP-OCRv6_det_medium.onnx" "$dir" "PP-OCRv6_det_medium.onnx" 92078b7355007ccfffcd4c8cd441a3afd4538904d06881b29a155e1e679907c2
            fetch "$OCR_BASE/onnx/PP-OCRv6/rec/PP-OCRv6_rec_medium.onnx" "$dir" "PP-OCRv6_rec_medium.onnx" eef444829dbbe18d7fea59a3f6eb75647518d2b3a9568d27c92e42940204894b
            fetch "$OCR_BASE/paddle/PP-OCRv6/rec/PP-OCRv6_rec_medium/ppocrv6_dict.txt" "$dir" "ppocrv6_dict.txt" b5f2bfe2bdd9448429e3e82b51c789775d9b42f2403d082b00662eb77e401c5d ;;
        *) echo "unknown OCR model '$1'; expected PPOCRv5Latin, PPOCRv6Tiny, PPOCRv6Small or PPOCRv6Medium" >&2; exit 2 ;;
    esac
}

laya() {
    dir=${1:-models/elbruno_laya-onnx}
    fetch "$LAYA_BASE/config.json" "$dir" "config.json" 7f7ac00d9f70a2d5e8f41a10b6984d4a0833405984b3bf859ff1d49f1bced359
    fetch "$LAYA_BASE/rl_agent_config.json" "$dir" "rl_agent_config.json" ae287b56bbcf5f8c4f4541ae9dfd00c914c4c48b940b8398c3058af37ba92bbd
    fetch "$LAYA_BASE/tokenizer/tokenizer.json" "$dir" "tokenizer/tokenizer.json" 6c8aaa9a542084f2457eab775d4eeb51f92a70c0fd9de28d5edb0ddec3c08d30
    fetch "$LAYA_BASE/tokenizer/tokenizer_config.json" "$dir" "tokenizer/tokenizer_config.json" 50044de60daaa73df97d262e15a40d4faf0160e7d742df64b377877a1320dd12
    fetch "$LAYA_BASE/model.onnx" "$dir" "model.onnx" 75d37762009e57b03b28877d330a2a466acd576eb444b0fdd00ee2a3221f8761
    fetch "$LAYA_BASE/model.onnx.data" "$dir" "model.onnx.data" 356764eb3ab760468479e6fa732217d8ccaa84ab38f2ca9e2ed174ae22aaf1b4
}

case "${1:-}" in
    ocr) shift; [ $# -ge 1 ] || { echo "usage: $0 ocr <OcrModel> [models-dir]" >&2; exit 2; }; ocr "$@" ;;
    laya) shift; laya "$@" ;;
    *) echo "usage: $0 ocr <OcrModel> [models-dir] | laya [dir]" >&2; exit 2 ;;
esac
