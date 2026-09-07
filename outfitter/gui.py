"""Tkinter front end: pick ship, start and goals, get the route as a numbered list.

Dark, flat look built on the ttk "clam" theme; no third-party packages.
"""
from __future__ import annotations

import queue
import threading
import tkinter as tk
from tkinter import ttk

from . import api
from .optimizer import GOALS
from .planner import DEFAULT_GOALS, Plan, make_plan
from .routing import fmt_duration
from .starmap import Starmap

FALLBACK_SHIPS = ["Gladius", "Arrow", "Cutlass Black", "Avenger Titan", "F7C Hornet Mk II",
                  "Freelancer", "Constellation Andromeda", "Vanguard Warden", "Corsair", "Mercury Star Runner"]

# palette
BG = "#15181d"        # window
PANEL = "#1d2128"     # cards
FIELD = "#262b34"     # inputs, table rows
FIELD2 = "#2c323c"    # alternate rows
BORDER = "#333a45"
FG = "#e6e9ef"
MUTED = "#8b93a1"
ACCENT = "#f2a93b"    # Star Citizen amber
ACCENT_DARK = "#c98620"
GOOD = "#7fd18b"
WARN = "#ff7b72"
FONT = ("Segoe UI", 10)
FONT_BOLD = ("Segoe UI", 10, "bold")
FONT_TITLE = ("Segoe UI Semibold", 15)
FONT_SMALL = ("Segoe UI", 9)


def apply_theme(root: tk.Tk) -> None:
    st = ttk.Style(root)
    st.theme_use("clam")
    root.configure(bg=BG)
    root.option_add("*TCombobox*Listbox.background", FIELD)
    root.option_add("*TCombobox*Listbox.foreground", FG)
    root.option_add("*TCombobox*Listbox.selectBackground", ACCENT)
    root.option_add("*TCombobox*Listbox.selectForeground", BG)
    root.option_add("*TCombobox*Listbox.font", FONT)

    st.configure(".", background=BG, foreground=FG, font=FONT, bordercolor=BORDER,
                 lightcolor=PANEL, darkcolor=PANEL, troughcolor=PANEL, focuscolor=ACCENT)
    st.configure("TFrame", background=BG)
    st.configure("Card.TFrame", background=PANEL)
    st.configure("TLabel", background=BG, foreground=FG)
    st.configure("Card.TLabel", background=PANEL, foreground=FG)
    st.configure("Muted.TLabel", background=PANEL, foreground=MUTED, font=FONT_SMALL)
    st.configure("Title.TLabel", background=BG, foreground=ACCENT, font=FONT_TITLE)
    st.configure("Section.TLabel", background=PANEL, foreground=ACCENT, font=FONT_BOLD)
    st.configure("Status.TLabel", background=BG, foreground=MUTED)
    st.configure("Total.TLabel", background=PANEL, foreground=GOOD, font=FONT_BOLD)

    st.configure("TCheckbutton", background=PANEL, foreground=FG, indicatorbackground=FIELD,
                 indicatorforeground=BG, indicatormargin=(2, 2, 6, 2), padding=(2, 2))
    st.map("TCheckbutton", indicatorbackground=[("selected", ACCENT), ("active", FIELD2)],
           background=[("active", PANEL)], foreground=[("active", FG)])

    st.configure("TCombobox", fieldbackground=FIELD, background=FIELD, foreground=FG,
                 arrowcolor=FG, selectbackground=FIELD, selectforeground=FG, padding=4)
    st.map("TCombobox", fieldbackground=[("readonly", FIELD), ("disabled", PANEL)],
           foreground=[("readonly", FG)], arrowcolor=[("disabled", MUTED)])
    st.configure("TSpinbox", fieldbackground=FIELD, background=FIELD, foreground=FG, arrowcolor=FG,
                 selectbackground=FIELD, padding=2)

    st.configure("Accent.TButton", background=ACCENT, foreground=BG, font=FONT_BOLD, padding=(18, 8),
                 borderwidth=0)
    st.map("Accent.TButton", background=[("active", ACCENT_DARK), ("disabled", BORDER)],
           foreground=[("disabled", MUTED)])

    st.configure("Treeview", background=FIELD, fieldbackground=FIELD, foreground=FG, rowheight=26,
                 borderwidth=0, font=FONT)
    st.map("Treeview", background=[("selected", ACCENT)], foreground=[("selected", BG)])
    st.configure("Treeview.Heading", background=PANEL, foreground=MUTED, font=FONT_BOLD, relief="flat",
                 padding=(6, 6))
    st.map("Treeview.Heading", background=[("active", PANEL)])
    st.configure("TPanedwindow", background=BG)
    st.configure("Sash", sashthickness=6, background=BG)
    st.configure("Vertical.TScrollbar", background=PANEL, troughcolor=BG, arrowcolor=MUTED, borderwidth=0)


class App(tk.Tk):
    def __init__(self) -> None:
        super().__init__()
        self.title("sc-outfitter")
        self.geometry("1180x980")
        self.minsize(960, 760)
        apply_theme(self)
        self._queue: queue.Queue = queue.Queue()
        self._plan: Plan | None = None
        self._build()
        self.after(200, self._poll)

    # ---------- layout helpers ----------
    def _card(self, parent, title: str) -> ttk.Frame:
        outer = ttk.Frame(parent, style="Card.TFrame", padding=12)
        ttk.Label(outer, text=title, style="Section.TLabel").pack(anchor="w", pady=(0, 8))
        return outer

    def _table(self, parent, columns, height: int) -> ttk.Treeview:
        wrap = ttk.Frame(parent, style="Card.TFrame")
        wrap.pack(fill="both", expand=True)
        parent.pack_propagate(True)
        tree = ttk.Treeview(wrap, columns=[c[0] for c in columns], show="headings", height=height)
        for key, title, width, anchor, stretch in columns:
            tree.heading(key, text=title, anchor=anchor)
            tree.column(key, width=width, anchor=anchor, stretch=stretch, minwidth=30)
        sb = ttk.Scrollbar(wrap, orient="vertical", command=tree.yview)
        tree.configure(yscrollcommand=sb.set)
        tree.pack(side="left", fill="both", expand=True)
        sb.pack(side="right", fill="y")
        tree.tag_configure("odd", background=FIELD2)
        tree.tag_configure("even", background=FIELD)
        tree.tag_configure("warn", foreground=WARN)
        tree.tag_configure("keep", foreground=MUTED)
        tree.tag_configure("extra", foreground=MUTED)
        return tree

    # ---------- layout ----------
    def _build(self) -> None:
        head = ttk.Frame(self, padding=(16, 12, 16, 4))
        head.pack(fill="x")
        ttk.Label(head, text="sc-outfitter", style="Title.TLabel").pack(side="left")
        ttk.Label(head, text="   best loadout + shopping route", style="Status.TLabel").pack(side="left", pady=(6, 0))

        top = self._card(self, "Ship and where you are")
        top.pack(fill="x", padx=16, pady=(4, 8))
        row1 = ttk.Frame(top, style="Card.TFrame")
        row1.pack(fill="x")
        ttk.Label(row1, text="Ship", style="Card.TLabel").pack(side="left")
        self.ship = ttk.Combobox(row1, values=FALLBACK_SHIPS, width=30)
        self.ship.set("Gladius")
        self.ship.pack(side="left", padx=(8, 24))
        self.ship.bind("<KeyRelease>", self._filter_ships)
        self._all_ships: list[str] = FALLBACK_SHIPS
        threading.Thread(target=self._load_ships, daemon=True).start()

        self.gimbal = tk.BooleanVar(value=False)
        self.turrets = tk.BooleanVar(value=False)
        self.buy_all = tk.BooleanVar(value=False)
        ttk.Checkbutton(row1, text="keep gimbals", variable=self.gimbal).pack(side="left", padx=(0, 12))
        ttk.Checkbutton(row1, text="manned turrets", variable=self.turrets).pack(side="left", padx=(0, 12))
        ttk.Checkbutton(row1, text="buy every slot (ignore stock parts)", variable=self.buy_all).pack(
            side="left", padx=(0, 24))
        ttk.Label(row1, text="Max grade", style="Card.TLabel").pack(side="left")
        self.grade = ttk.Combobox(row1, values=["any", "A", "B", "C", "D"], width=5, state="readonly")
        self.grade.set("any")
        self.grade.pack(side="left", padx=(8, 0))

        row2 = ttk.Frame(top, style="Card.TFrame")
        row2.pack(fill="x", pady=(10, 0))
        self.starmap = Starmap()
        ttk.Label(row2, text="I am in", style="Card.TLabel").pack(side="left")
        self.system = ttk.Combobox(row2, values=self.starmap.systems(), width=10, state="readonly")
        self.body = ttk.Combobox(row2, width=24, state="readonly")
        self.place = ttk.Combobox(row2, width=34, state="readonly")
        self.system.pack(side="left", padx=(8, 8))
        self.body.pack(side="left", padx=(0, 8))
        self.place.pack(side="left")
        self.system.bind("<<ComboboxSelected>>", self._system_changed)
        self.body.bind("<<ComboboxSelected>>", self._body_changed)
        self.system.set("Stanton")
        self._system_changed()
        self.body.set("Hurston")
        self._body_changed()
        self.place.set("Everus Harbor")

        goals = self._card(self, "What matters")
        goals.pack(fill="x", padx=16, pady=(0, 8))
        grid = ttk.Frame(goals, style="Card.TFrame")
        grid.pack(fill="x")
        self.goal_on: dict[str, tk.BooleanVar] = {}
        self.goal_w: dict[str, tk.StringVar] = {}
        per_row = 3
        for i, (g, desc) in enumerate(GOALS.items()):
            on = tk.BooleanVar(value=g in DEFAULT_GOALS)
            w = tk.StringVar(value=f"{DEFAULT_GOALS.get(g, 1.0):g}")
            self.goal_on[g], self.goal_w[g] = on, w
            cell = ttk.Frame(grid, style="Card.TFrame")
            cell.grid(row=i // per_row, column=i % per_row, sticky="w", padx=(0, 22), pady=3)
            ttk.Checkbutton(cell, text=g, variable=on, width=10).pack(side="left")
            ttk.Spinbox(cell, from_=0.5, to=5, increment=0.5, textvariable=w, width=4).pack(side="left", padx=(0, 6))
            ttk.Label(cell, text=desc, style="Muted.TLabel").pack(side="left")

        bar = ttk.Frame(self, padding=(16, 2, 16, 8))
        bar.pack(fill="x")
        self.button = ttk.Button(bar, text="Plan route", style="Accent.TButton", command=self._start_plan)
        self.button.pack(side="left")
        self.status = ttk.Label(bar, text="", style="Status.TLabel")
        self.status.pack(side="left", padx=14)

        panes = ttk.Frame(self)
        panes.pack(fill="both", expand=True, padx=16, pady=(0, 16))

        route_frame = self._card(panes, "Route - fly in this order")
        route_frame.pack(fill="x", pady=(0, 8))
        self.route = self._table(route_frame, (
            ("step", "#", 36, "center", False), ("where", "Where", 280, "w", True),
            ("system", "System", 80, "w", False), ("distance", "Distance", 90, "e", False),
            ("time", "Time", 80, "e", False), ("fuel", "Fuel", 70, "e", False),
            ("buy", "Buy there", 460, "w", True)), 4)
        self.route.bind("<<TreeviewSelect>>", self._show_stop)
        self.route_total = ttk.Label(route_frame, text="", style="Total.TLabel")
        self.route_total.pack(anchor="w", pady=(8, 0))

        buys_frame = self._card(panes, "Shopping list at the selected stop")
        buys_frame.pack(fill="x", pady=(0, 8))
        self.buys = self._table(buys_frame, (
            ("item", "Item", 300, "w", True), ("qty", "Qty", 50, "center", False),
            ("shop", "Shop", 400, "w", True), ("price", "aUEC", 100, "e", False)), 3)

        load_frame = self._card(panes, "Loadout")
        load_frame.pack(fill="both", expand=True)
        self.loadout = self._table(load_frame, (
            ("kind", "Slot", 110, "w", False), ("size", "S", 36, "center", False),
            ("item", "Item", 250, "w", True), ("grade", "Gr", 36, "center", False),
            ("status", "Buy / keep", 100, "e", False), ("stock", "Stock (wiki)", 190, "w", False),
            ("stats", "Stats", 360, "w", True)), 6)
        self.loadout_total = ttk.Label(load_frame, text="", style="Total.TLabel")
        self.loadout_total.pack(anchor="w", pady=(8, 0))

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
        kw = dict(gimbal=self.gimbal.get(), turrets=self.turrets.get(), trust_stock=not self.buy_all.get(),
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
    @staticmethod
    def _stripe(i: int, *extra: str) -> tuple[str, ...]:
        return ("odd" if i % 2 else "even",) + extra

    def _render(self, plan: Plan) -> None:
        trip, ship = plan.trip, plan.ship
        for tree in (self.route, self.buys, self.loadout):
            tree.delete(*tree.get_children())
        tank = ship.quantum_fuel_units
        for i, s in enumerate(trip.planned, 1):
            where = s.location.label
            if s.jumps:
                where += f"   (via {s.jumps} jump point{'s' if s.jumps > 1 else ''})"
            over = bool(tank and s.leg_fuel > tank)
            fuel = f"{s.leg_fuel:.0f}" + ("  !!" if over else "")
            items = ", ".join(f"{b.quantity}x {b.item}" if b.quantity > 1 else b.item for b in s.buys)
            self.route.insert("", "end", iid=f"p{i}", tags=self._stripe(i, *(("warn",) if over else ())), values=(
                i, where, s.system, f"{s.leg_km / 1e6:.2f} Gm", fmt_duration(s.leg_seconds), fuel, items))
        for j, s in enumerate(trip.extra):
            items = ", ".join(f"{b.quantity}x {b.item}" if b.quantity > 1 else b.item for b in s.buys)
            self.route.insert("", "end", iid=f"x{j}", tags=("extra",), values=(
                "-", s.location.name, s.system, "?", "?", "?", items + "  (not on the map)"))
        if trip.stops:
            pct = f" ({trip.fuel / tank * 100:.0f}% of tank)" if tank else ""
            self.route_total.config(text=(
                f"Start {trip.start.label} [{trip.start.system}]  |  quantum drive {plan.quantum_drive.name}  |  "
                f"{fmt_duration(trip.seconds)} incl. landings  |  {trip.km / 1e6:.2f} Gm  |  "
                f"fuel {trip.fuel:.0f}{pct}  |  {trip.cost:,} aUEC"))
            self.route.selection_set(self.route.get_children()[0])
        else:
            self.route_total.config(text="Nothing to buy - stock parts already win for these goals.")
        if trip.unavailable:
            self.route_total.config(text=self.route_total.cget("text") +
                                    f"   Not sold anywhere: {', '.join(trip.unavailable)}")

        for i, p in enumerate(plan.picks):
            status = "fixed" if p.fixed else ("keep" if p.keep else f"{p.price:,}")
            name = f"{p.quantity}x {p.component.name}" if p.quantity > 1 else p.component.name
            tags = self._stripe(i, *(("keep",) if p.keep else ()))
            self.loadout.insert("", "end", tags=tags, values=(
                p.component.kind, p.component.size, name, p.component.grade, status, p.stock or "-",
                p.component.summary()))
        t, b = plan.totals, plan.budget
        self.loadout_total.config(text=(
            f"guns {t['dps']:.0f} dps  |  shields {t['shield_hp']:.0f} hp  |  missiles {t['missile_damage']:.0f} dmg  |  "
            f"power {b['power_usage']:.1f}/{b['power_generation']:.0f}  |  "
            f"cooling {b['cooling_usage']:.1f}/{b['cooling_generation']:.0f} segments  |  {t['cost']:,} aUEC"))
        self.status.config(text=f"{ship.name}: {len(trip.stops)} stop(s)")

    def _show_stop(self, _event=None) -> None:
        self.buys.delete(*self.buys.get_children())
        if not self._plan or not self.route.selection():
            return
        iid = self.route.selection()[0]
        stops = self._plan.trip.planned if iid.startswith("p") else self._plan.trip.extra
        idx = int(iid[1:]) - (1 if iid.startswith("p") else 0)
        for i, b in enumerate(stops[idx].buys):
            self.buys.insert("", "end", tags=self._stripe(i), values=(b.item, b.quantity, b.shop, f"{b.price:,}"))


def run() -> None:
    App().mainloop()
