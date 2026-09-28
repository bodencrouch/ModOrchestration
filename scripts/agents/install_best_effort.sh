#!/usr/bin/env bash
# Best-effort full install: download what can be fetched, install all selected mods,
# skip missing archives and individual mod failures (Nexus key from env or settings).
#
# Exit status (see docs/knowledgebase/core-cli-reference.md, install exit codes):
#   0  never, for this script: --best-effort/--skip-validation cannot produce a published PASS.
#   2  completed, unverified: the run reached the end of the plan. Component failures and
#      skipped archives are listed in the log ("Installation finished unverified, with N
#      component failure(s)"). Read that line; the exit code does not distinguish them.
#   1  failure: the run stopped (bad arguments, download/installer error, etc.).
set -euo pipefail

ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/../.." && pwd)"
cd "$ROOT"

TOML="${1:-$ROOT/mod-builds/TOMLs/KOTOR1_Full.toml}"
GAME_DIR="${2:-$ROOT/tmp/k1_best_game}"
MOD_DIR="${3:-$ROOT/tmp/k1_best_mods}"

if [[ ! -f "$TOML" ]]; then
  echo "Instruction file not found: $TOML" >&2
  echo "Usage: $0 [path/to/KOTOR1_Full.toml] [game_dir] [mod_workspace_dir]" >&2
  exit 1
fi

mkdir -p "$MOD_DIR"
"$ROOT/scripts/agents/create_template_kotor_install.sh" "$GAME_DIR" "$MOD_DIR"

# shellcheck source=common.sh
source "$(dirname "${BASH_SOURCE[0]}")/common.sh"
ensure_core_resources_symlink "$ROOT"

dotnet build "$ROOT/src/ModSync.Core/ModSync.Core.csproj" -c Debug -f net9.0 -v q

install_status=0
dotnet run --project "$ROOT/src/ModSync.Core/ModSync.Core.csproj" -f net9.0 --no-build -- \
  install -i "$TOML" -g "$GAME_DIR" -s "$MOD_DIR" \
  -d --concurrent \
  --best-effort \
  --skip-validation \
  --download-timeout-hours 72 || install_status=$?

if [[ "$install_status" -eq 2 ]]; then
  echo "Best-effort install completed, unverified (exit 2): files were applied without a published install PASS." >&2
  echo "Check the log for 'finished unverified, with N component failure(s)' before trusting the game directory." >&2
elif [[ "$install_status" -ne 0 ]]; then
  echo "Best-effort install failed with exit status $install_status." >&2
fi
exit "$install_status"
