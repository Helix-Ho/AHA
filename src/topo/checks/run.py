from pathlib import Path
import sys
sys.path.insert(0, str(Path(__file__).resolve().parents[3] / 'src/aha/checks/runner'))
from runner import main
if __name__ == '__main__':
    raise SystemExit(main('topo'))
