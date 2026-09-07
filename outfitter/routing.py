"""Turn a shopping list into the shortest shopping trip through Stanton."""
from __future__ import annotations

import itertools
import math
from dataclasses import dataclass, field

from .catalog import Component
from .optimizer import Pick
from .starmap import Location, Starmap

# Fixed overhead per stop: approach after quantum exit, landing, walking to the shop, take-off.
STOP_OVERHEAD_S = {"station": 180.0, "city": 420.0, "outpost": 240.0, "body": 300.0}
CALIBRATION_S = 5.0


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
class Stop:
    location: Location
    buys: list[tuple[str, str, int]] = field(default_factory=list)  # (item, shop, price)
    leg_seconds: float = 0.0
    leg_fuel: float = 0.0
    leg_km: float = 0.0

    @property
    def cost(self) -> int:
        return sum(p for _, _, p in self.buys)


@dataclass
class Trip:
    start: Location
    stops: list[Stop]
    unavailable: list[str]

    @property
    def seconds(self) -> float:
        return sum(s.leg_seconds for s in self.stops)

    @property
    def fuel(self) -> float:
        return sum(s.leg_fuel for s in self.stops)

    @property
    def km(self) -> float:
        return sum(s.leg_km for s in self.stops)

    @property
    def cost(self) -> int:
        return sum(s.cost for s in self.stops)


def _shop_options(picks: list[Pick], terminals: dict[int, dict], starmap: Starmap
                  ) -> tuple[dict[str, dict[str, tuple[Location, str, int]]], list[str]]:
    """{item: {location_name: (Location, shop, price)}} using the cheapest shop per location."""
    options: dict[str, dict[str, tuple[Location, str, int]]] = {}
    unavailable = []
    for p in picks:
        if p.keep:
            continue
        name = p.component.name
        if name in options:
            continue
        per_loc: dict[str, tuple[Location, str, int]] = {}
        for o in p.component.offers:
            term = terminals.get(o.terminal_id)
            if not term or term.get("star_system_name") != "Stanton":
                continue
            loc = starmap.locate_terminal(term)
            if not loc:
                continue
            if loc.name not in per_loc or o.price < per_loc[loc.name][2]:
                per_loc[loc.name] = (loc, term.get("name") or o.terminal_name, o.price)
        if per_loc:
            options[name] = per_loc
        else:
            unavailable.append(name)
    return options, unavailable


def plan_trip(picks: list[Pick], terminals: dict[int, dict], starmap: Starmap, start: Location,
              qd: QuantumDrive, auec_per_minute: float = 0.0, max_stops: int = 5) -> Trip:
    """Pick the set of shops + visiting order that minimizes travel time (+ optional price weight).

    Small search space (a dozen shop locations, a handful of items), so we enumerate location
    subsets up to max_stops and all visiting orders. Every unit is bought once per slot.
    """
    options, unavailable = _shop_options(picks, terminals, starmap)
    # how many of each item we need (one per non-kept slot)
    needed: dict[str, int] = {}
    for p in picks:
        if not p.keep:
            needed[p.component.name] = needed.get(p.component.name, 0) + 1
    needed = {k: v for k, v in needed.items() if k in options}
    if not needed:
        return Trip(start, [], unavailable)

    locations: dict[str, Location] = {}
    for per_loc in options.values():
        for loc_name, (loc, _, _) in per_loc.items():
            locations[loc_name] = loc

    def leg(a: Location, b: Location) -> tuple[float, float, float]:
        d_m = a.distance_km(b) * 1000
        secs, fuel = qd.hop(d_m)
        return secs + STOP_OVERHEAD_S.get(b.kind, 300.0), fuel, d_m / 1000

    best: tuple[float, list[Stop]] | None = None
    names = sorted(locations)
    for n in range(1, min(max_stops, len(names)) + 1):
        for subset in itertools.combinations(names, n):
            # every needed item must be sold somewhere in the subset
            if not all(any(l in options[item] for l in subset) for item in needed):
                continue
            # assign each item to the cheapest shop within the subset
            buys: dict[str, list[tuple[str, str, int]]] = {l: [] for l in subset}
            price_total = 0
            for item, count in needed.items():
                l = min((l for l in subset if l in options[item]), key=lambda l: options[item][l][2])
                _, shop, price = options[item][l]
                buys[l].append((item, shop, price * count))
                price_total += price * count
            # drop locations that ended up buying nothing (a smaller subset covers that already)
            if any(not b for b in buys.values()):
                continue
            for order in itertools.permutations(subset):
                stops: list[Stop] = []
                prev = start
                total_s = 0.0
                for l in order:
                    loc = locations[l]
                    secs, fuel, km = leg(prev, loc)
                    stops.append(Stop(loc, buys[l], secs, fuel, km))
                    total_s += secs
                    prev = loc
                # price is converted into minutes when the user values their time
                objective = total_s + (price_total / auec_per_minute * 60 if auec_per_minute else 0)
                if best is None or objective < best[0]:
                    best = (objective, stops)
    return Trip(start, best[1] if best else [], unavailable)


def fmt_duration(seconds: float) -> str:
    m, s = divmod(int(round(seconds)), 60)
    h, m = divmod(m, 60)
    return f"{h}h {m:02d}m" if h else f"{m}m {s:02d}s"
