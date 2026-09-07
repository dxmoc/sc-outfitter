# sc-outfitter

Builds the best purchasable loadout for a Star Citizen ship and plans the shopping trip
through Stanton: which shop to visit in which order, how long each quantum jump takes and
how much quantum fuel it burns.

Python 3.10+, standard library only. No account, no API key.

```
python -m outfitter plan Gladius --start "Everus Harbor"
```

```
== Gladius - profile 'combat'

Loadout:
  gun            S3  Revenant Gatling             A   56,955 aUEC  1054 dps, 2166 m, ammo
  gun            S3  Revenant Gatling             A   56,955 aUEC  1054 dps, 2166 m, ammo
  cooler         S1  Bracer                       C  keep          34 coolant segments
  ...
  quantum_drive  S1  FoxFire                      B  115,500 aUEC  263 Mm/s, spool 4.8s, 5.88 fuel/Gm

  total dps 3162 | shield 6000 hp | power 14.3/16 | cooling 24.3/68 segments

Route from Everus Harbor (quantum drive: Beacon):
  1. HUR-L3                                    25.70 Gm   8m 08s  fuel    479
       buy 7SA 'Concord'                 120,000 aUEC   @ Platinum Bay - HUR-L3
  2. MIC-L2                                    ...

  total: 17m 22s incl. landings | 42.75 Gm | fuel 796 (133% of tank) | 523,965 aUEC
```

## Commands

| Command | What it does |
|---|---|
| `plan <ship>` | best loadout + shopping route |
| `slots <ship>` | list the hull's component slots and stock parts |
| `components <kind> [--size N]` | rank guns / shields / power plants / coolers / quantum drives |
| `locations` | known start locations for `--start` |

`plan` options:

| Option | Meaning |
|---|---|
| `--start LOC` | where you are now (station, city or body; default Everus Harbor) |
| `--profile combat\|brawl\|travel\|economy` | what "best" means, see below |
| `--gimbal` | keep gimbal mounts (guns one size smaller) instead of fixed max-size guns |
| `--turrets` | include manned turret guns in the shopping list |
| `--max-grade A..D` | cap the component grade for cheaper builds |
| `--replace-all` | buy even if the stock part scores equal |
| `--plan-with-new-qd` | compute travel time with the planned quantum drive instead of the equipped one |
| `--auec-per-minute N` | value of your time; lets the router trade travel time against cheaper shops |
| `--json` | machine-readable output |

Ship names are the wiki names: `Gladius`, `Cutlass Black`, `Constellation Andromeda`, ...

## Profiles

| Profile | Guns | Shields | Quantum drive |
|---|---|---|---|
| `combat` | dps, some range | hp, some regen | fastest |
| `brawl` | dps, prefers energy weapons (no rearming) | regen-heavy | fastest |
| `travel` | dps | hp | fastest, short spool |
| `economy` | dps | hp | least fuel per Gm |

Power plants and coolers are always the highest output for the slot size. The power/cooling
line shows demand vs. generation in the game's segment model; being over budget is normal on
some hulls and only means items get throttled when everything runs at once.

## Data sources

- Component stats, ship hardpoints and shop prices: [Star Citizen Wiki API](https://api.star-citizen.wiki)
  (the wiki mirrors UEX Corp prices per shop terminal).
- Shop locations (which station/city a terminal belongs to): [UEX Corp API](https://uexcorp.space/api/documentation/).
- In-game coordinates of Stanton bodies, Lagrange points and stations: derived from
  [Valalol/Star-Citizen-Navigation](https://github.com/Valalol/Star-Citizen-Navigation) (MIT),
  rebuilt with `tools/build_starmap.py`.

Responses are cached for 24 h in `.cache/`. Delete the folder to force a refresh.

## How the numbers are made

- **Loadout**: for every slot, the highest-scoring component of matching size that is sold
  somewhere. Guns may be smaller than the hardpoint, everything else must match exactly.
  Stock parts are kept when nothing sold beats them.
- **Route**: every shop location that sells one of the needed parts is a candidate. All
  subsets of up to `--max-stops` locations and all visiting orders are enumerated; the plan
  with the lowest travel time (plus price, if `--auec-per-minute` is set) wins. Each item is
  bought at the cheapest shop on the route.
- **Jump time**: spool + 5 s calibration + accelerate/cruise/decelerate at the drive's
  stage-two acceleration + cooldown, plus a fixed landing/shopping/take-off overhead per stop
  (station 3 min, outpost 4 min, city 7 min).
- **Fuel**: distance × the drive's fuel rate. The tank size is the wiki's quantum fuel
  capacity × 1000, which matches the wiki's own range figure. Legs that need more than one
  tank are flagged.

## Known limits

- Stanton only. Pyro shops are ignored; jump-point travel is not modelled.
- Positions are the parent body's centre. Surface cities and orbital stations are treated as
  sitting at the planet; the error is a few thousand km on jumps of tens of millions of km.
- Missiles, missile racks, radars and countermeasures are not part of the loadout.
- Prices and availability are what UEX users last reported, not live server data.
- "Best" is a weighted score. The weights are in `outfitter/optimizer.py` and easy to tweak.
