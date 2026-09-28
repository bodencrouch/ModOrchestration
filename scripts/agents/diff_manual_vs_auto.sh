#!/usr/bin/env bash
# Compare the MANUAL modbuild install against the ModSync-AUTOMATED install.
#
#   scripts/agents/diff_manual_vs_auto.sh k1|k2 [extra args passed through]
#
# Wires the canonical tree, ledger and install-log paths so the comparison is
# reproducible and nobody has to remember that K2 content lives under
# steamassets/. Everything it touches is read-only; output lands in tmp/diff/.
set -euo pipefail

REPO="$(cd "$(dirname "${BASH_SOURCE[0]}")/../.." && pwd)"
GAME="${1:-}"
shift || true

case "$GAME" in
  k1|K1)
    MANUAL="$REPO/kotor_manual_workdir/K1_manual"
    AUTO="$REPO/kotor_auto_workdir/K1_auto"
    LEDGER="$REPO/tmp/manual_work_k1_fresh/ledger.jsonl"
    LOG="$REPO/tmp/auto_install/k1_auto_install.log"
    LABEL="K1 manual vs automated"
    ;;
  k2|K2)
    MANUAL="$REPO/kotor_manual_workdir/K2_manual/steamassets"
    AUTO="$REPO/kotor_auto_workdir/K2_auto/steamassets"
    LEDGER="$REPO/tmp/manual_work_k2_fresh/ledger.jsonl"
    LOG="$REPO/tmp/auto_install/k2_auto_install.log"
    LABEL="K2 manual vs automated"
    ;;
  *)
    echo "usage: $0 k1|k2 [extra diff_kotor_installs.py args]" >&2
    exit 2
    ;;
esac

OUT="$REPO/tmp/diff"
mkdir -p "$OUT"
STAMP="$(date -u +%Y%m%dT%H%M%SZ)"

ARGS=()
[ -f "$LEDGER" ] && ARGS+=(--ledger "$LEDGER")
[ -f "$LOG" ] && ARGS+=(--install-log "$LOG")

echo "manual: $MANUAL"
echo "auto:   $AUTO"

exec python3 "$REPO/scripts/diff_kotor_installs.py" \
  "$MANUAL" "$AUTO" \
  --a-name manual --b-name auto --label "$LABEL" \
  --hash-mode all --jobs 8 --max-examples 25 \
  "${ARGS[@]}" \
  --json "$OUT/${GAME,,}_manual_vs_auto_${STAMP}.json" \
  --md "$OUT/${GAME,,}_manual_vs_auto_${STAMP}.md" \
  "$@"
