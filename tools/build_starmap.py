"""Convert scunpacked-data's starmap_positions.json into data/starmap.json.

Source: https://github.com/StarCitizenWiki/scunpacked-data (extracted game data), file
starmap_positions.json. Positions are in metres in the star's frame; we keep km and only the
entities a pilot can pick or a shop can sit at: stars, planets, moons, stations, landing
zones, gateways and visible outposts. Gateways are paired by name: "Pyro Gateway" in Stanton
connects to "Stanton Gateway" in Pyro.
"""
import json
import pathlib

KEEP = {
    "Star": "body", "Planet": "body", "Moon": "body",
    "Manmade": "station", "Manmade_VisibleOnInteraction": "station",
    "LandingZone": "city", "Outpost": "outpost",
}

src = pathlib.Path(__file__).with_name("scunpacked_starmap_positions.json")
dst = pathlib.Path(__file__).resolve().parent.parent / "src" / "ScOutfitter.Core" / "Data" / "starmap.json"

raw = json.loads(src.read_text(encoding="utf-8"))
ents = raw["entities"]
by_uuid = {e["uuid"]: e for e in ents}

systems: dict[str, list[dict]] = {}
for e in ents:
    if e["hidden"] or e["type"] not in KEEP or not e.get("name"):
        continue
    kind = KEEP[e["type"]]
    if kind == "station" and e["name"].startswith("Comm Array"):
        continue
    if e["name"].endswith(" Gateway"):
        kind = "gateway"
    parent = by_uuid.get(e["parent_uuid"] or "", {}).get("name")
    system = e["system"].capitalize()
    systems.setdefault(system, []).append({
        "name": e["name"], "kind": kind, "parent": parent,
        "pos": [round(e["x"] / 1000, 1), round(e["y"] / 1000, 1), round(e["z"] / 1000, 1)],
    })

seen = set()
out = {"unit": "km", "systems": {}}
for system, entries in sorted(systems.items()):
    uniq = []
    for x in sorted(entries, key=lambda x: (x["kind"], x["name"])):
        if (system, x["name"]) in seen:
            continue
        seen.add((system, x["name"]))
        uniq.append(x)
    out["systems"][system] = uniq

# gateway pairs: (system A, "B Gateway") <-> (system B, "A Gateway")
pairs = []
for system, entries in out["systems"].items():
    for x in entries:
        if x["kind"] != "gateway":
            continue
        other = x["name"][: -len(" Gateway")]
        if other in out["systems"] and any(y["name"] == f"{system} Gateway" for y in out["systems"][other]):
            pairs.append({"from_system": system, "from": x["name"], "to_system": other, "to": f"{system} Gateway"})
out["gateways"] = pairs

dst.write_text(json.dumps(out, indent=1), encoding="utf-8")
print({s: len(v) for s, v in out["systems"].items()}, len(pairs), "gateway links")
