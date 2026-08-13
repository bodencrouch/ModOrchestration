#!/usr/bin/env bash
# Creates an empty KOTOR-shaped directory skeleton. The files it writes are plain text, so
# nothing that parses a game format will accept them and no detector scores this tree.
#
# For a tree ModSync actually recognises as a KOTOR install - real KEY/BIF/TLK/RIM/ERF/2DA
# headers, correct override casing per game, and the Aspyr steamassets nesting - use
# scripts/agents/create_mock_kotor_install.sh instead. This script is kept as-is because
# install_best_effort.sh depends on it staying fast and dependency-free.
set -euo pipefail

if [[ $# -lt 2 ]]; then
  echo "Usage: $0 <kotor_dir> <mod_dir>" >&2
  exit 1
fi

kotor_dir="$1"
mod_dir="$2"

mkdir -p \
  "$kotor_dir/data" \
  "$kotor_dir/lips" \
  "$kotor_dir/modules/extras" \
  "$kotor_dir/movies" \
  "$kotor_dir/Override" \
  "$kotor_dir/rims" \
  "$kotor_dir/streammusic" \
  "$kotor_dir/streamsounds" \
  "$kotor_dir/streamwaves/globe" \
  "$kotor_dir/TexturePacks" \
  "$kotor_dir/utils/swupdateskins" \
  "$mod_dir"

printf 'fake exe\n' > "$kotor_dir/swkotor.exe"
printf 'fake dialog\n' > "$kotor_dir/dialog.tlk"

cat <<EOF
Created template KOTOR install:
  kotor_dir=$kotor_dir
  mod_dir=$mod_dir
EOF
