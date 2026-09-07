"""Command line interface: `python -m outfitter plan <ship> [--start LOCATION] [--goal ...]`."""
from __future__ import annotations

import argparse
import json
import sys

from . import __version__
from .catalog import KINDS, load_catalog
from .optimizer import GOALS, score
from .planner import DEFAULT_GOALS, Plan, make_plan, start_locations
from .routing import fmt_duration
from .ship import load_ship


def parse_goals(values: list[str] | None) -> dict[str, float]:
    """--goal dps --goal tank=2  ->  {"dps": 1.0, "tank": 2.0}"""
    if not values:
        return dict(DEFAULT_GOALS)
    goals: dict[str, float] = {}
    for v in values:
        name, _, weight = v.partition("=")
        if name not in GOALS:
            sys.exit(f"unknown goal {name!r}; choose from {', '.join(GOALS)}")
        goals[name] = float(weight) if weight else 1.0
    return goals


def cmd_plan(args: argparse.Namespace) -> int:
    try:
        plan = make_plan(args.ship, args.start, parse_goals(args.goal), gimbal=args.gimbal,
                         turrets=args.turrets, max_grade=args.max_grade, replace_all=args.replace_all,
                         plan_with_new_qd=args.plan_with_new_qd, auec_per_minute=args.auec_per_minute,
                         max_stops=args.max_stops)
    except LookupError as e:
        sys.exit(str(e))
    if args.json:
        print(json.dumps(plan.to_json(), indent=1))
    else:
        print_plan(plan)
    return 0


def print_plan(plan: Plan) -> None:
    ship, trip, budget, tot = plan.ship, plan.trip, plan.budget, plan.totals
    goals = ", ".join(f"{g}" if w == 1 else f"{g}x{w:g}" for g, w in plan.goals.items())
    print(f"== {ship.name} - goals: {goals}\n")
    print("Loadout:")
    for p in plan.picks:
        tag = "keep " if p.keep else f"{p.price:>7,} aUEC"
        qty = f"{p.quantity}x " if p.quantity > 1 else ""
        print(f"  {p.component.kind:<14} S{p.component.size}  {qty + p.component.name:<30} "
              f"{p.component.grade:<2} {tag:<13} {p.component.summary()}")
    print(f"\n  guns {tot['dps']:.0f} dps | shields {tot['shield_hp']:.0f} hp | "
          f"missiles {tot['missile_damage']:.0f} dmg | "
          f"power {budget['power_usage']:.1f}/{budget['power_generation']:.0f} | "
          f"cooling {budget['cooling_usage']:.1f}/{budget['cooling_generation']:.0f} segments")
    if trip.unavailable:
        print(f"\n  not sold anywhere (skipped): {', '.join(trip.unavailable)}")

    if not trip.stops:
        print("\nNothing to buy.")
        return
    tank = ship.quantum_fuel_units
    if trip.planned:
        print(f"\nRoute from {trip.start.name} (quantum drive: {plan.quantum_drive.name}):")
    for i, s in enumerate(trip.planned, 1):
        where = s.location.name if s.location.name == s.location.container else \
            f"{s.location.name} ({s.location.container})"
        warn = "  !! more than one tank" if tank and s.leg_fuel > tank else ""
        print(f"  {i}. {where:<40} {s.leg_km / 1e6:>6.2f} Gm  {fmt_duration(s.leg_seconds):>8}  "
              f"fuel {s.leg_fuel:>6.0f}{warn}")
        for b in s.buys:
            qty = f"{b.quantity}x " if b.quantity > 1 else ""
            print(f"       buy {qty + b.item:<30} {b.price:>8,} aUEC   @ {b.shop}")
    if trip.extra:
        print("\nOnly sold outside the Stanton map (no order/distance):")
        for s in trip.extra:
            print(f"  - {s.location.name} ({s.system})")
            for b in s.buys:
                qty = f"{b.quantity}x " if b.quantity > 1 else ""
                print(f"       buy {qty + b.item:<30} {b.price:>8,} aUEC   @ {b.shop}")
    pct = f" ({trip.fuel / tank * 100:.0f}% of tank)" if tank else ""
    print(f"\n  total: {fmt_duration(trip.seconds)} incl. landings | {trip.km / 1e6:.2f} Gm | "
          f"fuel {trip.fuel:.0f}{pct} | {trip.cost:,} aUEC")


def cmd_slots(args: argparse.Namespace) -> int:
    ship = load_ship(args.ship, fixed_guns=not args.gimbal, manned_turrets=args.turrets)
    print(f"{ship.name}: power {ship.power_generation:.0f}, cooling {ship.cooling_generation:.0f}, "
          f"quantum fuel {ship.quantum_fuel_units:.0f}")
    for s in ship.slots:
        extra = f"  [{s.equipped_missile}]" if s.equipped_missile else ""
        print(f"  {s.kind:<14} S{s.size}  {s.port:<40} {s.equipped or '-'}{extra}")
    return 0


def cmd_components(args: argparse.Namespace) -> int:
    catalog = load_catalog({args.kind, "missile"} if args.kind == "missile_rack" else {args.kind})
    goals = parse_goals(args.goal)
    if args.kind == "missile_rack":
        from .optimizer import _attach_rack_stats
        _attach_rack_stats(catalog["missile_rack"], catalog["missile"], goals)
    comps = [c for c in catalog[args.kind] if (args.size is None or c.size == args.size)]
    cands = list(comps)  # list.sort empties the list while sorting, so score against a copy
    comps.sort(key=lambda c: score(c, goals, cands), reverse=True)
    for c in comps:
        price = f"{c.cheapest:,}" if c.buyable else "not sold"
        print(f"  S{c.size} {c.grade:<2} {c.name:<30} {price:>10}  {c.summary()}")
    return 0


def cmd_locations(args: argparse.Namespace) -> int:
    for name in start_locations():
        print(name)
    return 0


def cmd_gui(args: argparse.Namespace) -> int:
    from .gui import run
    run()
    return 0


def main(argv: list[str] | None = None) -> int:
    ap = argparse.ArgumentParser(prog="outfitter", description=__doc__)
    ap.add_argument("--version", action="version", version=__version__)
    sub = ap.add_subparsers(dest="cmd", required=True)
    goal_help = "what 'best' means; repeatable, optional weight: --goal dps --goal tank=2. " \
                f"Goals: {', '.join(GOALS)}. Default: {' '.join(DEFAULT_GOALS)}"

    def ship_opts(p):
        p.add_argument("ship", help="ship name as on the wiki, e.g. 'Gladius', 'Cutlass Black'")
        p.add_argument("--gimbal", action="store_true", help="keep gimbal mounts (guns one size smaller)")
        p.add_argument("--turrets", action="store_true", help="include manned turret guns")

    p = sub.add_parser("plan", help="best loadout + shopping route")
    ship_opts(p)
    p.add_argument("--start", default="Everus Harbor", help="where you are now (default: Everus Harbor)")
    p.add_argument("--goal", action="append", help=goal_help)
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
    p.add_argument("kind", choices=KINDS)
    p.add_argument("--size", type=int)
    p.add_argument("--goal", action="append", help=goal_help)
    p.set_defaults(func=cmd_components)

    p = sub.add_parser("locations", help="list known start locations")
    p.set_defaults(func=cmd_locations)

    p = sub.add_parser("gui", help="open the graphical planner")
    p.set_defaults(func=cmd_gui)

    args = ap.parse_args(argv)
    if hasattr(sys.stdout, "reconfigure"):
        sys.stdout.reconfigure(encoding="utf-8", errors="replace")
    return args.func(args)
