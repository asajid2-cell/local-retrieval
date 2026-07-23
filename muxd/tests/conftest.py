"""Make `import muxctl` / `import muxd` resolve no matter which directory pytest runs from.

The verifier is documented as `python -m pytest muxd/tests/... -q` from the REPO ROOT. Without
this, collection dies with ModuleNotFoundError: pytest only puts the rootdir (and each test
file's own dir) on sys.path, never muxd/.
"""
import sys
from pathlib import Path

MUXD_DIR = str(Path(__file__).resolve().parent.parent)
if MUXD_DIR not in sys.path:
    sys.path.insert(0, MUXD_DIR)
