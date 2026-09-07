"""HTTP clients for the Star Citizen Wiki API and the UEX Corp API, with a JSON file cache."""
from __future__ import annotations

import hashlib
import json
import pathlib
import time
import urllib.parse
import urllib.request

WIKI = "https://api.star-citizen.wiki/api/v2"
UEX = "https://api.uexcorp.space/2.0"
CACHE_DIR = pathlib.Path(__file__).resolve().parent.parent / ".cache"
CACHE_TTL = 24 * 3600
USER_AGENT = "sc-outfitter/0.1 (+https://github.com/dxmoc)"


def _get_json(url: str, ttl: int = CACHE_TTL) -> dict:
    CACHE_DIR.mkdir(exist_ok=True)
    key = hashlib.sha1(url.encode()).hexdigest()
    path = CACHE_DIR / f"{key}.json"
    if path.exists() and time.time() - path.stat().st_mtime < ttl:
        return json.loads(path.read_text(encoding="utf-8"))
    req = urllib.request.Request(url, headers={"Accept": "application/json", "User-Agent": USER_AGENT})
    with urllib.request.urlopen(req, timeout=60) as resp:
        data = json.loads(resp.read().decode("utf-8"))
    path.write_text(json.dumps(data), encoding="utf-8")
    return data


def wiki_vehicle(name: str) -> dict:
    """Full vehicle record incl. ports (hardpoints)."""
    url = f"{WIKI}/vehicles/{urllib.parse.quote(name)}"
    try:
        return _get_json(url)["data"]
    except urllib.error.HTTPError as e:  # type: ignore[attr-defined]
        if e.code == 404:
            raise LookupError(f"ship not found on the wiki: {name!r}") from None
        raise


def wiki_vehicles(flight_ready_only: bool = True) -> list[str]:
    """Names of all spaceships on the wiki (sorted, deduplicated)."""
    names: set[str] = set()
    page = 1
    while True:
        data = _get_json(f"{WIKI}/vehicles?limit=50&page={page}")
        for v in data["data"]:
            status = ((v.get("production_status") or {}).get("en_EN") or "").lower()
            if v.get("is_spaceship") and (not flight_ready_only or status == "flight-ready"):
                names.add(v["name"])
        if page >= data["meta"]["last_page"]:
            return sorted(names)
        page += 1


def wiki_items(item_type: str) -> list[dict]:
    """All items of one wiki type (QuantumDrive, Shield, PowerPlant, Cooler, WeaponGun, ...)."""
    items: list[dict] = []
    page = 1
    while True:
        q = urllib.parse.urlencode({"filter[type]": item_type, "limit": 100, "page": page})
        data = _get_json(f"{WIKI}/items?{q}")
        items.extend(data["data"])
        if page >= data["meta"]["last_page"]:
            return items
        page += 1


def uex_terminals() -> dict[int, dict]:
    """UEX item terminals keyed by id (gives us the location hierarchy of every shop)."""
    data = _get_json(f"{UEX}/terminals?type=item")["data"]
    return {t["id"]: t for t in data}
