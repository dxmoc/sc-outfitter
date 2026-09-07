namespace ScOutfitter.Core;

/// <summary>One vehicle entry of the wiki. Several entries can share a name (variants, editions).</summary>
public sealed record ShipRef(string Name, string Slug, string ClassName, bool FlightReady)
{
    /// <summary>Name plus a variant tag when the plain name is ambiguous, e.g. "S-65 Stingray (Ballistic)".</summary>
    public string Label { get; init; } = Name;
}

/// <summary>
/// Picks the right wiki entry for what the user typed. The wiki's name lookup returns an arbitrary
/// variant (the Stingray came back as the BALLISTIC edition), so everything goes through the slug.
/// </summary>
public static class ShipResolver
{
    /// <summary>Give duplicates a variant tag derived from the class name; the shortest class name is the base.</summary>
    public static List<ShipRef> Label(IEnumerable<ShipRef> ships)
    {
        var result = new List<ShipRef>();
        foreach (IGrouping<string, ShipRef> group in ships.GroupBy(s => s.Name, StringComparer.Ordinal))
        {
            List<ShipRef> variants = group.OrderBy(s => s.ClassName.Length).ThenBy(s => s.ClassName, StringComparer.Ordinal).ToList();
            if (variants.Count == 1)
            {
                result.Add(variants[0]);
                continue;
            }

            string baseClass = variants[0].ClassName;
            result.Add(variants[0]);
            foreach (ShipRef v in variants.Skip(1))
            {
                string tail = v.ClassName.StartsWith(baseClass + "_", StringComparison.OrdinalIgnoreCase)
                    ? v.ClassName[(baseClass.Length + 1)..]
                    : v.ClassName;
                string tag = string.Join(" ", tail.Split('_', StringSplitOptions.RemoveEmptyEntries).Select(Pretty));
                result.Add(v with { Label = $"{v.Name} ({tag})" });
            }
        }

        return result.OrderBy(s => s.Label, StringComparer.OrdinalIgnoreCase).ToList();
    }

    private static string Pretty(string part) =>
        part.Length <= 3 || part.Any(char.IsDigit) ? part.ToUpperInvariant() : char.ToUpperInvariant(part[0]) + part[1..].ToLowerInvariant();

    /// <summary>
    /// Exact label, then exact name (base variant), then a unique substring on labels.
    /// Throws <see cref="LookupException"/> when nothing or too much matches.
    /// </summary>
    public static ShipRef Resolve(IReadOnlyList<ShipRef> ships, string query)
    {
        string q = query.Trim();
        if (q.Length == 0)
        {
            throw new LookupException("Enter a ship name.");
        }

        ShipRef? exactLabel = ships.FirstOrDefault(s => s.Label.Equals(q, StringComparison.OrdinalIgnoreCase));
        if (exactLabel is not null)
        {
            return exactLabel;
        }

        ShipRef? exactName = ships.Where(s => s.Name.Equals(q, StringComparison.OrdinalIgnoreCase))
            .OrderBy(s => s.ClassName.Length).FirstOrDefault();
        if (exactName is not null)
        {
            return exactName;
        }

        List<ShipRef> hits = ships.Where(s => s.Label.Contains(q, StringComparison.OrdinalIgnoreCase)).ToList();
        List<string> names = hits.Select(h => h.Name).Distinct(StringComparer.Ordinal).ToList();
        if (names.Count == 1)
        {
            // one ship, maybe several variants: take the base
            return hits.OrderBy(s => s.ClassName.Length).First();
        }

        throw names.Count > 1
            ? new LookupException($"Ambiguous ship name '{q}': {string.Join(", ", names.Take(8))}")
            : new LookupException($"Ship not found on the wiki: '{q}'");
    }
}
