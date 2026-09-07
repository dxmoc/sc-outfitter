"""Normalize wiki items into flat Component records with a shop offer list."""
from __future__ import annotations

from dataclasses import dataclass, field

from . import api

# wiki item type -> our slot kind
TYPES = {
    "QuantumDrive": "quantum_drive",
    "Shield": "shield",
    "PowerPlant": "power_plant",
    "Cooler": "cooler",
    "WeaponGun": "gun",
    "MissileLauncher": "missile_rack",
    "Missile": "missile",
    "Radar": "radar",
}
KINDS = list(TYPES.values())


@dataclass
class Offer:
    terminal_id: int
    terminal_name: str
    price: int


@dataclass
class Component:
    name: str
    kind: str
    size: int
    grade: str
    cls: str
    stats: dict = field(default_factory=dict)
    power_draw: float = 0.0
    coolant_draw: float = 0.0
    offers: list[Offer] = field(default_factory=list)
    tags: frozenset = frozenset()           # e.g. {"LaserRepeater", "Wolf_Gun"}
    required_tags: frozenset = frozenset()  # bespoke parts: only fit ports that carry these

    @property
    def buyable(self) -> bool:
        return bool(self.offers)

    @property
    def cheapest(self) -> int | None:
        return min((o.price for o in self.offers), default=None)

    def summary(self) -> str:
        s = self.stats
        if self.kind == "gun":
            return f"{s['dps']:.0f} dps, {s['range']:.0f} m{', ammo' if s['ammo'] else ''}"
        if self.kind == "shield":
            return f"{s['hp']:.0f} hp, {s['regen']:.0f}/s regen"
        if self.kind == "power_plant":
            return f"{s['power']:.0f} power segments"
        if self.kind == "cooler":
            return f"{s['coolant']:.0f} coolant segments"
        if self.kind == "quantum_drive":
            return (f"{s['speed'] / 1e6:.0f} Mm/s, spool {s['spool']:.1f}s, "
                    f"{s['fuel_rate'] * 1e9:.2f} fuel/Gm")
        if self.kind == "missile_rack":
            return f"{s['count']}x S{s['missile_size']}"
        if self.kind == "missile":
            return f"{s['damage']:.0f} dmg, {s['speed']:.0f} m/s, {s['range'] / 1000:.0f} km, {s['signal']}"
        if self.kind == "radar":
            return f"sensitivity {s['sensitivity']:.2f}"
        return ""


def _usage(item: dict) -> tuple[float, float]:
    rn = item.get("resource_network") or {}
    u = rn.get("usage") or {}
    p = (u.get("power") or {}).get("max") or 0.0
    c = (u.get("coolant") or {}).get("max") or 0.0
    return float(p), float(c)


def _stats(kind: str, item: dict) -> dict | None:
    if kind == "gun":
        w = item.get("vehicle_weapon") or {}
        modes = w.get("modes") or []
        dps = max((m.get("damage_per_second") or 0) for m in modes) if modes else 0
        if dps <= 0:
            return None
        return {"dps": float(dps), "range": float(w.get("range") or 0),
                "ammo": bool(w.get("capacity")), "weapon_type": w.get("type") or ""}
    if kind == "shield":
        s = item.get("shield") or {}
        if not s.get("max_health"):
            return None
        return {"hp": float(s["max_health"]), "regen": float(s.get("regen_rate") or 0)}
    if kind == "power_plant":
        p = item.get("power_plant") or {}
        gen = p.get("power_segment_generation") or 0
        return {"power": float(gen)} if gen else None
    if kind == "cooler":
        c = item.get("cooler") or {}
        gen = c.get("coolant_segment_generation") or 0
        return {"coolant": float(gen)} if gen else None
    if kind == "quantum_drive":
        q = item.get("quantum_drive") or {}
        j = q.get("standard_jump") or {}
        if not j.get("drive_speed"):
            return None
        return {"speed": float(j["drive_speed"]), "spool": float(j.get("spool_up_time") or 0),
                "cooldown": float(j.get("cooldown_time") or 0),
                "accel": float(j.get("stage_two_accel_rate") or j.get("stage_one_accel_rate") or 0),
                "fuel_rate": float(q.get("fuel_rate") or 0)}  # fuel units per metre
    if kind == "missile_rack":
        r = item.get("missile_rack") or {}
        if not r.get("missile_count"):
            return None
        # do not filter on required_tags: the MSD-322 entry that carries the shop offers has them
        return {"count": int(r["missile_count"]), "missile_size": int(r["missile_size"])}
    if kind == "missile":
        m = item.get("missile") or {}
        dmg = m.get("damage_total") or 0
        if not dmg or item.get("sub_type") == "Torpedo" and not m.get("speed"):
            return None
        fl = m.get("flight") or {}
        return {"damage": float(dmg), "speed": float(fl.get("speed") or m.get("speed") or 0),
                "range": float(fl.get("range") or 0), "signal": m.get("signal_type") or "?",
                "lock_time": float(m.get("lock_time") or 0)}
    if kind == "radar":
        r = item.get("radar") or {}
        sens = r.get("sensitivity") or {}
        vals = [sens.get(k) for k in ("infrared", "cross_section", "electromagnetic") if sens.get(k)]
        if not vals:
            return None
        return {"sensitivity": sum(vals) / len(vals), "sub_type": item.get("sub_type") or ""}
    return None


def _offers(item: dict) -> list[Offer]:
    out = []
    for o in ((item.get("uex_prices") or {}).get("purchase") or []):
        if o.get("price_buy"):
            out.append(Offer(int(o["terminal_id"]), o.get("terminal_name") or "", int(o["price_buy"])))
    return out


def load_catalog(kinds: set[str] | None = None) -> dict[str, list[Component]]:
    """{kind: [Component]} for every kind in TYPES (or the requested subset)."""
    catalog: dict[str, list[Component]] = {}
    for wiki_type, kind in TYPES.items():
        if kinds and kind not in kinds:
            continue
        by_key: dict[tuple[str, int], Component] = {}
        for item in api.wiki_items(wiki_type):
            stats = _stats(kind, item)
            if stats is None:
                continue
            # stock parts (e.g. Regulus) are flagged as variants on the wiki and the same name can
            # appear several times (loot/paint variants) with the shop offers on only one of them,
            # so merge by (name, size) instead of filtering on is_base_variant
            power, coolant = _usage(item)
            comp = Component(item["name"], kind, int(item.get("size") or 0),
                             str(item.get("grade") or "?"), str(item.get("class") or ""),
                             stats, power, coolant, _offers(item),
                             frozenset(item.get("tags") or []), frozenset(item.get("required_tags") or []))
            key = (comp.name, comp.size)
            if key in by_key:
                by_key[key].offers.extend(comp.offers)
            else:
                by_key[key] = comp
        catalog[kind] = list(by_key.values())
    return catalog
