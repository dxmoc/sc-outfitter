"""Pick the best purchasable component for every slot according to a profile."""
from __future__ import annotations

from dataclasses import dataclass

from .catalog import Component
from .ship import Ship, Slot

# Profile weights. Each scorer returns a number; higher is better.
PROFILES = {
    # all-round fighter: raw dps, shield pool with some regen, fast quantum
    "combat": {"gun": ("dps", 1.0, "range", 0.1), "shield": ("hp", 1.0, "regen", 3.0),
               "quantum_drive": ("speed", 1.0)},
    # sustained brawl: regen matters more, prefer energy weapons (no rearming)
    "brawl": {"gun": ("dps", 1.0, "energy", 150.0), "shield": ("hp", 1.0, "regen", 8.0),
              "quantum_drive": ("speed", 1.0)},
    # get around fast: fastest quantum drive, rest as combat
    "travel": {"gun": ("dps", 1.0), "shield": ("hp", 1.0, "regen", 3.0),
               "quantum_drive": ("speed", 1.0, "spool", -2e6)},
    # cheapest fuel per Gm, rest as combat
    "economy": {"gun": ("dps", 1.0), "shield": ("hp", 1.0, "regen", 3.0),
                "quantum_drive": ("efficiency", 1.0)},
}


def score(comp: Component, profile: str) -> float:
    weights = PROFILES[profile].get(comp.kind)
    s = comp.stats
    if comp.kind == "power_plant":
        return s["power"]
    if comp.kind == "cooler":
        return s["coolant"]
    if not weights:
        return 0.0
    total = 0.0
    for key, w in zip(weights[::2], weights[1::2]):
        if key == "energy":
            total += w * (0 if s.get("ammo") else 1)
        elif key == "efficiency":
            total += w * (1.0 / s["fuel_rate"] if s.get("fuel_rate") else 0)
        else:
            total += w * s.get(key, 0.0)
    return total


@dataclass
class Pick:
    slot: Slot
    component: Component
    keep: bool  # already equipped, nothing to buy

    @property
    def price(self) -> int:
        return 0 if self.keep else (self.component.cheapest or 0)


def _candidates(comps: list[Component], slot: Slot, exact_size: bool) -> list[Component]:
    if exact_size:
        return [c for c in comps if c.size == slot.size and c.buyable]
    return [c for c in comps if c.size <= slot.size and c.buyable]


def choose(ship: Ship, catalog: dict[str, list[Component]], profile: str = "combat",
           keep_equal: bool = True, max_grade: str | None = None) -> list[Pick]:
    """One Pick per slot. Guns may be smaller than the hardpoint; everything else must match."""
    picks: list[Pick] = []
    by_name = {c.name: c for comps in catalog.values() for c in comps}
    for slot in ship.slots:
        comps = catalog.get(slot.kind, [])
        cands = _candidates(comps, slot, exact_size=slot.kind != "gun")
        if max_grade:
            cands = [c for c in cands if c.grade <= max_grade] or cands
        if not cands:
            continue
        best = max(cands, key=lambda c: (score(c, profile), -(c.cheapest or 0)))
        current = by_name.get(slot.equipped or "")
        keep = False
        if current is not None and keep_equal and score(current, profile) >= score(best, profile):
            best, keep = current, True
        elif current is not None and current.name == best.name:
            keep = True
        picks.append(Pick(slot, best, keep))
    return picks


def budget_report(ship: Ship, picks: list[Pick]) -> dict:
    """Power/cooling demand of the chosen loadout vs. what plants/coolers produce."""
    power_gen = sum(p.component.stats["power"] for p in picks if p.slot.kind == "power_plant")
    cool_gen = sum(p.component.stats["coolant"] for p in picks if p.slot.kind == "cooler")
    power_use = sum(p.component.power_draw for p in picks if p.slot.kind != "power_plant")
    cool_use = sum(p.component.coolant_draw for p in picks if p.slot.kind != "cooler")
    return {"power_generation": power_gen or ship.power_generation, "power_usage": power_use,
            "cooling_generation": cool_gen or ship.cooling_generation, "cooling_usage": cool_use}
