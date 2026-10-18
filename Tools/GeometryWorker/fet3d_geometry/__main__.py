"""CLI: python -m fet3d_geometry convert --ifc model.ifc --out dir [--revision-id uuid]."""
import argparse
import json
import sys
import traceback

from .convert import ConversionError, convert


def main(argv=None) -> int:
    parser = argparse.ArgumentParser(prog="fet3d_geometry")
    sub = parser.add_subparsers(dest="command", required=True)
    run = sub.add_parser("convert", help="convert one IFC file")
    run.add_argument("--ifc", required=True)
    run.add_argument("--out", required=True)
    run.add_argument("--revision-id")
    args = parser.parse_args(argv)
    try:
        summary = convert(args.ifc, args.out, args.revision_id)
    except ConversionError as error:
        print(json.dumps({"ok": False, "code": error.code, "error": str(error)}, ensure_ascii=False))
        return 2
    except Exception as error:  # noqa: BLE001 - the agent needs a single JSON line whatever happened
        print(json.dumps({"ok": False, "code": "WORKER_EXCEPTION", "error": f"{type(error).__name__}: {error}"[:900],
                          "trace": traceback.format_exc()[-2000:]}, ensure_ascii=False))
        return 1
    print(json.dumps({k: summary[k] for k in ("ok", "outcome", "floors", "elements", "triangles", "spaces", "portals", "interactables")}, ensure_ascii=False))
    return 0


if __name__ == "__main__":
    sys.exit(main())
