"""Checks a runtime catalog payload against this Unity checkout before it is registered.

Usage (from the Unity repo root):  python -I Tools/RuntimeCatalog/check_payload.py Tools/RuntimeCatalog/runtime-2.0.1.json

Fails when the payload version differs from RuntimeCatalog.RuntimeVersion, when the capability set differs from
Capabilities in Runtime/Contracts, or when a contract parameter, event type, event payload field or action id does not
appear in the runtime code. Exits do not use an action button (ExitZone triggers on entering), so `enter` is allowed.
"""
import glob
import json
import os
import re
import sys

ROOT = os.path.abspath(os.path.join(os.path.dirname(__file__), "..", ".."))
RUNTIME = os.path.join(ROOT, "Assets", "_Fire3D", "Runtime")
TRIGGER_ACTIONS = {"enter"}


def main(path: str) -> int:
    payload = json.load(open(path, encoding="utf-8"))
    sources = {p: open(p, encoding="utf-8").read() for p in glob.glob(os.path.join(RUNTIME, "**", "*.cs"), recursive=True)}
    code = "\n".join(sources.values())
    problems = []

    version = re.search(r'RuntimeVersion = "([0-9.]+)"', code)
    if not version or version.group(1) != payload["runtimeVersion"]:
        problems.append(f"runtimeVersion {payload['runtimeVersion']} != RuntimeCatalog.RuntimeVersion {version and version.group(1)}")
    protocol = re.search(r'CurrentProtocol = "([^"]+)"', code)
    if not protocol or protocol.group(1) != payload["protocolVersion"]:
        problems.append(f"protocolVersion {payload['protocolVersion']} != LaunchRequest.CurrentProtocol {protocol and protocol.group(1)}")

    capability_file = next(text for text in sources.values() if "static class Capabilities" in text)
    unity_caps = set(re.findall(r'public const string \w+ = "([a-z]+\.[a-z_.]+)";', capability_file))
    caps = set(payload["capabilities"])
    if caps != unity_caps:
        problems.append(f"capabilities differ: payload-only {sorted(caps - unity_caps)}, Unity-only {sorted(unity_caps - caps)}")
    if set(payload["capabilityContracts"]) - caps:
        problems.append(f"contracts for unlisted capabilities: {sorted(set(payload['capabilityContracts']) - caps)}")

    checked = 0
    for cap, contract in payload["capabilityContracts"].items():
        for name in (contract.get("parameters") or {}).get("properties", {}):
            checked += 1
            if f'"{name}"' not in code:
                problems.append(f"{cap}: parameter '{name}' is not read by the runtime")
        for event in contract.get("events", []):
            checked += 1
            if f'"{event["type"]}"' not in code:
                problems.append(f"{cap}: event '{event['type']}' is never emitted")
            for field in event.get("payload", {}).get("properties", {}):
                checked += 1
                if f'"{field}"' not in code and field[0].upper() + field[1:] not in code:
                    problems.append(f"{cap}: event '{event['type']}' field '{field}' is not written")
        for action in contract.get("actions", []):
            checked += 1
            if action["id"] not in TRIGGER_ACTIONS and f'"{action["id"]}"' not in code:
                problems.append(f"{cap}: action '{action['id']}' is not offered")

    for problem in problems:
        print("FAIL", problem)
    print(f"{len(caps)} capabilities, {checked} contract items checked, {len(problems)} problem(s)")
    return 1 if problems else 0


if __name__ == "__main__":
    sys.exit(main(sys.argv[1] if len(sys.argv) > 1 else os.path.join(os.path.dirname(__file__), "runtime-2.0.1.json")))
