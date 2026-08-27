from __future__ import annotations

import argparse
import json


def main() -> int:
    parser = argparse.ArgumentParser(prog="python -m hubradio_worker")
    parser.add_argument("--health", action="store_true", required=True)
    parser.parse_args()

    print(json.dumps({"status": "ok"}, sort_keys=True, separators=(",", ":")))
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
