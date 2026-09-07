"""Positions of bodies, stations and outposts in Stanton, Pyro and Nyx; name lookup; system hops."""
from __future__ import annotations

import json
import math
import pathlib
import re
from dataclasses import dataclass

DATA = pathlib.Path(__file__).resolve().parent.parent / "data" / "starmap.json"

# Names UEX uses that differ from the game data.
ALIASES = {
    "green imperial housing exchange": "Grim HEX",
    "grimhex": "Grim HEX",
    "area 18": "Area18",
    "port olisar": "Crusader",
    "checkmate station": "Checkmate",
}
KIND_ORDER = {"station": 0, "city": 0, "gateway": 1, "outpost": 2, "body": 3}


@dataclass(frozen=True)
class Location:
    name: str
    system: str
    container: str     # parent body (or the star for deep-space stations)
    pos_km: tuple[float, float, float]
    kind: str          # station | city | outpost | gateway | body

    def distance_km(self, other: "Location") -> float:
        return math.dist(self.pos_km, other.pos_km)

    @property
    def label(self) -> str:
        if self.name == self.container or self.container == self.system:
            return self.name
        return f"{self.name} ({self.container})"


def _norm(s: str) -> str:
    s = re.sub(r"\s*\((stanton|pyro|nyx)( system)?\)\s*$", "", s.lower())  # "Pyro Gateway (Stanton)"
    return re.sub(r"[^a-z0-9]+", " ", s).strip()


class Starmap:
    def __init__(self, path: pathlib.Path = DATA):
        raw = json.loads(path.read_text(encoding="utf-8"))
        self.locations: dict[str, list[Location]] = {}
        self._index: dict[str, dict[str, Location]] = {}  # system -> normalized name -> Location
        for system, entries in raw["systems"].items():
            locs = []
            for e in entries:
                loc = Location(e["name"], system, e["parent"] or e["name"], tuple(e["pos"]), e["kind"])
                locs.append(loc)
                self._index.setdefault(system, {})[_norm(e["name"])] = loc
            self.locations[system] = locs
        self.gateways: dict[tuple[str, str], Location] = {}  # (from system, to system) -> gateway in from
        self._gate_exit: dict[tuple[str, str], Location] = {}  # (from system, to system) -> arrival in to
        for g in raw["gateways"]:
            self.gateways[(g["from_system"], g["to_system"])] = self._index[g["from_system"]][_norm(g["from"])]
            self._gate_exit[(g["from_system"], g["to_system"])] = self._index[g["to_system"]][_norm(g["to"])]

    # ---------- browsing ----------
    def systems(self) -> list[str]:
        return ["Stanton", "Pyro", "Nyx"] + sorted(s for s in self.locations if s not in ("Stanton", "Pyro", "Nyx"))

    def bodies(self, system: str) -> list[str]:
        """Planets and moons of a system plus the star itself (for deep-space stations)."""
        star = next((l.name for l in self.locations.get(system, []) if l.name == l.container and l.kind == "body"), system)
        planets = [l for l in self.locations.get(system, []) if l.kind == "body" and l.container == star and l.name != star]
        out = []
        for p in sorted(planets, key=lambda l: l.name):
            out.append(p.name)
            out.extend(sorted(l.name for l in self.locations[system] if l.kind == "body" and l.container == p.name))
        out.append(f"{star} (deep space)")
        return out

    def places(self, system: str, body: str, include_outposts: bool = False) -> list[str]:
        """Stations/cities (and optionally outposts) around a body, best-known first."""
        body = body.replace(" (deep space)", "")
        kinds = {"station", "city", "gateway"} | ({"outpost"} if include_outposts else set())
        hits = [l for l in self.locations.get(system, []) if l.container == body and l.kind in kinds]
        return [l.name for l in sorted(hits, key=lambda l: (KIND_ORDER[l.kind], l.name))]

    # ---------- lookup ----------
    def locate(self, name: str, system: str | None = None) -> Location | None:
        """Find by name. 'Pyro/Checkmate' pins the system; otherwise Stanton is searched first."""
        if "/" in name and system is None:
            system, _, name = name.partition("/")
            system = system.strip().capitalize()
        n = _norm(name)
        n = _norm(ALIASES.get(n, name))
        order = [system] if system else self.systems()
        for sysname in order:
            idx = self._index.get(sysname, {})
            if n in idx:
                return idx[n]
        for sysname in order:  # "R&R HUR-L5 High Course Station" vs "HUR-L5 High Course Station"
            for key, loc in self._index.get(sysname, {}).items():
                if len(key) > 3 and (n in key or key in n):
                    return loc
        return None

    def locate_terminal(self, term: dict) -> Location | None:
        """Best position for a UEX terminal record, most specific name first."""
        system = (term.get("star_system_name") or "").capitalize()
        for field in ("space_station_name", "city_name", "outpost_name", "moon_name", "planet_name", "orbit_name"):
            val = term.get(field)
            if val:
                loc = self.locate(val, system or None)
                if loc:
                    return loc
        return None

    # ---------- travel ----------
    def path(self, a: Location, b: Location) -> list[tuple[Location, Location, bool]]:
        """Quantum legs from a to b as (from, to, is_jump). Cross-system goes through gateways."""
        if a.system == b.system:
            return [(a, b, False)]
        chain = self._system_chain(a.system, b.system)
        if not chain:
            return [(a, b, False)]
        legs = []
        cur = a
        for s_from, s_to in zip(chain, chain[1:]):
            gate = self.gateways[(s_from, s_to)]
            legs.append((cur, gate, False))
            arrive = self._gate_exit[(s_from, s_to)]
            legs.append((gate, arrive, True))
            cur = arrive
        legs.append((cur, b, False))
        return legs

    def _system_chain(self, start: str, goal: str) -> list[str] | None:
        frontier = [[start]]
        seen = {start}
        while frontier:
            chain = frontier.pop(0)
            if chain[-1] == goal:
                return chain
            for (s_from, s_to) in self.gateways:
                if s_from == chain[-1] and s_to not in seen:
                    seen.add(s_to)
                    frontier.append(chain + [s_to])
        return None
