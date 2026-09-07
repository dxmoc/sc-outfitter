"""Ship model: which component slots a hull has and what is equipped by default."""
from __future__ import annotations

from dataclasses import dataclass, field

from . import api

PORT_TYPES = {"QuantumDrive": "quantum_drive", "Shield": "shield",
              "PowerPlant": "power_plant", "Cooler": "cooler"}


@dataclass
class Slot:
    kind: str
    size: int
    port: str
    equipped: str | None
    gimbal: bool = False  # gun slot currently carrying a gimbal mount

    def __str__(self) -> str:
        return f"{self.kind} S{self.size} ({self.port})"


@dataclass
class Ship:
    name: str
    slots: list[Slot] = field(default_factory=list)
    quantum_fuel_units: float = 0.0  # wiki capacity * 1000 (matches quantum_range / fuel_rate)
    quantum_range_m: float = 0.0
    power_generation: float = 0.0
    cooling_generation: float = 0.0

    def slots_of(self, kind: str) -> list[Slot]:
        return [s for s in self.slots if s.kind == kind]

    @property
    def default_quantum_drive(self) -> str | None:
        qd = self.slots_of("quantum_drive")
        return qd[0].equipped if qd else None


def _compatible(port: dict, type_name: str) -> bool:
    return any(c.get("type") == type_name for c in (port.get("compatible_types") or []))


def _walk_ports(ports: list[dict], slots: list[Slot], *, fixed_guns: bool, manned: bool) -> None:
    for p in ports or []:
        ptype = p.get("type")
        size = int((p.get("sizes") or {}).get("max") or 0)
        eq = (p.get("equipped_item") or {}).get("name")
        if ptype in PORT_TYPES:
            slots.append(Slot(PORT_TYPES[ptype], size, p["name"], eq))
            continue
        if _compatible(p, "WeaponGun") and size > 0:
            # a gun hardpoint; may currently hold a gimbal mount whose child port is one size smaller
            child_gun = None
            for c in (p.get("ports") or []):
                if c.get("type") == "WeaponGun" or _compatible(c, "WeaponGun"):
                    child_gun = c
            eq_is_gimbal = child_gun is not None and "gimbal" in (eq or "").lower()
            child_eq = (child_gun.get("equipped_item") or {}).get("name") if child_gun else None
            if fixed_guns or not eq_is_gimbal:
                slots.append(Slot("gun", size, p["name"], child_eq if eq_is_gimbal else eq, False))
            else:
                slots.append(Slot("gun", size - 1, p["name"], child_eq, True))
            continue
        if ptype in ("Turret", "TurretBase") and manned:
            _walk_ports(p.get("ports") or [], slots, fixed_guns=fixed_guns, manned=manned)


def load_ship(name: str, *, fixed_guns: bool = True, manned_turrets: bool = False) -> Ship:
    v = api.wiki_vehicle(name)
    ship = Ship(v.get("name") or name)
    q = v.get("quantum") or {}
    ship.quantum_fuel_units = float(q.get("quantum_fuel_capacity") or 0) * 1000
    ship.quantum_range_m = float(q.get("quantum_range") or 0)
    ship.power_generation = float((v.get("power") or {}).get("generation_segments") or 0)
    ship.cooling_generation = float((v.get("cooling") or {}).get("generation_segments") or 0)
    _walk_ports(v.get("ports") or [], ship.slots, fixed_guns=fixed_guns, manned=manned_turrets)
    return ship
