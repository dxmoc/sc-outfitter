"""Tkinter front end: pick ship, start and goals, get the route as a numbered list."""
from __future__ import annotations

import queue
import threading
import tkinter as tk
from tkinter import ttk

from . import api
from .optimizer import GOALS
from .planner import DEFAULT_GOALS, Plan, make_plan
from .starmap import Starmap
from .routing import fmt_duration

FALLBACK_SHIPS = ["Gladius", "Arrow", "Cutlass Black", "Avenger Titan", "Hornet F7C Mk II",
                  "Freelancer", "Constellation Andromeda", "Vanguard Warden", "Corsair", "Mercury Star Runner"]


class App(tk.Tk):
    def __init__(self) -> None:
        super().__init__()
        self.title("sc-outfitter")
        self.geometry("1100x760")
        self.minsize(900, 600)
        self._queue: queue.Queue = queue.Queue()
        self._plan: Plan | None = None
        self._build()
        self.after(200, self._poll)

    # ---------- layout ----------
    def _build(self) -> None:
        top = ttk.Frame(self, padding=8)
        top.pack(fill="x")

        ttk.Label(top, text="Ship").grid(row=0, column=0, sticky="w")
        self.ship = ttk.Combobox(top, values=FALLBACK_SHIPS, width=28)
        self.ship.set("Gladius")
        self.ship.grid(row=0, column=1, sticky="w", padx=(4, 16))
        self.ship.bind("<KeyRelease>", self._filter_ships)
        self._all_ships: list[str] = FALLBACK_SHIPS
        threading.Thread(target=self._load_ships, daemon=True).start()

        self.gimbal = tk.BooleanVar(value=False)
        self.turrets = tk.BooleanVar(value=False)
        ttk.Checkbutton(top, text="keep gimbals", variable=self.gimbal).grid(row=0, column=2, sticky="w")
        ttk.Checkbutton(top, text="manned turrets", variable=self.turrets).grid(row=0, column=3, sticky="w")

        ttk.Label(top, text="Max grade").grid(row=0, column=4, sticky="w", padx=(16, 0))
        self.grade = ttk.Combobox(top, values=["any", "A", "B", "C", "D"], width=5, state="readonly")
        self.grade.set("any")
        self.grade.grid(row=0, column=5, sticky="w", padx=4)

        # where am I: system -> body -> station/city
        self.starmap = Starmap()
        ttk.Label(top, text="I am in").grid(row=1, column=0, sticky="w", pady=(6, 0))
        where = ttk.Frame(top)
        where.grid(row=1, column=1, columnspan=7, sticky="w", pady=(6, 0))
        self.system = ttk.Combobox(where, values=self.starmap.systems(), width=10, state="readonly")
        self.body = ttk.Combobox(where, width=22, state="readonly")
        self.place = ttk.Combobox(where, width=32, state="readonly")
        self.system.pack(side="left", padx=(4, 8))
        self.body.pack(side="left", padx=(0, 8))
        self.place.pack(side="left")
        self.system.bind("<<ComboboxSelected>>", self._system_changed)
        self.body.bind("<<ComboboxSelected>>", self._body_changed)
        self.system.set("Stanton")
        self._system_changed()
        self.body.set("Hurston")
        self._body_changed()
        self.place.set("Everus Harbor")

        goals = ttk.LabelFrame(self, text="What matters (weight)", padding=8)
        goals.pack(fill="x", padx=8)
        self.goal_on: dict[str, tk.BooleanVar] = {}
        self.goal_w: dict[str, tk.StringVar] = {}
        for i, (g, desc) in enumerate(GOALS.items()):
            on = tk.BooleanVar(value=g in DEFAULT_GOALS)
            w = tk.StringVar(value=f"{DEFAULT_GOALS.get(g, 1.0):g}")
            self.goal_on[g], self.goal_w[g] = on, w
            col = (i % 5) * 3
            row = i // 5
            ttk.Checkbutton(goals, text=g, variable=on).grid(row=row, column=col, sticky="w")
            ttk.Spinbox(goals, from_=0.5, to=5, increment=0.5, textvariable=w, width=4).grid(
                row=row, column=col + 1, sticky="w", padx=(2, 4))
            ttk.Label(goals, text=desc, foreground="#666").grid(row=row, column=col + 2, sticky="w", padx=(0, 18))

        bar = ttk.Frame(self, padding=(8, 6))
        bar.pack(fill="x")
        self.button = ttk.Button(bar, text="Plan route", command=self._start_plan)
        self.button.pack(side="left")
        self.status = ttk.Label(bar, text="")
        self.status.pack(side="left", padx=12)

        panes = ttk.PanedWindow(self, orient="vertical")
        panes.pack(fill="both", expand=True, padx=8, pady=(0, 8))

        route_frame = ttk.LabelFrame(panes, text="Route - fly in this order", padding=4)
        panes.add(route_frame, weight=3)
        cols = ("step", "where", "system", "distance", "time", "fuel", "buy")
        self.route = ttk.Treeview(route_frame, columns=cols, show="headings", height=8)
        for c, title, w, anchor in (("step", "#", 30, "center"), ("where", "Where", 260, "w"),
                                    ("system", "System", 70, "w"), ("distance", "Distance", 80, "e"),
                                    ("time", "Time", 70, "e"), ("fuel", "Fuel", 60, "e"),
                                    ("buy", "Buy there", 480, "w")):
            self.route.heading(c, text=title)
            self.route.column(c, width=w, anchor=anchor, stretch=c in ("where", "buy"))
        self.route.pack(fill="both", expand=True)
        self.route.bind("<<TreeviewSelect>>", self._show_stop)
        self.route_total = ttk.Label(route_frame, text="")
        self.route_total.pack(anchor="w", pady=(4, 0))

        buys_frame = ttk.LabelFrame(panes, text="Shopping list at the selected stop", padding=4)
        panes.add(buys_frame, weight=2)
        cols = ("item", "qty", "shop", "price")
        self.buys = ttk.Treeview(buys_frame, columns=cols, show="headings", height=5)
        for c, title, w, anchor in (("item", "Item", 280, "w"), ("qty", "Qty", 40, "center"),
                                    ("shop", "Shop", 380, "w"), ("price", "aUEC", 90, "e")):
            self.buys.heading(c, text=title)
            self.buys.column(c, width=w, anchor=anchor, stretch=c in ("item", "shop"))
        self.buys.pack(fill="both", expand=True)

        load_frame = ttk.LabelFrame(panes, text="Loadout", padding=4)
        panes.add(load_frame, weight=3)
        cols = ("kind", "size", "item", "grade", "status", "stats")
        self.loadout = ttk.Treeview(load_frame, columns=cols, show="headings", height=8)
        for c, title, w, anchor in (("kind", "Slot", 110, "w"), ("size", "S", 30, "center"),
                                    ("item", "Item", 260, "w"), ("grade", "Gr", 30, "center"),
                                    ("status", "Buy / keep", 100, "e"), ("stats", "Stats", 400, "w")):
            self.loadout.heading(c, text=title)
            self.loadout.column(c, width=w, anchor=anchor, stretch=c in ("item", "stats"))
        self.loadout.pack(fill="both", expand=True)
        self.loadout_total = ttk.Label(load_frame, text="")
        self.loadout_total.pack(anchor="w", pady=(4, 0))

    def _system_changed(self, _event=None) -> None:
        bodies = self.starmap.bodies(self.system.get())
        self.body["values"] = bodies
        self.body.set(bodies[0] if bodies else "")
        self._body_changed()

    def _body_changed(self, _event=None) -> None:
        places = self.starmap.places(self.system.get(), self.body.get())
        self.place["values"] = ["(in orbit)"] + places
        self.place.set(places[0] if places else "(in orbit)")

    def _start_name(self) -> str:
        place = self.place.get()
        body = self.body.get().replace(" (deep space)", "")
        return f"{self.system.get()}/{body if place == '(in orbit)' else place}"

    def _load_ships(self) -> None:
        try:
            self._queue.put(("ships", api.wiki_vehicles()))
        except Exception:  # noqa: BLE001 - offline: keep the fallback list
            pass

    def _filter_ships(self, _event=None) -> None:
        """Narrow the dropdown to names containing what was typed."""
        typed = self.ship.get().strip().lower()
        hits = [n for n in self._all_ships if typed in n.lower()] if typed else self._all_ships
        self.ship["values"] = hits or self._all_ships

    # ---------- planning ----------
    def _goals(self) -> dict[str, float]:
        goals = {}
        for g, on in self.goal_on.items():
            if on.get():
                try:
                    goals[g] = float(self.goal_w[g].get())
                except ValueError:
                    goals[g] = 1.0
        return goals or dict(DEFAULT_GOALS)

    def _start_plan(self) -> None:
        ship, start = self.ship.get().strip(), self._start_name()
        if not ship:
            self.status.config(text="enter a ship name")
            return
        self.button.state(["disabled"])
        self.status.config(text=f"planning {ship} from {start} ... (first run downloads ~1 min of data)")
        kw = dict(gimbal=self.gimbal.get(), turrets=self.turrets.get(),
                  max_grade=None if self.grade.get() == "any" else self.grade.get())
        goals = self._goals()

        def work() -> None:
            try:
                self._queue.put(("ok", make_plan(ship, start, goals, **kw)))
            except Exception as e:  # noqa: BLE001 - show anything to the user
                self._queue.put(("err", f"{type(e).__name__}: {e}"))

        threading.Thread(target=work, daemon=True).start()

    def _poll(self) -> None:
        try:
            kind, payload = self._queue.get_nowait()
        except queue.Empty:
            self.after(200, self._poll)
            return
        if kind == "ships":
            self._all_ships = payload
            self.ship["values"] = payload
            self.after(200, self._poll)
            return
        self.button.state(["!disabled"])
        if kind == "err":
            self.status.config(text=payload)
        else:
            self._plan = payload
            self._render(payload)
        self.after(200, self._poll)

    # ---------- rendering ----------
    def _render(self, plan: Plan) -> None:
        trip, ship = plan.trip, plan.ship
        for tree in (self.route, self.buys, self.loadout):
            tree.delete(*tree.get_children())
        tank = ship.quantum_fuel_units
        for i, s in enumerate(trip.planned, 1):
            where = s.location.label
            if s.jumps:
                where += f"   (via {s.jumps} jump point{'s' if s.jumps > 1 else ''})"
            fuel = f"{s.leg_fuel:.0f}" + ("  !!" if tank and s.leg_fuel > tank else "")
            items = ", ".join(f"{b.quantity}x {b.item}" if b.quantity > 1 else b.item for b in s.buys)
            self.route.insert("", "end", iid=f"p{i}", values=(
                i, where, s.system, f"{s.leg_km / 1e6:.2f} Gm", fmt_duration(s.leg_seconds), fuel, items))
        for j, s in enumerate(trip.extra):
            items = ", ".join(f"{b.quantity}x {b.item}" if b.quantity > 1 else b.item for b in s.buys)
            self.route.insert("", "end", iid=f"x{j}", values=(
                "-", s.location.name, s.system, "?", "?", "?", items + "  (not on the map)"))
        if trip.stops:
            pct = f" ({trip.fuel / tank * 100:.0f}% of tank)" if tank else ""
            self.route_total.config(text=(
                f"Start {trip.start.label} [{trip.start.system}], quantum drive {plan.quantum_drive.name}. "
                f"Total {fmt_duration(trip.seconds)} incl. landings, {trip.km / 1e6:.2f} Gm, "
                f"fuel {trip.fuel:.0f}{pct}, {trip.cost:,} aUEC"))
            self.route.selection_set(self.route.get_children()[0])
        else:
            self.route_total.config(text="Nothing to buy - stock parts already win for these goals.")
        if trip.unavailable:
            self.route_total.config(text=self.route_total.cget("text") +
                                    f"   Not sold anywhere: {', '.join(trip.unavailable)}")

        for p in plan.picks:
            status = "keep" if p.keep else f"{p.price:,}"
            name = f"{p.quantity}x {p.component.name}" if p.quantity > 1 else p.component.name
            self.loadout.insert("", "end", values=(p.component.kind, p.component.size, name,
                                                   p.component.grade, status, p.component.summary()))
        t, b = plan.totals, plan.budget
        self.loadout_total.config(text=(
            f"guns {t['dps']:.0f} dps | shields {t['shield_hp']:.0f} hp | missiles {t['missile_damage']:.0f} dmg | "
            f"power {b['power_usage']:.1f}/{b['power_generation']:.0f} | "
            f"cooling {b['cooling_usage']:.1f}/{b['cooling_generation']:.0f} segments | {t['cost']:,} aUEC"))
        self.status.config(text=f"{ship.name}: {len(trip.stops)} stop(s)")

    def _show_stop(self, _event=None) -> None:
        self.buys.delete(*self.buys.get_children())
        if not self._plan or not self.route.selection():
            return
        iid = self.route.selection()[0]
        stops = self._plan.trip.planned if iid.startswith("p") else self._plan.trip.extra
        idx = int(iid[1:]) - (1 if iid.startswith("p") else 0)
        for b in stops[idx].buys:
            self.buys.insert("", "end", values=(b.item, b.quantity, b.shop, f"{b.price:,}"))


def run() -> None:
    App().mainloop()
