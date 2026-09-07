# sc-outfitter

Builds the best purchasable loadout for a Star Citizen ship and plans the shopping trip
through Stanton, Pyro and Nyx: which shop to visit in which order, how far each quantum jump
is, how long it takes and how much quantum fuel it burns. Jump points between systems are
part of the route. Comes as a CLI and a small GUI.

Python 3.10+, standard library only (tkinter for the GUI). No account, no API key.

```
python -m outfitter gui
python -m outfitter plan Gladius --start "Everus Harbor" --goal dps --goal tank
```

```
== Gladius - goals: dps, tank

Loadout:
  missile_rack   S3  MSD-322 Missile Rack           A  keep          2x S2
  missile        S2  2x Tempest II-G Missile        A      320 aUEC  2400 dmg, 1029 m/s, 31 km, CrossSection
  gun            S3  Revenant Gatling               A   56,955 aUEC  1054 dps, 2166 m, ammo
  shield         S1  AllStop                        C  keep          3168 hp, 602/s regen
  quantum_drive  S1  FoxFire                        B  115,500 aUEC  263 Mm/s, spool 4.8s, 5.88 fuel/Gm
  radar          S1  Ecouter                        C  keep          sensitivity 0.80
  ...
  guns 3162 dps | shields 6336 hp | missiles 19200 dmg | power 19.3/16 | cooling 29.3/68 segments

Route from Everus Harbor (quantum drive: Beacon):
  1. Orison (Crusader)                         31.92 Gm   10m 46s  fuel    594
       buy 8x Tempest II-G Missile           1,280 aUEC   @ Ship Weapons - Crusader Showroom - Orison
       buy 3x Revenant Gatling             170,865 aUEC   @ Ship Weapons - Crusader Showroom - Orison
       buy FoxFire                         115,500 aUEC   @ Cousin Crow's - Providence Platform - Orison

  total: 10m 46s incl. landings | 31.92 Gm | fuel 594 (99% of tank) | 298,081 aUEC
```

## GUI

`python -m outfitter gui` (or double-click `gui.pyw`) opens a window: pick the ship, where
you are (system, then planet/moon, then station or city), tick the goals that matter (with a
weight each), hit **Plan route**. The route table lists the stops in flying order with
distance, time and fuel; clicking a stop shows what to buy there and in which shop. The
loadout table below shows every slot with the chosen part, its stats and whether you keep the
stock part or what it costs.

## Goals

"Best" is whatever you tick. Every goal maps to one stat per component kind; stats are
normalized against the best candidate for the slot so goals with different units can be mixed
and weighted. Kinds that none of your goals touch (e.g. shields when you only tick `dps`) fall
back to a balanced score, and power plants/coolers always take the highest output.

| Goal | Affects |
|---|---|
| `dps` | gun damage per second |
| `damage` | missile payload per rack (tube count × missile damage) |
| `range` | gun and missile range |
| `tank` | shield hit points |
| `regen` | shield regeneration |
| `speed` | quantum drive speed |
| `fuel` | quantum fuel per Gm (less is better) |
| `detection` | radar sensitivity |
| `cheap` | lower price, across all kinds |

Default when nothing is chosen: `dps damage tank regen=0.5 speed`.

Covered slots: guns, missile racks + missiles, shields, power plant, coolers, quantum drive,
radar. Bespoke hardpoints (e.g. the Stingray's Kruger-only guns) only get parts that fit them.
Stock parts are kept when nothing sold beats them. The stock loadout comes from the
wiki's hardpoint data; the `Stock (wiki)` column shows what it assumes. If that is not what your
ship actually spawns with, tick **buy every slot** in the GUI or pass `--buy-all`.

## Commands

| Command | What it does |
|---|---|
| `gui` | graphical planner |
| `plan <ship>` | best loadout + shopping route |
| `slots <ship>` | list the hull's component slots and stock parts |
| `components <kind> [--size N] [--goal ...]` | rank parts of one kind |
| `ships [filter]` | list flight-ready ship names (`--all` incl. concepts) |
| `locations [system]` | start locations grouped by system and body |

`plan` options:

| Option | Meaning |
|---|---|
| `--start LOC` | where you are now: a station, city or body, optionally with system, e.g. `Pyro/Checkmate` (default Everus Harbor) |
| `--goal NAME[=WEIGHT]` | repeatable, see Goals |
| `--gimbal` | keep gimbal mounts (guns one size smaller) instead of fixed max-size guns |
| `--turrets` | include manned turret guns in the shopping list |
| `--max-grade A..D` | cap the component grade for cheaper builds |
| `--replace-all` | buy even if the stock part scores equal |
| `--buy-all` | ignore the wiki's stock loadout and buy every slot (use when your ship does not spawn with what the wiki lists) |
| `--plan-with-new-qd` | compute travel with the planned quantum drive instead of the equipped one |
| `--auec-per-minute N` | value of your time; lets the router trade travel time against cheaper shops |
| `--json` | machine-readable output |

Ship names are the wiki names; a unique substring works too (`Stingray` finds `S-65 Stingray`).
`ships [filter]` lists them and the GUI dropdown filters as you type.

## Data sources

- Component stats, ship hardpoints and shop prices: [Star Citizen Wiki API](https://api.star-citizen.wiki)
  (the wiki mirrors UEX Corp prices per shop terminal).
- Shop locations (which station/city a terminal belongs to): [UEX Corp API](https://uexcorp.space/api/documentation/).
- In-game positions of bodies, stations, outposts and gateways in Stanton, Pyro and Nyx:
  `starmap_positions.json` from [StarCitizenWiki/scunpacked-data](https://github.com/StarCitizenWiki/scunpacked-data)
  (extracted game data), reduced to `data/starmap.json` by `tools/build_starmap.py`.

Responses are cached for 24 h in `.cache/`. Delete the folder to force a refresh.

## How the numbers are made

- **Route**: every shop location that sells one of the needed parts is a candidate. All
  subsets of up to `--max-stops` locations and all visiting orders are enumerated; the plan
  with the lowest travel time (plus price, if `--auec-per-minute` is set) wins. Each item is
  bought at the cheapest shop on the route. A stop in another system is reached through the
  gateways (Stanton–Pyro, Stanton–Nyx, Pyro–Nyx); the leg shows how many jump points it uses.
- **Jump time**: spool + 5 s calibration + accelerate/cruise/decelerate at the drive's
  stage-two acceleration + cooldown, plus a fixed landing/shopping/take-off overhead per stop
  (station 3 min, outpost 4 min, city 7 min) and 2 min per jump point.
- **Fuel**: distance × the drive's fuel rate. The tank size is the wiki's quantum fuel
  capacity × 1000, which matches the wiki's own range figure. Legs needing more than one tank
  are flagged; refuelling is up to you.

## Known limits

- Positions are static snapshots from the game files. Bodies orbit and rotate, so surface
  cities drift by up to a planet diameter; irrelevant on jumps of tens of millions of km.
- Shops at places the map does not know appear as unordered extra stops without distance.
- Countermeasures, jump modules and paints are not part of the loadout.
- Prices and availability are what UEX users last reported, not live server data.
- Power/cooling is reported, not enforced: being over budget is normal on some hulls and only
  means items get throttled when everything runs at once.
