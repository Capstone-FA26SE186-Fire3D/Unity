"""IFC class/name rules: what is exported, which library material it gets, which runtime capability it suggests."""
from __future__ import annotations

import re

# Never part of the runtime geometry (openings are already cut, spaces/site live in metadata only).
SKIP_CLASSES = (
    "IfcOpeningElement", "IfcSpace", "IfcSite", "IfcAnnotation", "IfcVirtualElement", "IfcGrid", "IfcBuilding",
    "IfcBuildingStorey", "IfcProject", "IfcSpatialZone", "IfcExternalSpatialElement",
    "IfcDuct", "IfcPipe", "IfcCableCarrier", "IfcCableSegment", "IfcFlowSegment", "IfcFlowFitting",
    "IfcDistributionPort", "IfcFastener", "IfcMechanicalFastener", "IfcReinforcingBar", "IfcReinforcingMesh", "IfcTendon",
)

# Elements whose missing Body blocks training (walkable shell); the rest only warn.
STRUCTURAL_CLASSES = ("IfcWall", "IfcSlab", "IfcDoor", "IfcStair", "IfcRamp", "IfcRoof", "IfcCovering", "IfcCurtainWall")

STAIR_CLASSES = ("IfcStair", "IfcStairFlight", "IfcRamp", "IfcRampFlight")

# keyword (regex on lowercase IFC material/style names) -> libraryKey of Unity Art/MaterialLibrary; first match wins.
MATERIAL_RULES = [
    (r"deckenraster|ceiling|himling|akustik|acoustic|decke -|trần", "ceiling.tile"),
    (r"glas|glass|glazing|vindu|kính", "glass.clear"),
    (r"epoxy|epoxid|polished concrete|beschichtung|sàn epoxy|bê tông mài", "floor.epoxy"),
    (r"vinyl|linoleum|linol|pvc|sàn nhựa|kautschuk", "floor.vinyl"),
    (r"terrazzo|granito|đá mài", "floor.terrazzo"),
    (r"rubber|gummi|cao su|anti.?slip|chống trơn", "floor.rubber"),
    (r"(weiß|weiss|white|trắng).*(fliese|tile)|(fliese|tile|gạch ốp).*(weiß|weiss|white|trắng)", "wall.tile.white"),
    (r"fliese|tile|flis|ceramic|keramik|gạch men", "floor.tile"),
    (r"holzdiel|dielen|parkett|wood ?floor|laminat|floor.*wood|sàn gỗ", "floor.wood"),
    (r"teppich|carpet|tepp|thảm", "floor.carpet"),
    (r"ziegel|brick|masonry|tegl|mauerwerk|gạch", "wall.brick"),
    (r"beton|concrete|stb|betong|estrich|screed|bê tông", "concrete"),
    (r"stainless|edelstahl|inox|nirosta", "metal.stainless"),
    (r"chrom|alumin", "metal.brushed"),
    (r"stahl|steel|metal|metall|st[aå]l|ral \d|thép", "metal.painted"),
    (r"holz|wood|eiche|esche|kiefer|oak|ash|pine|\btre\b|finer|gỗ", "wood"),
    (r"naturstein|marble|granit|sandstein|stone|stein|đá", "stone"),
    (r"paint|farbe|anstrich|maling|sơn", "wall.paint"),
    (r"verputz|plaster|gips|gypsum|drywall|plasterboard|rigips|malt|vữa", "wall.plaster"),
]

CLASS_MATERIAL = {
    "IfcSlab": "floor.tile", "IfcCovering": "ceiling.tile", "IfcWall": "wall.plaster", "IfcWallStandardCase": "wall.plaster",
    "IfcWindow": "glass.clear", "IfcDoor": "wood", "IfcStair": "concrete", "IfcStairFlight": "concrete", "IfcRamp": "concrete",
    "IfcRampFlight": "concrete", "IfcRailing": "metal.painted", "IfcColumn": "concrete", "IfcBeam": "concrete",
    "IfcRoof": "concrete", "IfcCurtainWall": "glass.clear", "IfcPlate": "glass.clear", "IfcMember": "metal.painted",
    "IfcFooting": "concrete", "IfcFurnishingElement": "wood", "IfcFurniture": "wood",
}
FALLBACK_MATERIAL = "wall.plaster"

# Preview colours (linear RGBA) per libraryKey so the GLB is readable before Unity swaps in library materials.
PREVIEW_COLOURS = {
    "ceiling.tile": (0.86, 0.86, 0.84, 1.0), "glass.clear": (0.62, 0.78, 0.86, 0.35), "glass.frosted": (0.8, 0.85, 0.88, 0.6),
    "floor.tile": (0.62, 0.6, 0.57, 1.0), "floor.wood": (0.52, 0.36, 0.22, 1.0), "floor.carpet": (0.36, 0.38, 0.42, 1.0),
    "wall.brick": (0.55, 0.3, 0.22, 1.0), "wall.plaster": (0.9, 0.88, 0.84, 1.0), "wall.tile": (0.85, 0.87, 0.88, 1.0),
    "concrete": (0.6, 0.6, 0.58, 1.0), "metal.brushed": (0.75, 0.76, 0.78, 1.0), "metal.painted": (0.35, 0.37, 0.4, 1.0),
    "wood": (0.58, 0.4, 0.25, 1.0), "stone": (0.78, 0.76, 0.72, 1.0),
    "floor.epoxy": (0.55, 0.56, 0.55, 1.0), "floor.vinyl": (0.62, 0.63, 0.64, 1.0), "floor.terrazzo": (0.74, 0.72, 0.68, 1.0),
    "floor.rubber": (0.18, 0.18, 0.19, 1.0), "wall.tile.white": (0.93, 0.93, 0.92, 1.0), "wall.paint": (0.88, 0.87, 0.83, 1.0),
    "metal.stainless": (0.78, 0.79, 0.8, 1.0),
}

HIDDEN_LAYER = re.compile(r"stud|air|insulation|d(ä|a)mm|membrane|vapou?r|misc|leer|empty|isolasjon|luft", re.I)
MASS = re.compile(r"\bmass\b|massing", re.I)


FLOOR_CLASSES = ("IfcSlab", "IfcCovering", "IfcStair", "IfcStairFlight", "IfcRamp", "IfcRampFlight")
# A floor finish named on a non-floor element (door leaf laminate, wall tiles) maps to the matching non-floor key.
NON_FLOOR_EQUIVALENT = {
    "floor.tile": "wall.tile", "floor.wood": "wood", "floor.carpet": None, "floor.epoxy": "concrete", "floor.vinyl": None,
    "floor.terrazzo": "stone", "floor.rubber": None,
}


def library_key_for(names: list[str], ifc_class: str, predefined: str | None) -> tuple[str, bool]:
    """Returns (libraryKey, mapped_from_name). Roof slabs become concrete regardless of name."""
    for name in names:
        lowered = (name or "").lower()
        for pattern, key in MATERIAL_RULES:
            if re.search(pattern, lowered):
                if key == "wall.tile.white" and ifc_class in FLOOR_CLASSES:
                    key = "floor.tile"
                if key.startswith("floor.") and ifc_class not in FLOOR_CLASSES:
                    key = NON_FLOOR_EQUIVALENT[key]
                    if key is None:
                        continue
                return key, True
    if ifc_class == "IfcSlab" and predefined == "ROOF":
        return "concrete", False
    for prefix, key in CLASS_MATERIAL.items():
        if ifc_class == prefix:
            return key, False
    return FALLBACK_MATERIAL, False


# (regex on "Name ObjectType TypeName" lowercase, catalogKey, confidence) used when the IFC class itself is generic.
EQUIPMENT_NAME_RULES = [
    (r"extinguisher|feuerl(ö|oe)scher|brannslukker|bình chữa cháy|\bco2\b|fire ?ext", "extinguisher.portable", 0.75),
    (r"smoke ?detector|rauchmelder|heat ?detector|đầu báo", "alarm.detector", 0.7),
    (r"call ?point|break ?glass|manual ?(call|pull)|druckknopfmelder|nút báo cháy", "alarm.call_point", 0.7),
    (r"sprinkler", "suppression.sprinkler", 0.75),
    (r"\bsink\b|wash ?basin|washbasin|lavatory|waschtisch|sp(ü|ue)le|lavabo|bồn rửa|chậu rửa", "water.sink", 0.7),
    (r"\bbath(tub)?\b|badewanne|bồn tắm", "water.bath", 0.7),
    (r"shower|dusche|vòi sen", "water.shower", 0.7),
    (r"elevator|\blift\b|aufzug|thang máy", "transport.elevator", 0.7),
]

PREDEFINED_EQUIPMENT = {
    ("IfcFireSuppressionTerminal", "SPRINKLER"): ("suppression.sprinkler", 0.95),
    ("IfcFireSuppressionTerminal", "SPRINKLERDEFLECTOR"): ("suppression.sprinkler", 0.9),
    ("IfcAlarm", "MANUALPULLBOX"): ("alarm.call_point", 0.95),
    ("IfcAlarm", "BREAKGLASSBUTTON"): ("alarm.call_point", 0.95),
    ("IfcSensor", "SMOKESENSOR"): ("alarm.detector", 0.95),
    ("IfcSensor", "HEATSENSOR"): ("alarm.detector", 0.95),
    ("IfcSanitaryTerminal", "SINK"): ("water.sink", 0.95),
    ("IfcSanitaryTerminal", "WASHHANDBASIN"): ("water.sink", 0.95),
    ("IfcSanitaryTerminal", "BATH"): ("water.bath", 0.95),
    ("IfcSanitaryTerminal", "SHOWER"): ("water.shower", 0.95),
    ("IfcTransportElement", "ELEVATOR"): ("transport.elevator", 0.95),
}

FIRE_KEYS = {"extinguisher.portable", "alarm.detector", "alarm.call_point", "suppression.sprinkler"}
WATER_KEYS = {"water.sink", "water.bath", "water.shower"}

# Classes worth scanning for equipment by name (doors are handled separately as portals).
EQUIPMENT_CLASSES = ("IfcFireSuppressionTerminal", "IfcAlarm", "IfcSensor", "IfcSanitaryTerminal", "IfcTransportElement",
                     "IfcFlowTerminal", "IfcBuildingElementProxy", "IfcFurnishingElement", "IfcFurniture", "IfcDistributionElement",
                     "IfcDistributionControlElement", "IfcEnergyConversionDevice", "IfcFlowController")


def equipment_for(ifc_class: str, predefined: str | None, text: str) -> tuple[str, float] | None:
    hit = PREDEFINED_EQUIPMENT.get((ifc_class, (predefined or "").upper()))
    if hit:
        return hit
    if ifc_class == "IfcFireSuppressionTerminal":
        if re.search(r"sprinkler", text):
            return "suppression.sprinkler", 0.9
        return "extinguisher.portable", 0.6
    lowered = text.lower()
    for pattern, key, confidence in EQUIPMENT_NAME_RULES:
        if re.search(pattern, lowered):
            return key, confidence
    return None


def space_category(name: str) -> str:
    lowered = (name or "").lower()
    rules = [
        (r"corridor|hallway|flur|gang|korridor|hall|foyer|lobby|entr|hành lang|sảnh", "corridor"),
        (r"stair|treppe|trapp|cầu thang", "stair"),
        (r"kitchen|küche|kjøkken|bếp", "kitchen"),
        (r"\bwc\b|toilet|bath|bad|dusch|restroom|vệ sinh|tắm", "wet"),
        (r"office|büro|kontor|văn phòng|phòng làm việc", "office"),
        (r"class|klasse|lecture|học", "classroom"),
        (r"store|storage|lager|abstell|kho", "storage"),
        (r"technical|tech|server|mechanical|elektro|kỹ thuật", "technical"),
        (r"outdoor|terrace|balcony|balkon|terrasse|ban công|sân", "outdoor"),
        (r"living|wohn|stue|bed|schlaf|soverom|khách|ngủ", "living"),
    ]
    for pattern, category in rules:
        if re.search(pattern, lowered):
            return category
    return "room"
