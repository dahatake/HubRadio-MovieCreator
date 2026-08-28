from __future__ import annotations

import argparse
import json
import sys

from hubradio_worker.protocol import run_protocol


def main() -> int:
    parser = argparse.ArgumentParser(prog="python -m hubradio_worker")
    mode = parser.add_mutually_exclusive_group(required=True)
    mode.add_argument("--health", action="store_true")
    mode.add_argument("--protocol", action="store_true")
    arguments = parser.parse_args()

    if arguments.health:
        print(json.dumps({"status": "ok"}, sort_keys=True, separators=(",", ":")))
        return 0

    return run_protocol(sys.stdin.buffer, sys.stdout.buffer)


if __name__ == "__main__":
    raise SystemExit(main())
