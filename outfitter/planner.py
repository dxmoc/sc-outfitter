"""One entry point for CLI and GUI: ship + goals + start -> loadout and trip."""
from __future__ import annotations

from dataclasses import dataclass

from . import api
from .catalog import Component, load_catalog
from .optimizer import Pick, budget_report, choose, totals
from .routing import QuantumDrive, Trip, plan_trip
from .ship import Ship, load_ship
from .starmap import Starmap

DEFAULT_GOALS = {"dps": 1.0, "damage": 1.0, "tank": 1.0, "regen": 0.5, "speed": 1.0}


@dataclass
class Plan:
    ship: Ship
    picks: list[Pick]
    trip: Trip
    budget: dict
    totals: dict
    quantum_drive: QuantumDrive
    goals: dict[str, float]

    def to_json(self) -> dict:
        t = self.trip
        return {
            "ship": self.ship.name,
            "goals": self.goals,
            "loadout": [{"slot": p.slot.port, "kind": p.component.kind, "size": p.slot.size,
                         "item": p.component.name, "grade": p.component.grade, "keep": p.keep,
                         "quantity": p.quantity, "stats": p.component.stats, "price": p.price}
                        for p in self.picks],
            "budget": self.budget,
            "totals": self.totals,
            "trip": {"start": t.start.name, "quantum_drive": self.quantum_drive.name,
                     "seconds": t.seconds, "fuel_units": t.fuel, "km": t.km, "cost": t.cost,
                     "stops": [{"location": s.location.name, "container": s.location.container,
                                "system": s.system, "planned": s.planned,
                                "leg_seconds": s.leg_seconds, "leg_fuel": s.leg_fuel, "leg_km": s.leg_km,
                                "buys": [{"item": b.item, "shop": b.shop, "quantity": b.quantity,
                                          "price": b.price} for b in s.buys]}
                               for s in t.stops],
                     "unavailable": t.unavailable},
        }


def _trip_drive(picks: list[Pick], catalog: dict[str, list[Component]], ship: Ship,
                use_planned: bool) -> QuantumDrive:
    qds = {c.name: c for c in catalog.get("quantum_drive", [])}
    planned = next((p.component for p in picks if p.component.kind == "quantum_drive"), None)
    current = qds.get(ship.default_quantum_drive or "")
    comp = planned if (use_planned and planned) else (current or planned)
    if comp is None:
        raise LookupError("no quantum drive data for this ship")
    return QuantumDrive.from_component(comp)


def make_plan(ship_name: str, start_name: str, goals: dict[str, float] | None = None, *,
              gimbal: bool = False, turrets: bool = False, max_grade: str | None = None,
              replace_all: bool = False, plan_with_new_qd: bool = False,
              auec_per_minute: float = 0.0, max_stops: int = 5) -> Plan:
    goals = goals or dict(DEFAULT_GOALS)
    starmap = Starmap()
    start = starmap.locate(start_name, "station")
    if not start:
        raise LookupError(f"unknown start location {start_name!r}")
    ship = load_ship(ship_name, fixed_guns=not gimbal, manned_turrets=turrets)
    catalog = load_catalog()
    picks = choose(ship, catalog, goals, keep_equal=not replace_all, max_grade=max_grade)
    terminals = api.uex_terminals()
    qd = _trip_drive(picks, catalog, ship, plan_with_new_qd)
    trip = plan_trip(picks, terminals, starmap, start, qd, auec_per_minute, max_stops)
    return Plan(ship, picks, trip, budget_report(ship, picks), totals(picks), qd, goals)


def start_locations() -> list[str]:
    """Sensible --start choices: stations, cities and bodies."""
    sm = Starmap()
    out = []
    for cname, c in sm.containers.items():
        out.append(cname)
        out.extend(p for p in c["pois"] if any(k in p for k in (
            "Station", "Harbor", "Point", "Tressler", "HEX", "Lorville", "Area 18", "New Babbage", "Orison")))
    return out
