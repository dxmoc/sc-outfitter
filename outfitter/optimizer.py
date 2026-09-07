"""Pick the best purchasable component for every slot according to user-chosen goals.

Scoring: every goal maps to one stat per component kind. A component's score is the weighted
sum of its stats, each normalized by the best value among the candidates for that slot, so
goals with different units (dps vs. shield hp) can be mixed. Kinds that none of the chosen
goals touch fall back to a balanced score over all their stats.
"""
from __future__ import annotations

from dataclasses import dataclass

from .catalog import Component
from .ship import Ship, Slot

GOALS = {
    "dps": "gun damage per second",
    "damage": "missile payload (count x damage per rack)",
    "range": "gun and missile range",
    "tank": "shield hit points",
    "regen": "shield regeneration",
    "speed": "quantum drive speed",
    "fuel": "quantum fuel efficiency",
    "detection": "radar aim-assist range",
    "stealth": "low EM signature (radar)",
    "cheap": "prefer cheaper parts",
}

# kind -> {goal: stat key inside Component.stats}
KIND_GOALS = {
    "gun": {"dps": "dps", "range": "range"},
    "shield": {"tank": "hp", "regen": "regen"},
    "quantum_drive": {"speed": "speed", "fuel": "efficiency"},
    "radar": {"detection": "aim_range", "stealth": "low_em"},
    "missile": {"damage": "damage", "range": "range"},
    "missile_rack": {"damage": "payload", "range": "range"},
    "power_plant": {"_": "power"},
    "cooler": {"_": "coolant"},
}


def _stat(comp: Component, key: str) -> float:
    if key == "efficiency":
        fr = comp.stats.get("fuel_rate") or 0
        return 1.0 / fr if fr else 0.0
    if key == "low_em":
        em = comp.stats.get("em") or 0
        return 1.0 / em if em else 0.0
    return float(comp.stats.get(key) or 0.0)


def score(comp: Component, goals: dict[str, float], cands: list[Component]) -> float:
    """Weighted, normalized score of comp among cands (comp should be part of cands)."""
    mapping = KIND_GOALS.get(comp.kind, {})
    active = {g: w for g, w in goals.items() if g in mapping and w > 0}
    if not active:  # nothing chosen touches this kind -> balanced over all its stats
        active = {g: 1.0 for g in mapping}
    total = 0.0
    for goal, weight in active.items():
        key = mapping[goal]
        best = max((_stat(c, key) for c in cands), default=0.0)
        if best > 0:
            total += weight * _stat(comp, key) / best
    if goals.get("cheap", 0) > 0:
        prices = [c.cheapest or 0 for c in cands if c.buyable]
        top = max(prices, default=0)
        if top > 0:
            total += goals["cheap"] * (1 - (comp.cheapest or 0) / top)
    return total


@dataclass
class Pick:
    slot: Slot
    component: Component
    keep: bool          # already equipped, nothing to buy
    quantity: int = 1   # missiles: one per rack tube
    stock: str | None = None  # what the wiki says is fitted in this slot by default
    fixed: bool = False       # slot cannot be changed in game

    @property
    def price(self) -> int:
        return 0 if self.keep else (self.component.cheapest or 0) * self.quantity


def fits(comp: Component, slot: Slot) -> bool:
    """Bespoke ports (e.g. the Stingray's Merlin_Nose / Wolf_Gun) only take parts with those tags,
    and bespoke parts only go into ports that offer their tag."""
    if not slot.required_tags <= comp.tags:
        return False
    return comp.required_tags <= slot.port_tags


def _candidates(comps: list[Component], slot: Slot, exact: bool) -> list[Component]:
    size_ok = (lambda c: c.size == slot.size) if exact else (lambda c: c.size <= slot.size)
    return [c for c in comps if size_ok(c) and c.buyable and fits(c, slot)]


def _missile_candidates(missiles: list[Component], size: int) -> list[Component]:
    return [c for c in missiles if c.size == size and c.buyable and not c.required_tags]


def _best(cands: list[Component], goals: dict[str, float]) -> Component | None:
    if not cands:
        return None
    return max(cands, key=lambda c: (score(c, goals, cands), -(c.cheapest or 0)))


def _attach_rack_stats(racks: list[Component], missiles: list[Component], goals: dict[str, float]
                       ) -> dict[str, Component]:
    """Give every rack a payload/range stat from the best missile of its tube size.

    Returns {rack name: best missile} so the caller can add the missile picks.
    """
    best_missiles: dict[str, Component] = {}
    for rack in racks:
        cands = _missile_candidates(missiles, rack.stats["missile_size"])
        m = _best(cands, goals)
        if m is None:
            rack.stats["payload"] = rack.stats["range"] = 0.0
            continue
        best_missiles[rack.name] = m
        rack.stats["payload"] = rack.stats["count"] * m.stats["damage"]
        rack.stats["range"] = m.stats["range"]
    return best_missiles


def choose(ship: Ship, catalog: dict[str, list[Component]], goals: dict[str, float],
           keep_equal: bool = True, max_grade: str | None = None, trust_stock: bool = True) -> list[Pick]:
    """One Pick per slot (plus one per missile rack for its missiles).

    trust_stock=False ignores what the wiki lists as fitted (its default loadouts are not always
    what a ship spawns with) and buys every slot.
    """
    picks: list[Pick] = []
    by_name = {c.name: c for comps in catalog.values() for c in comps}
    missiles = catalog.get("missile", [])
    best_missiles = _attach_rack_stats(catalog.get("missile_rack", []), missiles, goals)

    for slot in ship.slots:
        comps = catalog.get(slot.kind, [])
        cands = _candidates(comps, slot, exact=slot.kind not in ("gun",))
        if max_grade:
            cands = [c for c in cands if c.grade <= max_grade] or cands
        stock_comp = by_name.get(slot.equipped or "")
        if slot.fixed:
            # welded-on part (Stingray racks): nothing to choose, but missiles below may still be swapped
            if stock_comp is None:
                continue
            best, keep = stock_comp, True
            picks.append(Pick(slot, best, keep, stock=slot.equipped, fixed=True))
        else:
            current = stock_comp if trust_stock else None
            if current is not None and current not in cands:
                cands = cands + [current]  # stock part competes even if it is not sold
            best = _best(cands, goals)
            if best is None:
                if stock_comp is not None:  # nothing sold fits this port: show the stock part as-is
                    picks.append(Pick(slot, stock_comp, True, stock=slot.equipped, fixed=True))
                continue
            keep = current is not None and (current.name == best.name or
                                            (keep_equal and score(current, goals, cands) >= score(best, goals, cands)))
            if keep:
                best = current
            picks.append(Pick(slot, best, keep, stock=slot.equipped))

        if slot.kind == "missile_rack":
            m = best_missiles.get(best.name)
            if m is None:
                continue
            count = int(best.stats["count"])
            m_keep = keep and trust_stock and slot.equipped_missile == m.name
            picks.append(Pick(slot, m, m_keep, count, stock=slot.equipped_missile))
    return picks


def budget_report(ship: Ship, picks: list[Pick]) -> dict:
    """Power/cooling demand of the chosen loadout vs. what plants/coolers produce."""
    power_gen = sum(p.component.stats["power"] for p in picks if p.slot.kind == "power_plant")
    cool_gen = sum(p.component.stats["coolant"] for p in picks if p.slot.kind == "cooler")
    power_use = sum(p.component.power_draw for p in picks if p.component.kind != "power_plant")
    cool_use = sum(p.component.coolant_draw for p in picks if p.component.kind != "cooler")
    return {"power_generation": power_gen or ship.power_generation, "power_usage": power_use,
            "cooling_generation": cool_gen or ship.cooling_generation, "cooling_usage": cool_use}


def totals(picks: list[Pick]) -> dict:
    """Headline numbers of a loadout."""
    return {
        "dps": sum(p.component.stats.get("dps", 0) for p in picks if p.component.kind == "gun"),
        "shield_hp": sum(p.component.stats.get("hp", 0) for p in picks if p.component.kind == "shield"),
        "missile_damage": sum(p.component.stats.get("damage", 0) * p.quantity
                              for p in picks if p.component.kind == "missile"),
        "cost": sum(p.price for p in picks),
    }
