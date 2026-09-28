#!/usr/bin/env bash
# Generate a synthetic KOTOR install, the synthetic mod archives, and a build file that
# installs them. Everything is written from format layouts - no game data is copied.
#
# The generator itself lives in src/ModSync.Tests/Fixtures/MockKotorInstall.cs so the tests
# and this script build the same tree.
set -euo pipefail

repo_root="$(cd "$(dirname "${BASH_SOURCE[0]}")/../.." && pwd)"

game="k1"
out_dir=""
mod_dir=""
build_file=""
configuration="Debug"
do_build=true

usage() {
  cat <<'EOF'
Usage: create_mock_kotor_install.sh --out <dir> [options]

Options:
  --out PATH           Where to write the mock install (required, recreated)
  --game k1|k2         Which game layout to generate (default: k1)
  --mod-dir PATH       Where to write the synthetic mod archives
                       (default: <out>/../mods)
  --build-file PATH    Where to write the TOML build file
                       (default: <out>/../mock_build.toml)
  --configuration CFG  Debug or Release (default: Debug)
  --no-build           Use the existing ModSync.Tests output instead of building
  -h, --help           Show this help

Prints shell-style key=value lines describing what was created.
EOF
}

while [[ $# -gt 0 ]]; do
  case "$1" in
    --out) out_dir="$2"; shift 2 ;;
    --game) game="$2"; shift 2 ;;
    --mod-dir) mod_dir="$2"; shift 2 ;;
    --build-file) build_file="$2"; shift 2 ;;
    --configuration) configuration="$2"; shift 2 ;;
    --no-build) do_build=false; shift ;;
    -h|--help) usage; exit 0 ;;
    *) echo "error: unknown option '$1'" >&2; usage >&2; exit 1 ;;
  esac
done

if [[ -z "$out_dir" ]]; then
  echo "error: --out is required" >&2
  usage >&2
  exit 1
fi

case "$game" in
  k1|K1|kotor1|k2|K2|kotor2) ;;
  *) echo "error: --game must be k1 or k2" >&2; exit 1 ;;
esac

parent_dir="$(cd "$(dirname "$out_dir")" && pwd)"
: "${mod_dir:=$parent_dir/mods}"
: "${build_file:=$parent_dir/mock_build.toml}"

tests_project="$repo_root/src/ModSync.Tests/ModSync.Tests.csproj"
tests_dll="$repo_root/src/ModSync.Tests/bin/$configuration/net9.0/ModSync.Tests.dll"

if [[ "$do_build" == true ]]; then
  dotnet build "$tests_project" --configuration "$configuration" -f net9.0 >/dev/null
fi

if [[ ! -f "$tests_dll" ]]; then
  echo "error: ModSync.Tests is not built at $tests_dll (drop --no-build?)" >&2
  exit 1
fi

dotnet "$tests_dll" make-mock-kotor --game "$game" --out "$out_dir"
dotnet "$tests_dll" make-mock-mods --out "$mod_dir"
dotnet "$tests_dll" make-mock-build --game "$game" --out "$build_file"
