#!/usr/bin/env bash
# atg.sh — Run the vendored ATG CLI from any repository.
#
# Resolves the interpreter, puts vendor/ on the import path, and forwards every
# argument to `python -m atg`. Does not change directory: ATG resolves its
# state directory relative to the git repository root, not the launcher's
# own location, so the caller's working directory must be preserved.
#
# Usage:
#   ./atg.sh --help
#   ./atg.sh init
#   ./atg.sh contract check atg-contract.json
#
# Environment:
#   ATG_PYTHON  Interpreter to use (default: .venv-atg if present, else python3).
set -euo pipefail

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
VENDOR_DIR="${SCRIPT_DIR}/vendor"
VENV_PY="${SCRIPT_DIR}/.venv-atg/bin/python"

if [ ! -f "${VENDOR_DIR}/atg/cli.py" ]; then
    echo "vendor/atg is missing. Re-copy the kit with copy_to_repo.py." >&2
    exit 2
fi

if [ -n "${ATG_PYTHON:-}" ]; then
    PYTHON="$ATG_PYTHON"
elif [ -x "$VENV_PY" ]; then
    PYTHON="$VENV_PY"
else
    PYTHON="python3"
fi

export PYTHONPATH="${VENDOR_DIR}${PYTHONPATH:+:${PYTHONPATH}}"
exec "$PYTHON" -m atg "$@"
