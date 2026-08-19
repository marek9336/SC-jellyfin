namespace Jellyfin.Plugin.StreamCinema.Core;

/// <summary>
/// Skládá cílové cesty podle Jellyfin konvencí pojmenování.
/// Filmy:   <MoviesPath>/Nazev (rok)/Nazev (rok) - [kvalita].mkv
/// Seriály: <SeriesPath>/Nazev (rok)/Season 01/Nazev (rok) - S01E03.mkv
/// </summary>
public static class MediaOrganizer
{
    private static readonly char[] InvalidChars = ['\\', '/', ':', '*', '?', '"', '<', '>', '|'];

    /// <summary>Odstraní znaky nepovolené v názvech souborů a ořízne tečky/mezery na konci.</summary>
    public static string Sanitize(string name)
    {
        var chars = name.Where(c => !InvalidChars.Contains(c) && !char.IsControl(c)).ToArray();
        return new string(chars).Trim().TrimEnd('.', ' ');
    }

    /// <summary>
    /// Cílová cesta pro položku fronty. `extension` včetně tečky (".mkv").
    /// Vrací absolutní cestu; adresáře nevytváří (to dělá volající).
    /// </summary>
    public static string BuildTargetPath(string moviesPath, string seriesPath, QueueItem item, string extension)
    {
        if (item.MediaType == ScMediaType.Episode)
        {
            var series = Sanitize(FormatTitle(item.SeriesTitle ?? item.Title, item.Year));
            var season = item.Season ?? 1;
            var episode = item.Episode ?? 1;
            var file = $"{series} - S{season:D2}E{episode:D2}{extension}";
            return Path.Combine(seriesPath, series, $"Season {season:D2}", Sanitize(file));
        }

        var movie = Sanitize(FormatTitle(item.Title, item.Year));
        var quality = string.IsNullOrWhiteSpace(item.Quality) ? string.Empty : $" - [{Sanitize(item.Quality)}]";
        return Path.Combine(moviesPath, movie, $"{movie}{quality}{extension}");
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
