using System.Globalization;

namespace Jellyfin.Plugin.StreamCinema.Core;

/// <summary>
/// Rozpoznání duplicit — „tenhle díl už mám / už se stahuje ve stejné kvalitě?".
/// Kvalita se porovnává normalizovaně (SC posílá „1080p", „4K", „3D-SBS"…),
/// velikost s tolerancí: tentýž film v téže kvalitě se mezi zdroji liší o stovky MB,
/// ale jiná verze (3D, extended, jiný remux) se liší řádově víc.
/// </summary>
public static class Dedup
{
    /// <summary>Výchozí tolerance velikosti v procentech (rozdíl do 5 % = tentýž soubor).</summary>
    public const int DefaultTolerancePercent = 5;

    private static readonly Dictionary<string, string> QualityAliases = new(StringComparer.OrdinalIgnoreCase)
    {
        ["4k"] = "4k",
        ["2160p"] = "4k",
        ["2160"] = "4k",
        ["uhd"] = "4k",
        ["8k"] = "8k",
        ["4320p"] = "8k",
        ["1080p"] = "1080p",
        ["1080"] = "1080p",
        ["fullhd"] = "1080p",
        ["fhd"] = "1080p",
        ["720p"] = "720p",
        ["720"] = "720p",
        ["hd"] = "720p",
        ["sd"] = "sd",
        ["480p"] = "sd",
        ["dvd"] = "sd",
    };

    /// <summary>
    /// Klíč obsahu (bez ohledu na stream) — dva záznamy se stejným klíčem jsou tentýž
    /// film / tatáž epizoda. Používá se pro „už je ve frontě" i „už se stahuje".
    /// </summary>
    public static string MediaKey(QueueItem item)
    {
        if (item.MediaType == ScMediaType.Episode)
        {
            var series = MediaOrganizer.CleanTitle(item.SeriesTitle ?? item.Title).ToLowerInvariant();
            return string.Create(
                CultureInfo.InvariantCulture,
                $"ep|{series}|{item.Season ?? 0}|{item.Episode ?? 0}");
        }

        var title = MediaOrganizer.CleanTitle(item.Title).ToLowerInvariant();
        return string.Create(CultureInfo.InvariantCulture, $"mv|{title}|{item.Year ?? 0}");
    }

    /// <summary>Normalizovaná kvalita pro porovnání („4K" == „2160p", „3D-SBS" zůstává vlastní).</summary>
    public static string NormQuality(string? quality)
    {
        var q = (quality ?? string.Empty).Trim();
        if (q.Length == 0)
        {
            return string.Empty;
        }

        return QualityAliases.TryGetValue(q, out var norm) ? norm : q.ToLowerInvariant();
    }

    /// <summary>
    /// Je to velikostně tentýž soubor? Když jednu z velikostí neznáme, bereme to jako
    /// shodu — raději nestáhnout znovu, než stahovat zbytečně (anti-ban i objem).
    /// </summary>
    public static bool SameSize(long a, long b, int tolerancePercent)
    {
        if (a <= 0 || b <= 0)
        {
            return true;
        }

        var max = Math.Max(a, b);
        var diff = Math.Abs(a - b) * 100.0 / max;
        return diff <= Math.Max(0, tolerancePercent);
    }

    /// <summary>Lidský popis velikosti pro hlášky (12,4 GB).</summary>
    public static string FormatSize(long bytes)
    {
        if (bytes <= 0)
        {
            return "?";
        }

        string[] units = ["B", "kB", "MB", "GB", "TB"];
        double value = bytes;
        var unit = 0;
        while (value >= 1024 && unit < units.Length - 1)
        {
            value /= 1024;
            unit++;
        }

        return string.Create(CultureInfo.InvariantCulture, $"{value:0.#} {units[unit]}");
    }
}
