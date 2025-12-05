#!/bin/bash
# Start the Cljr nREPL server
# Usage: ./nrepl.sh [port]

SCRIPT_DIR="$(cd "$(dirname "$0")" && pwd)"
PORT="${1:-0}"

dotnet run --project "$SCRIPT_DIR/src/Cljr.Cli" -- nrepl -p "$PORT"
