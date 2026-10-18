"""Writes a minimal fet3d.editor/1 scenario for a converted building, standing in for what an OrganizationUser authors in the
web editor (spawn, one fire, the exit door confirmed as door.exit and the goal just outside it). Test only.

    python make_scenario.py <converter output dir> [--spawn-space A203] [--exit-door <GlobalId>] [--fire-space A103]

Coordinates are in the contract (GLB) frame; placement y is height above the floor.
"""
import argparse
import json
import math
import os


def centre(space):
    (x0, y0, z0), (x1, y1, z1) = space["bbox"]
    return (x0 + x1) / 2, (z0 + z1) / 2


def area(space):
    (x0, _, z0), (x1, _, z1) = space["bbox"]
    return (x1 - x0) * (z1 - z0)


def placement(floor, x, z, y=0.0, yaw_deg=0.0):
    half = math.radians(yaw_deg) / 2
    return {"floorId": floor, "position": {"x": round(x, 3), "y": y, "z": round(z, 3)},
            "rotation": {"x": 0.0, "y": round(math.sin(half), 6), "z": 0.0, "w": round(math.cos(half), 6)}}


def main():
    parser = argparse.ArgumentParser()
    parser.add_argument("out")
    parser.add_argument("--spawn-space")
    parser.add_argument("--fire-space")
    parser.add_argument("--exit-door")
    args = parser.parse_args()
    with open(os.path.join(args.out, "building_metadata.json"), encoding="utf-8") as handle:
        building = json.load(handle)
    floors = {s["id"]: s for s in building["storeys"]}
    rooms = [s for s in building["spaces"] if s.get("category") not in ("outdoor",) and area(s) < 400 and (s.get("name") or "").lower() != "roof"]
    by_name = {s.get("name"): s for s in rooms}

    exits = [i for i in building["interactables"] if i["catalogKey"] == "door.exit"]
    if args.exit_door:
        exits = [i for i in building["interactables"] if i["ifcGlobalId"] == args.exit_door]
    if not exits:
        raise SystemExit("no exit door candidate; pass --exit-door")
    door = exits[0]
    ground = door["floorId"]

    spawn = by_name.get(args.spawn_space) if args.spawn_space else max(
        (s for s in rooms if s["storey"] != ground), key=area, default=None) or max(rooms, key=area)
    fire = by_name.get(args.fire_space) if args.fire_space else next(
        (s for s in rooms if s["storey"] == ground and s is not spawn and s.get("category") in ("kitchen", "room", "living")), rooms[0])

    # Goal 1.4 m outside the door, away from the building centre (doors sit on the outer wall).
    dx, dz = door["position"][0], door["position"][2]
    length = math.hypot(dx, dz) or 1.0
    thin_x = door.get("rotationY", 0) == 90.0
    if thin_x:
        gx, gz = dx + math.copysign(1.4, dx), dz
    else:
        gx, gz = dx, dz + math.copysign(1.4, dz)
    sx, sz = centre(spawn)
    fx, fz = centre(fire)
    objects = [
        {"id": "spawn-1", "kind": "Spawn", "label": spawn.get("longName") or spawn.get("name"), "placement": placement(spawn["storey"], sx, sz)},
        {"id": "fire-1", "kind": "Hazard", "capability": {"id": "fire.source", "version": "1.0.0"},
         "parameters": {"intensity": 6, "activationSeconds": 5}, "placement": placement(fire["storey"], fx, fz)},
        {"id": "exit-door-1", "kind": "Equipment", "anchorId": door["ifcGlobalId"], "capability": {"id": "door.exit", "version": "1.0.0"},
         "placement": placement(ground, dx, dz)},
        {"id": "exit-1", "kind": "Goal", "label": "Cửa thoát", "capability": {"id": "exit.final", "version": "1.0.0"},
         "anchorId": door["ifcGlobalId"], "placement": placement(ground, gx, gz)},
    ]
    scenario = {
        "schemaVersion": "fet3d.editor/1", "name": f"Thoát nạn — {os.path.basename(os.path.normpath(args.out))}", "runtimeVersion": "2.0.0",
        "timeLimitSeconds": 300, "randomSeed": 7, "replanIntervalSeconds": 3,
        "modePolicy": {"allowedModes": ["Learn", "Guided", "Assessment"]},
        "learningObjectives": ["Thoát ra ngoài qua lối an toàn"],
        "learnerInstructions": "Có cháy trong nhà. Hãy thoát ra ngoài an toàn.",
        "objects": objects,
    }
    with open(os.path.join(args.out, "scenario.json"), "w", encoding="utf-8") as handle:
        json.dump(scenario, handle, ensure_ascii=False, indent=1)
    print(json.dumps({"spawn": [spawn.get("name"), spawn["storey"]], "fire": [fire.get("name"), fire["storey"]], "exitDoor": door["ifcGlobalId"],
                      "goal": [round(gx, 2), round(gz, 2)], "floors": list(floors)}, ensure_ascii=False))


if __name__ == "__main__":
    main()
