"""Command line interface: `python -m outfitter plan <ship> [--start LOCATION] [--profile ...]`."""
from __future__ import annotations

import argparse
import json
import sys

from . import __version__, api
from .catalog import load_catalog
from .optimizer import PROFILES, budget_report, choose, score
from .routing import QuantumDrive, Trip, fmt_duration, plan_trip
from .ship import load_ship
from .starmap import Starmap


def _pick_qd(picks, catalog, ship, use_planned: bool) -> QuantumDrive:
    """Quantum drive used for the trip: the currently equipped one unless --plan-with-new-qd."""
    qds = {c.name: c for c in catalog.get("quantum_drive", [])}
    planned = next((p.component for p in picks if p.slot.kind == "quantum_drive"), None)
    current = qds.get(ship.default_quantum_drive or "")
    comp = planned if (use_planned and planned) else (current or planned)
    if comp is None:
        sys.exit("no quantum drive data for this ship")
    return QuantumDrive.from_component(comp)


def cmd_plan(args: argparse.Namespace) -> int:
    starmap = Starmap()
    start = starmap.locate(args.start, "station")
    if not start:
        sys.exit(f"unknown start location {args.start!r} (try `locations`)")
    ship = load_ship(args.ship, fixed_guns=not args.gimbal, manned_turrets=args.turrets)
    catalog = load_catalog()
    picks = choose(ship, catalog, args.profile, keep_equal=not args.replace_all, max_grade=args.max_grade)
    terminals = api.uex_terminals()
    qd = _pick_qd(picks, catalog, ship, args.plan_with_new_qd)
    trip = plan_trip(picks, terminals, starmap, start, qd, args.auec_per_minute, args.max_stops)
    budget = budget_report(ship, picks)

    if args.json:
        print(json.dumps(_to_json(ship, picks, trip, budget, qd), indent=1))
        return 0
    _print_plan(ship, picks, trip, budget, qd, args.profile)
    return 0


def _to_json(ship, picks, trip: Trip, budget, qd) -> dict:
    return {
        "ship": ship.name,
        "loadout": [{"slot": p.slot.port, "kind": p.slot.kind, "size": p.slot.size,
                     "item": p.component.name, "grade": p.component.grade, "keep": p.keep,
                     "stats": p.component.stats, "price": p.price} for p in picks],
        "budget": budget,
        "trip": {"start": trip.start.name, "quantum_drive": qd.name,
                 "seconds": trip.seconds, "fuel_units": trip.fuel, "km": trip.km, "cost": trip.cost,
                 "stops": [{"location": s.location.name, "container": s.location.container,
                            "leg_seconds": s.leg_seconds, "leg_fuel": s.leg_fuel, "leg_km": s.leg_km,
                            "buys": [{"item": i, "shop": sh, "price": pr} for i, sh, pr in s.buys]}
                           for s in trip.stops],
                 "unavailable": trip.unavailable},
    }


def _print_plan(ship, picks, trip: Trip, budget, qd, profile) -> None:
    print(f"== {ship.name} - profile '{profile}'\n")
    print("Loadout:")
    for p in picks:
        tag = "keep " if p.keep else f"{p.price:>7,} aUEC"
        print(f"  {p.slot.kind:<14} S{p.slot.size}  {p.component.name:<28} {p.component.grade:<2} "
              f"{tag:<13} {p.component.summary()}")
    total_dps = sum(p.component.stats.get("dps", 0) for p in picks if p.slot.kind == "gun")
    total_hp = sum(p.component.stats.get("hp", 0) for p in picks if p.slot.kind == "shield")
    print(f"\n  total dps {total_dps:.0f} | shield {total_hp:.0f} hp | "
          f"power {budget['power_usage']:.1f}/{budget['power_generation']:.0f} | "
          f"cooling {budget['cooling_usage']:.1f}/{budget['cooling_generation']:.0f} segments")
    if trip.unavailable:
        print(f"\n  not sold anywhere in Stanton (skipped): {', '.join(trip.unavailable)}")

    if not trip.stops:
        print("\nNothing to buy.")
        return
    print(f"\nRoute from {trip.start.name} (quantum drive: {qd.name}):")
    tank = ship.quantum_fuel_units
    for i, s in enumerate(trip.stops, 1):
        where = s.location.name if s.location.name == s.location.container else \
            f"{s.location.name} ({s.location.container})"
        warn = "  !! beyond one tank, refuel first" if tank and s.leg_fuel > tank else ""
        print(f"  {i}. {where:<40} {s.leg_km / 1e6:>6.2f} Gm  {fmt_duration(s.leg_seconds):>8}  "
              f"fuel {s.leg_fuel:>6.0f}{warn}")
        for item, shop, price in s.buys:
            print(f"       buy {item:<28} {price:>8,} aUEC   @ {shop}")
    pct = f" ({trip.fuel / tank * 100:.0f}% of tank)" if tank else ""
    print(f"\n  total: {fmt_duration(trip.seconds)} incl. landings | {trip.km / 1e6:.2f} Gm | "
          f"fuel {trip.fuel:.0f}{pct} | {trip.cost:,} aUEC")


def cmd_slots(args: argparse.Namespace) -> int:
    ship = load_ship(args.ship, fixed_guns=not args.gimbal, manned_turrets=args.turrets)
    print(f"{ship.name}: power {ship.power_generation:.0f}, cooling {ship.cooling_generation:.0f}, "
          f"quantum fuel {ship.quantum_fuel_units:.0f}")
    for s in ship.slots:
        print(f"  {s.kind:<14} S{s.size}  {s.port:<40} {s.equipped or '-'}")
    return 0


def cmd_components(args: argparse.Namespace) -> int:
    catalog = load_catalog({args.kind})
    comps = [c for c in catalog[args.kind] if (args.size is None or c.size == args.size)]
    comps.sort(key=lambda c: score(c, args.profile), reverse=True)
    for c in comps:
        price = f"{c.cheapest:,}" if c.buyable else "not sold"
        print(f"  S{c.size} {c.grade:<2} {c.name:<28} {price:>10}  {c.summary()}")
    return 0


def cmd_locations(args: argparse.Namespace) -> int:
    sm = Starmap()
    for cname, c in sm.containers.items():
        print(cname)
        for p in c["pois"]:
            if args.all or any(k in p for k in ("Station", "Harbor", "Point", "Tressler", "HEX",
                                                "Lorville", "Area 18", "New Babbage", "Orison")):
                print(f"    {p}")
    return 0


def main(argv: list[str] | None = None) -> int:
    ap = argparse.ArgumentParser(prog="outfitter", description=__doc__)
    ap.add_argument("--version", action="version", version=__version__)
    sub = ap.add_subparsers(dest="cmd", required=True)

    def ship_opts(p):
        p.add_argument("ship", help="ship name as on the wiki, e.g. 'Gladius', 'Cutlass Black'")
        p.add_argument("--gimbal", action="store_true", help="keep gimbal mounts (guns one size smaller)")
        p.add_argument("--turrets", action="store_true", help="include manned turret guns")

    p = sub.add_parser("plan", help="best loadout + shopping route")
    ship_opts(p)
    p.add_argument("--start", default="Everus Harbor", help="where you are now (default: Everus Harbor)")
    p.add_argument("--profile", choices=sorted(PROFILES), default="combat")
    p.add_argument("--max-grade", choices=["A", "B", "C", "D"], help="cap component grade (cheaper builds)")
    p.add_argument("--replace-all", action="store_true", help="buy even if the stock part scores equal")
    p.add_argument("--plan-with-new-qd", action="store_true",
                   help="compute travel with the planned quantum drive instead of the equipped one")
    p.add_argument("--auec-per-minute", type=float, default=0.0,
                   help="how much aUEC one minute of your time is worth; 0 = pure travel time")
    p.add_argument("--max-stops", type=int, default=5)
    p.add_argument("--json", action="store_true")
    p.set_defaults(func=cmd_plan)

    p = sub.add_parser("slots", help="show a ship's component slots")
    ship_opts(p)
    p.set_defaults(func=cmd_slots)

    p = sub.add_parser("components", help="rank components of one kind")
    p.add_argument("kind", choices=["gun", "shield", "power_plant", "cooler", "quantum_drive"])
    p.add_argument("--size", type=int)
    p.add_argument("--profile", choices=sorted(PROFILES), default="combat")
    p.set_defaults(func=cmd_components)

    p = sub.add_parser("locations", help="list known start locations")
    p.add_argument("--all", action="store_true")
    p.set_defaults(func=cmd_locations)

    args = ap.parse_args(argv)
    if hasattr(sys.stdout, "reconfigure"):
        sys.stdout.reconfigure(encoding="utf-8", errors="replace")
    return args.func(args)
