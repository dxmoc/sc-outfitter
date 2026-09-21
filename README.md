# sc-outfitter

Windows desktop app that builds the best purchasable loadout for a Star Citizen ship and plans
the shopping trip through Stanton, Pyro and Nyx: which shop to visit in which order and how far
each quantum jump is. Jump points between systems are part of the route.

.NET 8 / WPF, C# 12, no NuGet packages. No account, no API key.

## Run

Download from the [releases](https://github.com/dxmoc/sc-outfitter/releases):

- `sc-outfitter.exe` (a few MB) needs the [.NET 8 Desktop Runtime](https://dotnet.microsoft.com/download/dotnet/8.0)
  once on the machine.
- `sc-outfitter-standalone.exe` brings the runtime along, no install needed.

The first start downloads the component catalogue (about a minute) behind the splash screen,
then asks what you want: **Plan a loadout** (the full planner) or **Route for an erkul build**
(paste an erkul.games link, the parts are taken as they are and only the shopping trip is
planned). You can switch between the two at the top of the sidebar any time. Stats and hardpoints are cached for a day, prices for an hour, in
`%LOCALAPPDATA%\sc-outfitter\cache`. **Refresh data** throws the cache away and reloads everything;
the line under the buttons shows how old the newest UEX price report is.

Or build it yourself (the .NET SDK is expected in `%USERPROFILE%\.dotnet`, see `build.ps1`):

```powershell
.\build.ps1 -Run        # build and start
.\build.ps1 -Test       # run the test suite
.\build.ps1 -Publish    # both exes into publish\
```

## Using it

**Route for an erkul.games build**: paste a share link (`erkul.games/s/...`), a browse link
(`erkul.games/browse?q=@...`) or just the id, pick where you are, hit **Load build and plan
route**. Every part the build changed becomes a purchase; untouched ports keep the ship's stock
part. Parts the catalogue does not cover (paints, jump drives) are listed in the status line,
parts nobody sells are shown as `not sold`.

**Plan a loadout**:

1. **Ship**: type part of the name, the list filters as you type (`stingray` finds `S-65 Stingray`).
   Editions and variants that share a name are listed with a tag, e.g. `S-65 Stingray (Ballistic)`
   or `Cutlass Black (BIS2950)`; the plain name is always the base ship.
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

- Component stats and ship hardpoints: [Star Citizen Wiki API](https://api.star-citizen.wiki).
- Prices per shop terminal and shop locations: [UEX Corp API](https://uexcorp.space/api/documentation/),
  fetched live (`items_prices_all`, matched to wiki items by uuid). The wiki's mirrored prices are
  the fallback when UEX is unreachable.
- Shared and published builds: [erkul.games](https://erkul.games) API (`/shares/{id}`,
  `/browse/{id}`), raw-deflate JSON; parts matched to wiki items by class name.
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

## License

[MIT](LICENSE) for the code. The star map data under `tools/` and `src/ScOutfitter.Core/Data/`
is derived from Star Citizen game files and is not covered by it.

This is an unofficial Star Citizen fan project, not affiliated with the Cloud Imperium group of
companies. Star Citizen®, Roberts Space Industries® and Cloud Imperium® are registered
trademarks of Cloud Imperium Rights LLC.
