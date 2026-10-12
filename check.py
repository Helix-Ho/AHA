#!/usr/bin/env python3
"""Run AHA core regression checks using the project virtual environment."""
from pathlib import Path
import sys
sys.path.insert(0, str(Path(__file__).resolve().parent / 'src/aha/checks/runner'))
from runner import main
if __name__ == '__main__':
    raise SystemExit(main())
