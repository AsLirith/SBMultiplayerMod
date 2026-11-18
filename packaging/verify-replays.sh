#!/usr/bin/env bash
# Re-simulates replays with the game to check that the runs are genuine (see docs/replay-verification.md).
# Usage, from anywhere:
#   ./verify-replays.sh <results folder>        every result exported by the server -> verification.csv in that folder
#   ./verify-replays.sh <file.rpl | folder>...  replays or folders of replays -> verification.jsonl here
# This script must stay in the SBMultiplayer folder of the game. Steam doesn't need to be running.
set -euo pipefail
mod="$(cd "$(dirname "$0")" && pwd)"
game="$(dirname "$mod")"
if [ $# -eq 0 ]; then
  sed -n '2,6p' "$0"
  exit 1
fi
args=()
for a in "$@"; do args+=("$(realpath "$a")"); done
if [ -z "${SBMULTI_VERIFY:-}" ]; then
  if [ -d "$1" ] && compgen -G "$1/*_round*.json" > /dev/null; then
    SBMULTI_VERIFY="$(realpath "$1")/verification.jsonl"
  else
    SBMULTI_VERIFY="$PWD/verification.jsonl"
  fi
  rm -f "$SBMULTI_VERIFY"
fi
export SBMULTI_VERIFY
export DOTNET_STARTUP_HOOKS="$mod/SBMultiplayer.Client.dll"
cd "$game"
./STUNTBOOST "${args[@]}"
