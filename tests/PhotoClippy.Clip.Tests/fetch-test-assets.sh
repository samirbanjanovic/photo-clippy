#!/usr/bin/env bash
# Downloads the CLIP model files and public-domain/CC0 test photos used by PhotoClippy.Clip.Tests.
#
#   ./fetch-test-assets.sh <target-dir> [--fp32] [--no-models]
#
# Layout:  <target-dir>/models/clip-vit-base-patch32/{vision_model_quantized.onnx,text_model.onnx,vocab.json,merges.txt}
#          <target-dir>/images/*.jpg
# Then:    PHOTO_CLIPPY_MODELS=<target-dir>/models dotnet test tests/PhotoClippy.Clip.Tests
# --fp32 also fetches vision_model.onnx (needed for the opt-in benchmark, PHOTO_CLIPPY_CLIP_BENCH=1).
# Keep <target-dir> outside the repository (or git-ignored): the models are ~350–700 MB.
set -euo pipefail

if [[ $# -lt 1 || "$1" == -* ]]; then
  sed -n '2,10p' "$0"
  exit 2
fi

target="$1"
shift
fp32=0
models=1
for arg in "$@"; do
  case "$arg" in
    --fp32) fp32=1 ;;
    --no-models) models=0 ;;
    *) echo "unknown option: $arg" >&2; exit 2 ;;
  esac
done

ua="photo-clippy-tests/1.0 (https://github.com/; test asset fetch)"
hf="https://huggingface.co/Xenova/clip-vit-base-patch32/resolve/main"
model_dir="$target/models/clip-vit-base-patch32"
image_dir="$target/images"
mkdir -p "$model_dir" "$image_dir"

sha256() {
  if command -v sha256sum >/dev/null; then sha256sum "$1" | cut -d' ' -f1; else shasum -a 256 "$1" | cut -d' ' -f1; fi
}

urlencode() {
  local s="$1" out="" c i
  for ((i = 0; i < ${#s}; i++)); do
    c="${s:i:1}"
    case "$c" in
      [a-zA-Z0-9._~-]) out+="$c" ;;
      ' ') out+="_" ;;
      *) out+="$(printf '%%%02X' "'$c")" ;;
    esac
  done
  printf '%s' "$out"
}

fetch_model() {
  local repo_path="$1" sha="$2" dest="$model_dir/$(basename "$1")"
  if [[ -f "$dest" && "$(sha256 "$dest")" == "$sha" ]]; then
    echo "ok       $dest"
    return
  fi
  echo "download $repo_path"
  curl -fsSL --retry 3 -A "$ua" -o "$dest.part" "$hf/$repo_path"
  if [[ "$(sha256 "$dest.part")" != "$sha" ]]; then
    echo "sha256 mismatch for $repo_path" >&2
    rm -f "$dest.part"
    exit 1
  fi
  mv "$dest.part" "$dest"
}

if [[ $models -eq 1 ]]; then
  fetch_model onnx/vision_model_quantized.onnx 583fd1110a514667812fee7d684952aaf82a99b959760c8d7dca7e0ab9839299
  fetch_model onnx/text_model.onnx 3f6571f5bad13a97c469c1622e1cfc4d9aef78b79fdbfcff804ca357bfada8cc
  fetch_model vocab.json 5047b556ce86ccaf6aa22b3ffccfc52d391ea4accdab9c2f2407da5b742d4363
  fetch_model merges.txt 9fd691f7c8039210e0fced15865466c65820d09b63988b0174bfe25de299051a
  if [[ $fp32 -eq 1 ]]; then
    fetch_model onnx/vision_model.onnx fd6e1402a588279d1723c7534d4bcba5bc0b14b47dfab0e46f8c47b8270d7d40
  fi
fi

# Wikimedia Commons files (licence in brackets), fetched as small (≈960 px) thumbnails.
images=(
  "landscape.jpg|Lake Rotoiti from Mount Robert ridge.jpg"                                    # [Public domain]
  "landscape2.jpg|View On Dent De Lys (228681147).jpeg"                                       # [CC0]
  "portrait.jpg|Woman in a headwrap in Quebec City.jpg"                                       # [CC0]
  "cat.jpg|Tabby cat with blue eyes-3336579.jpg"                                              # [CC0]
  "food.jpg|A full plate Assame Food Thali.jpg"                                               # [CC0]
  "night.jpg|Ho Chi Minh City Skyline at Night.jpg"                                           # [CC0]
  "night2.jpg|Quezon City skyline at night (Quezon Avenue, West Avenue, West Triangle) 03.jpg" # [CC0]
)
for entry in "${images[@]}"; do
  name="${entry%%|*}"
  title="${entry#*|}"
  if [[ -s "$image_dir/$name" ]]; then
    echo "ok       $image_dir/$name"
    continue
  fi
  echo "download $title"
  curl -fsSL --retry 3 -A "$ua" -o "$image_dir/$name.part" "https://commons.wikimedia.org/wiki/Special:FilePath/$(urlencode "$title")?width=640"
  mv "$image_dir/$name.part" "$image_dir/$name"
done

echo "done: PHOTO_CLIPPY_MODELS=$target/models"
