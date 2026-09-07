"""Turn a shopping list into the shortest shopping trip through Stanton, Pyro and Nyx."""
from __future__ import annotations

import itertools
import math
from dataclasses import dataclass, field

from .catalog import Component
from .optimizer import Pick
from .starmap import Location, Starmap

# Fixed overhead per stop: approach after quantum exit, landing, walking to the shop, take-off.
STOP_OVERHEAD_S = {"station": 180.0, "city": 420.0, "outpost": 240.0, "body": 300.0, "gateway": 180.0}
CALIBRATION_S = 5.0
JUMP_S = 120.0  # line up, enter and traverse a jump point


@dataclass
class QuantumDrive:
    name: str
    speed: float      # m/s
    accel: float      # m/s^2 (stage two)
    spool: float      # s
    cooldown: float   # s
    fuel_rate: float  # fuel units per metre

    @classmethod
    def from_component(cls, c: Component) -> "QuantumDrive":
        s = c.stats
        return cls(c.name, s["speed"], s["accel"] or s["speed"] / 10, s["spool"], s["cooldown"], s["fuel_rate"])

    def hop(self, distance_m: float) -> tuple[float, float]:
        """(seconds, fuel units) for one quantum jump of distance_m."""
        if distance_m <= 0:
            return 0.0, 0.0
        d_ramp = self.speed ** 2 / (2 * self.accel)  # distance to reach full speed
        if distance_m >= 2 * d_ramp:
            travel = 2 * self.speed / self.accel + (distance_m - 2 * d_ramp) / self.speed
        else:
            travel = 2 * math.sqrt(distance_m / self.accel)
        return self.spool + CALIBRATION_S + travel + self.cooldown, distance_m * self.fuel_rate


@dataclass
class Buy:
    item: str
    shop: str
    quantity: int
    price: int  # total for the quantity


@dataclass
class Stop:
    location: Location
    buys: list[Buy] = field(default_factory=list)
    leg_seconds: float = 0.0
    leg_fuel: float = 0.0
    leg_km: float = 0.0
    system: str = "Stanton"
    planned: bool = True  # False: not in the coordinate data, listed without distance/order
    jumps: int = 0        # system jumps on the way here

    @property
    def cost(self) -> int:
        return sum(b.price for b in self.buys)


@dataclass
class Trip:
    start: Location
    stops: list[Stop]
    unavailable: list[str]  # items not sold anywhere at all

    @property
    def planned(self) -> list[Stop]:
        return [s for s in self.stops if s.planned]

    @property
    def extra(self) -> list[Stop]:
        return [s for s in self.stops if not s.planned]

    @property
    def seconds(self) -> float:
        return sum(s.leg_seconds for s in self.planned)

    @property
    def fuel(self) -> float:
        return sum(s.leg_fuel for s in self.planned)

    @property
    def km(self) -> float:
        return sum(s.leg_km for s in self.planned)

    @property
    def cost(self) -> int:
        return sum(s.cost for s in self.stops)


def _terminal_place(term: dict) -> str:
    for f in ("space_station_name", "city_name", "outpost_name", "moon_name", "planet_name", "orbit_name"):
        if term.get(f):
            return term[f]
    return term.get("name") or "?"


def _shop_options(picks: list[Pick], terminals: dict[int, dict], starmap: Starmap):
    """Per needed item: shops we can place on the map and shops we can only name.

    Returns (needed, mapped, unmapped, unavailable):
      needed   {item: quantity}
      mapped   {item: {location_name: (Location, shop, unit price)}}
      unmapped {item: {(system, place): (shop, unit price)}}
    """
    needed: dict[str, int] = {}
    comps: dict[str, Component] = {}
    for p in picks:
        if p.keep:
            continue
        needed[p.component.name] = needed.get(p.component.name, 0) + p.quantity
        comps[p.component.name] = p.component
    mapped: dict[str, dict[str, tuple[Location, str, int]]] = {}
    unmapped: dict[str, dict[tuple[str, str], tuple[str, int]]] = {}
    unavailable = []
    for name, comp in comps.items():
        per_loc: dict[str, tuple[Location, str, int]] = {}
        per_place: dict[tuple[str, str], tuple[str, int]] = {}
        for o in comp.offers:
            term = terminals.get(o.terminal_id)
            if not term:
                continue
            shop = term.get("name") or o.terminal_name
            loc = starmap.locate_terminal(term)
            if loc:
                key = f"{loc.system}/{loc.name}"  # gateway names repeat across systems
                if key not in per_loc or o.price < per_loc[key][2]:
                    per_loc[key] = (loc, shop, o.price)
            else:
                key = (term.get("star_system_name") or "?", _terminal_place(term))
                if key not in per_place or o.price < per_place[key][1]:
                    per_place[key] = (shop, o.price)
        if per_loc:
            mapped[name] = per_loc
        elif per_place:
            unmapped[name] = per_place
        else:
            unavailable.append(name)
    return needed, mapped, unmapped, unavailable


def plan_trip(picks: list[Pick], terminals: dict[int, dict], starmap: Starmap, start: Location,
              qd: QuantumDrive, auec_per_minute: float = 0.0, max_stops: int = 5) -> Trip:
    """Pick the set of shops + visiting order that minimizes travel time (+ optional price weight).

    Small search space (a dozen shop locations, a handful of items), so we enumerate location
    subsets up to max_stops and all visiting orders. Items only sold at places missing from the
    coordinate data are appended as unordered extra stops.
    """
    needed, mapped, unmapped, unavailable = _shop_options(picks, terminals, starmap)
    stops: list[Stop] = []

    locations: dict[str, Location] = {}
    for per_loc in mapped.values():
        for loc_name, (loc, _, _) in per_loc.items():
            locations[loc_name] = loc

    def leg(a: Location, b: Location) -> tuple[float, float, float, int]:
        """(seconds, fuel, km, jumps) from a to b, through gateways if they are in different systems."""
        secs = fuel = km = 0.0
        jumps = 0
        for x, y, is_jump in starmap.path(a, b):
            if is_jump:
                secs += JUMP_S
                jumps += 1
                continue
            d_m = x.distance_km(y) * 1000
            s, f = qd.hop(d_m)
            secs, fuel, km = secs + s, fuel + f, km + d_m / 1000
        return secs + STOP_OVERHEAD_S.get(b.kind, 300.0), fuel, km, jumps

    best: tuple[float, list[Stop]] | None = None
    names = sorted(locations)
    for n in range(1, min(max_stops, len(names)) + 1):
        for subset in itertools.combinations(names, n):
            if not all(any(l in mapped[item] for l in subset) for item in mapped):
                continue
            buys: dict[str, list[Buy]] = {l: [] for l in subset}
            price_total = 0
            for item in mapped:
                l = min((l for l in subset if l in mapped[item]), key=lambda l: mapped[item][l][2])
                _, shop, price = mapped[item][l]
                qty = needed[item]
                buys[l].append(Buy(item, shop, qty, price * qty))
                price_total += price * qty
            if any(not b for b in buys.values()):
                continue  # a smaller subset covers the same shops
            for order in itertools.permutations(subset):
                route: list[Stop] = []
                prev = start
                total_s = 0.0
                for l in order:
                    loc = locations[l]
                    secs, fuel, km, jumps = leg(prev, loc)
                    route.append(Stop(loc, buys[l], secs, fuel, km, loc.system, True, jumps))
                    total_s += secs
                    prev = loc
                # price is converted into minutes when the user values their time
                objective = total_s + (price_total / auec_per_minute * 60 if auec_per_minute else 0)
                if best is None or objective < best[0]:
                    best = (objective, route)
    if best:
        stops.extend(best[1])

    # items only sold at places missing from the coordinate data: cheapest place per item
    extra: dict[tuple[str, str], Stop] = {}
    for item, places in unmapped.items():
        (system, place), (shop, price) = min(places.items(), key=lambda kv: kv[1][1])
        stop = extra.setdefault((system, place), Stop(Location(place, system, place, (0, 0, 0), "station"),
                                                      system=system, planned=False))
        qty = needed[item]
        stop.buys.append(Buy(item, shop, qty, price * qty))
    stops.extend(extra.values())
    return Trip(start, stops, unavailable)


def fmt_duration(seconds: float) -> str:
    m, s = divmod(int(round(seconds)), 60)
    h, m = divmod(m, 60)
    return f"{h}h {m:02d}m" if h else f"{m}m {s:02d}s"
