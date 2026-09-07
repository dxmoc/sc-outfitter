"""Convert Valalol/Star-Citizen-Navigation Database.json into data/stanton.json.

Source: https://github.com/Valalol/Star-Citizen-Navigation (MIT).
Only positions are kept (km, Stanton system frame). POIs are stored with their
parent container; local offsets are dropped because they are negligible against
interplanetary distances (and surface POIs rotate with the planet anyway).
"""
import json
import pathlib

src = pathlib.Path(__file__).with_name("valalol_database.json")
dst = pathlib.Path(__file__).resolve().parent.parent / "data" / "stanton.json"

db = json.loads(src.read_text(encoding="utf-8"))["Containers"]
out = {"system": "Stanton", "unit": "km", "containers": {}}
for name, c in db.items():
    if name == "Stanton":
        continue
    pois = [p for p, v in c["POI"].items()
            if not p.startswith("OM-") and str(v.get("QTMarker", "")).upper() == "TRUE"]
    out["containers"][name] = {
        "pos": [c["X"], c["Y"], c["Z"]],
        "radius_km": c.get("Body Radius", 0.0),
        "pois": sorted(pois),
    }
dst.write_text(json.dumps(out, indent=1), encoding="utf-8")
print(f"wrote {dst} with {len(out['containers'])} containers")
