using System.Text.RegularExpressions;

namespace Jellyfin.Plugin.StreamCinema.Core;

/// <summary>
/// Skládá cílové cesty podle Jellyfin konvencí pojmenování.
/// Filmy:   <MoviesPath>/Nazev (rok)/Nazev (rok) - [kvalita jazyk].mkv
/// Seriály: <SeriesPath>/Nazev/Season 01/Nazev (rok) - S01E03 - [kvalita jazyk].mkv
/// (složka seriálu bez roku — přání uživatele)
/// </summary>
public static class MediaOrganizer
{
    private static readonly char[] InvalidChars = ['\\', '/', ':', '*', '?', '"', '<', '>', '|'];

    // Názvy z katalogu nesou Kodi markup a jazykový blok:
    // "Scary Movie: Děsnej biják - [B]CZ, EN, EN+tit, SK[/B] (2000)"
    private static readonly Regex KodiBoldBlock = new(@"\s*-?\s*\[B\].*?\[/B\]", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex KodiTag = new(@"\[/?[A-Za-z][^\]]*\]", RegexOptions.Compiled);
    private static readonly Regex TrailingYear = new(@"\s*\(\d{4}\)\s*$", RegexOptions.Compiled);

    /// <summary>Odstraní znaky nepovolené v názvech souborů a ořízne tečky/mezery na konci.</summary>
    public static string Sanitize(string name)
    {
        var chars = name.Where(c => !InvalidChars.Contains(c) && !char.IsControl(c)).ToArray();
        return new string(chars).Trim().TrimEnd('.', ' ');
    }

    /// <summary>
    /// Vyčistí zobrazovací název z katalogu: Kodi značky ([B]…[/B] blok s jazyky,
    /// [COLOR], …) a rok na konci (ten se doplňuje zvlášť z QueueItem.Year).
    /// </summary>
    public static string CleanTitle(string title)
    {
        var t = KodiBoldBlock.Replace(title, " ");
        t = KodiTag.Replace(t, string.Empty);
        while (TrailingYear.IsMatch(t))
        {
            t = TrailingYear.Replace(t, string.Empty);
        }

        t = t.Trim().TrimEnd('-', '·', ' ').Trim();
        return t.Length > 0 ? t : title.Trim();
    }

    /// <summary>
    /// Cílová cesta pro položku fronty. `extension` včetně tečky (".mkv").
    /// Vrací absolutní cestu; adresáře nevytváří (to dělá volající).
    /// </summary>
    public static string BuildTargetPath(string moviesPath, string seriesPath, QueueItem item, string extension)
    {
        var tag = TagSuffix(item);

        if (item.MediaType == ScMediaType.Episode)
        {
            // Složka seriálu BEZ roku (přání uživatele), název souboru s rokem
            var cleanSeries = CleanTitle(item.SeriesTitle ?? item.Title);
            var seriesDir = Sanitize(cleanSeries);
            var seriesFile = Sanitize(FormatTitle(cleanSeries, item.Year));
            var season = item.Season ?? 1;
            var episode = item.Episode ?? 1;
            var file = $"{seriesFile} - S{season:D2}E{episode:D2}{tag}{extension}";
            return Path.Combine(seriesPath, seriesDir, $"Season {season:D2}", Sanitize(file));
        }

        var movie = Sanitize(FormatTitle(CleanTitle(item.Title), item.Year));
        return Path.Combine(moviesPath, movie, $"{movie}{tag}{extension}");
    }

    /// <summary>Tag do názvu souboru: " - [kvalita jazyk]", např. " - [1080p CZ,EN]".</summary>
    private static string TagSuffix(QueueItem item)
    {
        var parts = new List<string>();
        if (!string.IsNullOrWhiteSpace(item.Quality))
        {
            parts.Add(Sanitize(item.Quality));
        }

        if (!string.IsNullOrWhiteSpace(item.Language))
        {
            // "CZ, EN, EN+tit" → "CZ,EN,EN+tit" (bez mezer, ať je název kompaktní)
            parts.Add(Sanitize(item.Language.Replace(" ", string.Empty)).ToUpperInvariant());
        }

        return parts.Count > 0 ? $" - [{string.Join(" ", parts)}]" : string.Empty;
    }

    /// <summary>Cesta pro titulky vedle videa: stejný název + .{lang}.srt.</summary>
    public static string BuildSubtitlePath(string videoPath, string? lang)
    {
        var dir = Path.GetDirectoryName(videoPath) ?? string.Empty;
        var baseName = Path.GetFileNameWithoutExtension(videoPath);
        var code = string.IsNullOrWhiteSpace(lang) ? "cs" : lang.ToLowerInvariant();
        return Path.Combine(dir, $"{baseName}.{code}.srt");
    }

    /// <summary>
    /// Najde už stažený soubor pro položku (jakákoli kvalita/jazyk/přípona) — aby se
    /// nestahovalo, co na disku je. Hledá podle základu názvu bez tagu „[kvalita jazyk]".
    /// Ignoruje nedokončené .part. Vrací cestu, nebo null.
    /// </summary>
    public static string? FindExisting(string moviesPath, string seriesPath, QueueItem item)
    {
        try
        {
            string dir;
            string baseName;

            if (item.MediaType == ScMediaType.Episode)
            {
                var clean = CleanTitle(item.SeriesTitle ?? item.Title);
                var season = item.Season ?? 1;
                dir = Path.Combine(seriesPath, Sanitize(clean), $"Season {season:D2}");
                baseName = Sanitize($"{FormatTitle(clean, item.Year)} - S{season:D2}E{item.Episode ?? 1:D2}");
            }
            else
            {
                var movie = Sanitize(FormatTitle(CleanTitle(item.Title), item.Year));
                dir = Path.Combine(moviesPath, movie);
                baseName = movie;
            }

            if (!Directory.Exists(dir))
            {
                return null;
            }

            foreach (var f in Directory.EnumerateFiles(dir, baseName + "*"))
            {
                if (!f.EndsWith(".part", StringComparison.OrdinalIgnoreCase)
                    && !f.EndsWith(".srt", StringComparison.OrdinalIgnoreCase))
                {
                    return f;
                }
            }

            return null;
        }
        catch (Exception)
        {
            return null; // nedostupná cesta apod. — raději stáhnout, než spadnout
        }
    }

    /// <summary>Přípona z URL kra.sk (fallback .mkv).</summary>
    public static string ExtensionFromUrl(string url)
    {
        try
        {
            var path = new Uri(url).AbsolutePath;
            var ext = Path.GetExtension(path);
            if (!string.IsNullOrEmpty(ext) && ext.Length <= 5)
            {
                return ext;
            }
        }
        catch (UriFormatException)
        {
        }

        return ".mkv";
    }

    private static string FormatTitle(string title, int? year)
        => year.HasValue ? $"{title} ({year})" : title;
}
