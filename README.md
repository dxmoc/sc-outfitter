# sc-outfitter

Windows desktop app that builds the best purchasable loadout for a Star Citizen ship and plans
the shopping trip through Stanton, Pyro and Nyx: which shop to visit in which order and how far
each quantum jump is. Jump points between systems are part of the route.

.NET 8 / WPF, C# 12, no NuGet packages. No account, no API key.

## Run

Download `sc-outfitter.exe` from the [releases](https://github.com/dxmoc/sc-outfitter/releases)
and start it. The first start downloads the component catalogue (about a minute) behind the
splash screen; after that everything is cached for a day in `%LOCALAPPDATA%\sc-outfitter\cache`.

Or build it yourself (the .NET SDK is expected in `%USERPROFILE%\.dotnet`, see `build.ps1`):

```powershell
.\build.ps1 -Run        # build and start
.\build.ps1 -Test       # run the test suite
.\build.ps1 -Publish    # self-contained single exe in publish\
```

## Using it

1. **Ship**: type part of the name, the list filters as you type (`stingray` finds `S-65 Stingray`).
2. **Where I am**: system, then planet/moon (or the star for deep-space stations), then station/city.
3. **What matters**: tick the goals and give them a weight. Every goal maps to one stat per
   component kind; stats are normalized against the best candidate for the slot so different
   units can be mixed. Kinds none of your goals touch fall back to a balanced score; `stealth`
   and `cheap` only count when ticked. Power plants and coolers always take the highest output.
4. **Plan route**. The summary tiles show dps, shield, missile damage, power and cooling budget,
   distance and cost. The route table lists the stops in flying order; click a
   stop to see what to buy there and in which shop. The loadout table shows every slot.

| Goal | Affects |
|---|---|
| `dps` | gun damage per second |
| `damage` | missile payload per rack (tube count × missile damage) |
| `range` | gun and missile range |
| `tank` | shield hit points |
| `regen` | shield regeneration |
| `speed` | quantum drive speed |
| `fuel` | quantum fuel per Gm (less is better) |
| `detection` | radar aim-assist range |
| `stealth` | low radar EM signature |
| `cheap` | lower price, across all kinds |

Options:

- **keep gimbal mounts**: plan guns one size smaller on the existing gimbals instead of fixed
  full-size guns.
- **include manned turret guns**: add turret hardpoints to the shopping list.
- **buy every slot**: ignore what the wiki lists as stock. The wiki's default loadouts are not
  always what a ship spawns with; the `Stock (wiki)` column shows what it assumed.
- **Max grade**: cap the component grade for cheaper builds.

Covered slots: guns, missile racks + missiles, shields, power plant, coolers, quantum drive,
radar. Bespoke hardpoints (the Stingray's Kruger-only guns, Hornet-only Revenants) only get parts
whose tags fit; ports that are not editable in game (welded racks and mounts) are kept and shown
as `fixed`, missiles inside them are still chosen.

## Data sources

- Component stats, ship hardpoints and shop prices: [Star Citizen Wiki API](https://api.star-citizen.wiki)
  (the wiki mirrors UEX Corp prices per shop terminal).
- Shop locations (which station/city a terminal belongs to): [UEX Corp API](https://uexcorp.space/api/documentation/).
- Positions of bodies, stations, outposts and gateways: `starmap_positions.json` from
  [StarCitizenWiki/scunpacked-data](https://github.com/StarCitizenWiki/scunpacked-data) (extracted
  game data), reduced by `tools/build_starmap.py` and embedded into the app.

## How the numbers are made

- **Route**: every shop location that sells a needed part is a candidate. All subsets of up to
  five locations and all visiting orders are enumerated; the plan with the lowest travel time
  wins. Each item is bought at the cheapest shop on the route. Stops in another system are
  reached through the gateways.
- **Ordering**: stops are ordered by modelled flight time (spool, acceleration, cruise speed and
  cooldown of the equipped quantum drive, a fixed landing/shopping/take-off overhead per stop and
  2 min per jump point). The time itself is not shown, only the resulting order and distances.

## Layout

```
src/ScOutfitter.Core     data access, catalog, ship hardpoints, optimizer, star map, routing, planner
src/ScOutfitter.App      WPF: splash screen, main window, dark theme, view model
tests/ScOutfitter.Tests  hand-rolled runner, no network needed
tools/build_starmap.py   regenerates Core/Data/starmap.json from scunpacked-data
```

## Known limits

- Positions are static snapshots from the game files. Bodies orbit and rotate, so surface
  cities drift by up to a planet diameter; irrelevant on jumps of tens of millions of km.
- Shops at places the map does not know appear as extra stops without distance or order.
- Countermeasures, jump modules and paints are not part of the loadout.
- Prices and availability are what UEX users last reported, not live server data.
- Power/cooling is reported, not enforced: being over budget is normal on some hulls and only
  means items get throttled when everything runs at once.
