"""Positions of Stanton bodies/stations and the mapping from UEX shop terminals to positions."""
from __future__ import annotations

import json
import math
import pathlib
import re
from dataclasses import dataclass

DATA = pathlib.Path(__file__).resolve().parent.parent / "data" / "stanton.json"

# Names UEX uses that differ from the navigation data set.
ALIASES = {
    "green imperial housing exchange": "Grim HEX",
    "grimhex": "Grim HEX",
    "seraphim station": "Crusader",  # not in the data set; sits in Crusader orbit
    "port olisar": "Crusader",
}


@dataclass(frozen=True)
class Location:
    name: str          # display name (station/city/outpost or body)
    container: str     # body or lagrange point the position belongs to
    pos_km: tuple[float, float, float]
    kind: str          # "station" | "city" | "outpost" | "body"

    def distance_km(self, other: "Location") -> float:
        return math.dist(self.pos_km, other.pos_km)


def _norm(s: str) -> str:
    return re.sub(r"[^a-z0-9]+", " ", s.lower()).strip()


class Starmap:
    def __init__(self, path: pathlib.Path = DATA):
        raw = json.loads(path.read_text(encoding="utf-8"))
        self.containers: dict[str, dict] = raw["containers"]
        # normalized lookup: name -> (container, poi-or-None)
        self._index: dict[str, tuple[str, str | None]] = {}
        for cname, c in self.containers.items():
            self._index[_norm(cname)] = (cname, None)
            for p in c["pois"]:
                self._index.setdefault(_norm(p), (cname, p))

    def _resolve(self, name: str) -> tuple[str, str | None] | None:
        n = _norm(name)
        if n in ALIASES:
            n = _norm(ALIASES[n])
        if n in self._index:
            return self._index[n]
        # "HUR-L5 High Course Station" -> "HUR-L5"
        m = re.search(r"\b([a-z]{3}) ?l([1-5])\b", n)
        if m:
            key = f"{m.group(1)} l{m.group(2)}"
            if key in self._index:
                return self._index[key]
        # substring match on POIs (e.g. "R&R HUR-L5 High Course Station")
        for key, hit in self._index.items():
            if n and (n in key or key in n) and len(key) > 3:
                return hit
        return None

    def locate(self, name: str, kind: str = "body") -> Location | None:
        hit = self._resolve(name)
        if not hit:
            return None
        cname, poi = hit
        pos = tuple(self.containers[cname]["pos"])
        return Location(poi or cname, cname, pos, kind if poi else "body")

    def locate_terminal(self, term: dict) -> Location | None:
        """Best position for a UEX terminal record, most specific name first."""
        for field, kind in (("space_station_name", "station"), ("city_name", "city"),
                            ("outpost_name", "outpost"), ("moon_name", "body"),
                            ("orbit_name", "body"), ("planet_name", "body")):
            val = term.get(field)
            if val:
                loc = self.locate(val, kind)
                if loc:
                    return loc
        return None

    def names(self) -> list[str]:
        out = []
        for cname, c in self.containers.items():
            out.append(cname)
            out.extend(c["pois"])
        return out
