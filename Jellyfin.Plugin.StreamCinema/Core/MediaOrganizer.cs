using System.Text.RegularExpressions;

namespace Jellyfin.Plugin.StreamCinema.Core;

/// <summary>
/// Skládá cílové cesty podle Jellyfin konvencí pojmenování.
/// Filmy:   <MoviesPath>/Nazev (rok)/Nazev (rok) - [kvalita jazyk].mkv
/// Seriály: <SeriesPath>/Nazev (rok)/Season 01/Nazev (rok) - S01E03 - [kvalita jazyk].mkv
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
            var series = Sanitize(FormatTitle(CleanTitle(item.SeriesTitle ?? item.Title), item.Year));
            var season = item.Season ?? 1;
            var episode = item.Episode ?? 1;
            var file = $"{series} - S{season:D2}E{episode:D2}{tag}{extension}";
            return Path.Combine(seriesPath, series, $"Season {season:D2}", Sanitize(file));
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
