"""Entry point used by Fire3DGeometryWorker.ps1 (a script file, so its own folder is the import root)."""
import sys

from fet3d_geometry.__main__ import main

if __name__ == "__main__":
    sys.exit(main())
