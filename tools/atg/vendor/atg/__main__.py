"""`python -m atg` のエントリポイント。T2-9 完了により `atg.cli.main` へ委譲する。"""

import sys
from atg.cli import main

if __name__ == "__main__":
    sys.stdout.reconfigure(encoding="utf-8", newline="\n")
    sys.stderr.reconfigure(encoding="utf-8", newline="\n")
    raise SystemExit(main())
