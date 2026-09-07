using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;

namespace ScOutfitter.Core;

/// <summary>
/// HTTP client for the Star Citizen Wiki API and the UEX Corp API with a JSON file cache.
/// </summary>
/// <remarks>
/// Neither API needs a key for the endpoints used here. Responses are cached on disk for
/// <see cref="CacheTtl"/> so a second start of the app is instant and offline works for a day.
/// </remarks>
public sealed class WikiClient
{
    public const string Wiki = "https://api.star-citizen.wiki/api/v2";
    public const string Uex = "https://api.uexcorp.space/2.0";

    private static readonly HttpClient Http = CreateHttp();

    public WikiClient(string? cacheDir = null)
    {
        CacheDir = cacheDir ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "sc-outfitter", "cache");
        Directory.CreateDirectory(CacheDir);
    }

    public string CacheDir { get; }

    public TimeSpan CacheTtl { get; set; } = TimeSpan.FromHours(24);

    /// <summary>Drop everything cached so the next calls hit the network again.</summary>
    public void ClearCache()
    {
        foreach (string file in Directory.EnumerateFiles(CacheDir, "*.json"))
        {
            File.Delete(file);
        }
    }

    /// <summary>Prices change daily, so they get a shorter cache life than stats and hardpoints.</summary>
    public TimeSpan PriceTtl { get; set; } = TimeSpan.FromHours(1);

    public Task<JsonNode> GetJsonAsync(string url, CancellationToken ct = default) => GetJsonAsync(url, CacheTtl, ct);

    public async Task<JsonNode> GetJsonAsync(string url, TimeSpan ttl, CancellationToken ct)
    {
        string path = Path.Combine(CacheDir, Convert.ToHexString(SHA1.HashData(Encoding.UTF8.GetBytes(url))) + ".json");
        if (File.Exists(path) && DateTime.UtcNow - File.GetLastWriteTimeUtc(path) < ttl)
        {
            try
            {
                JsonNode? cached = JsonNode.Parse(await File.ReadAllTextAsync(path, ct).ConfigureAwait(false));
                if (cached is not null)
                {
                    return cached;
                }
            }
            catch (System.Text.Json.JsonException)
            {
                // corrupt cache file: fall through and refetch
            }
        }

        string body;
        using (HttpResponseMessage response = await Http.GetAsync(url, ct).ConfigureAwait(false))
        {
            if (response.StatusCode == HttpStatusCode.NotFound)
            {
                throw new NotFoundException(url);
            }

            response.EnsureSuccessStatusCode();
            body = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
        }

        JsonNode node = JsonNode.Parse(body) ?? throw new InvalidDataException($"empty JSON from {url}");
        await File.WriteAllTextAsync(path, body, ct).ConfigureAwait(false);
        return node;
    }

    /// <summary>Full vehicle record incl. ports, fetched by slug so the right variant comes back.</summary>
    public async Task<JsonNode> VehicleAsync(ShipRef ship, CancellationToken ct = default) =>
        (await GetJsonAsync($"{Wiki}/vehicles/{Uri.EscapeDataString(ship.Slug)}", ct).ConfigureAwait(false))["data"]
        ?? throw new InvalidDataException("vehicle without data");

    /// <summary>Resolve a typed name through the ship list, then fetch by slug.</summary>
    public async Task<JsonNode> VehicleAsync(string query, CancellationToken ct = default)
    {
        List<ShipRef> ships = await VehiclesAsync(flightReadyOnly: false, ct).ConfigureAwait(false);
        return await VehicleAsync(ShipResolver.Resolve(ships, query), ct).ConfigureAwait(false);
    }

    /// <summary>All spaceships on the wiki with variant labels, sorted by label.</summary>
    public async Task<List<ShipRef>> VehiclesAsync(bool flightReadyOnly = true, CancellationToken ct = default)
    {
        var ships = new List<ShipRef>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        for (int page = 1; ; page++)
        {
            JsonNode data = await GetJsonAsync($"{Wiki}/vehicles?limit=50&page={page}", ct).ConfigureAwait(false);
            foreach (JsonNode v in data.Arr("data"))
            {
                string slug = v.Str("slug");
                bool ready = v.Str("production_status", "en_EN").Equals("flight-ready", StringComparison.OrdinalIgnoreCase);
                if (v.Bool("is_spaceship") && slug.Length > 0 && seen.Add(slug) && (!flightReadyOnly || ready))
                {
                    ships.Add(new ShipRef(v.Str("name"), slug, v.Str("class_name"), ready));
                }
            }

            if (page >= data.Int("meta", "last_page"))
            {
                break;
            }
        }

        return ShipResolver.Label(ships);
    }

    /// <summary>All items of one wiki type (QuantumDrive, Shield, WeaponGun, ...).</summary>
    public async Task<List<JsonNode>> ItemsAsync(string type, IProgress<string>? progress = null, CancellationToken ct = default)
    {
        var items = new List<JsonNode>();
        for (int page = 1; ; page++)
        {
            JsonNode data = await GetJsonAsync($"{Wiki}/items?filter%5Btype%5D={type}&limit=100&page={page}", ct).ConfigureAwait(false);
            items.AddRange(data.Arr("data"));
            int last = data.Int("meta", "last_page");
            progress?.Report($"{type} {page}/{Math.Max(last, 1)}");
            if (page >= last)
            {
                break;
            }
        }

        return items;
    }

    /// <summary>
    /// Every item price UEX knows, straight from UEX (the wiki mirror lags about a day).
    /// One request, ~24k rows; matched to wiki items by uuid, by name as fallback.
    /// </summary>
    public async Task<List<UexPrice>> PricesAsync(CancellationToken ct = default)
    {
        JsonNode data = await GetJsonAsync($"{Uex}/items_prices_all", PriceTtl, ct).ConfigureAwait(false);
        var rows = new List<UexPrice>();
        foreach (JsonNode p in data.Arr("data"))
        {
            int price = p.Int("price_buy");
            if (price <= 0)
            {
                continue;
            }

            rows.Add(new UexPrice(p.Str("item_uuid"), p.Str("item_name"), p.Int("id_terminal"), p.Str("terminal_name"), price,
                DateTimeOffset.FromUnixTimeSeconds((long)p.Num("date_modified")).UtcDateTime));
        }

        return rows;
    }

    /// <summary>UEX item terminals keyed by id: which station/city every shop belongs to.</summary>
    public async Task<Dictionary<int, JsonNode>> TerminalsAsync(CancellationToken ct = default)
    {
        JsonNode data = await GetJsonAsync($"{Uex}/terminals?type=item", ct).ConfigureAwait(false);
        var map = new Dictionary<int, JsonNode>();
        foreach (JsonNode t in data.Arr("data"))
        {
            map[t.Int("id")] = t;
        }

        return map;
    }

    private static HttpClient CreateHttp()
    {
        var http = new HttpClient { Timeout = TimeSpan.FromSeconds(60) };
        http.DefaultRequestHeaders.UserAgent.Add(new ProductInfoHeaderValue("sc-outfitter", "1.0"));
        http.DefaultRequestHeaders.UserAgent.Add(new ProductInfoHeaderValue("(+https://github.com/dxmoc)"));
        http.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        return http;
    }
}

public sealed record UexPrice(string ItemUuid, string ItemName, int TerminalId, string TerminalName, int PriceBuy, DateTime Modified);

public sealed class NotFoundException(string url) : Exception($"404: {url}");

public sealed class LookupException(string message) : Exception(message);
