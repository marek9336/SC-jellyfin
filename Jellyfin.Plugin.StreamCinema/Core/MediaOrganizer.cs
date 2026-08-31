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

    // Tag s kvalitou a jazykem na konci názvu souboru: " - [1080p CZ,EN]"
    private static readonly Regex TagRe = new(@"\[([^\]]*)\]", RegexOptions.Compiled);

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

    /// <summary>Jeden už stažený soubor patřící k položce (pro rozpoznání duplicit).</summary>
    public sealed class ExistingFile
    {
        public string Path { get; set; } = string.Empty;

        /// <summary>Kvalita vyčtená z tagu v názvu (" - [1080p CZ]"), nebo null.</summary>
        public string? Quality { get; set; }

        public long SizeBytes { get; set; }
    }

    /// <summary>
    /// Všechny už stažené soubory patřící k položce (jakákoli kvalita/jazyk/přípona).
    /// Hledá podle základu názvu bez tagu „[kvalita jazyk]", ignoruje .part a .srt.
    /// </summary>
    public static List<ExistingFile> FindExistingAll(string moviesPath, string seriesPath, QueueItem item)
    {
        var result = new List<ExistingFile>();
        try
        {
            var (dir, baseName) = ExistingBase(moviesPath, seriesPath, item);
            if (!Directory.Exists(dir))
            {
                return result;
            }

            foreach (var f in Directory.EnumerateFiles(dir, baseName + "*"))
            {
                if (f.EndsWith(".part", StringComparison.OrdinalIgnoreCase)
                    || f.EndsWith(".srt", StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                long size = 0;
                try
                {
                    size = new FileInfo(f).Length;
                }
                catch (Exception)
                {
                    // velikost je jen vodítko — bez ní se rozhoduje podle kvality
                }

                result.Add(new ExistingFile { Path = f, Quality = QualityFromFileName(f), SizeBytes = size });
            }
        }
        catch (Exception)
        {
            // nedostupná cesta apod. — raději stáhnout, než spadnout
        }

        return result;
    }

    /// <summary>
    /// Najde už stažený soubor, který je pro tuhle položku DUPLICITA — tj. stejná
    /// kvalita a (pokud velikosti známe) i velikost v rámci tolerance. Jiná kvalita
    /// (Stalingrad ve 3D vs. 1080p) duplicita NENÍ — takovou verzi chce uživatel stáhnout.
    /// Vrací nalezený soubor, nebo null.
    /// </summary>
    public static ExistingFile? FindDuplicate(
        string moviesPath, string seriesPath, QueueItem item, int tolerancePercent)
    {
        var wanted = Dedup.NormQuality(item.Quality);
        foreach (var f in FindExistingAll(moviesPath, seriesPath, item))
        {
            var have = Dedup.NormQuality(f.Quality);
            if (wanted.Length > 0 && have.Length > 0 && wanted != have)
            {
                continue;
            }

            if (!Dedup.SameSize(item.SizeBytes, f.SizeBytes, tolerancePercent))
            {
                continue;
            }

            return f;
        }

        return null;
    }

    /// <summary>
    /// Cesta, která nepřepíše existující soubor: „… .mkv" → „… (2).mkv".
    /// Používá se, když na disku je jiná verze se stejným tagem (stejná kvalita,
    /// jiná velikost) — obě verze mají zůstat.
    /// </summary>
    public static string EnsureUniquePath(string path)
    {
        if (!File.Exists(path))
        {
            return path;
        }

        var dir = Path.GetDirectoryName(path) ?? string.Empty;
        var name = Path.GetFileNameWithoutExtension(path);
        var ext = Path.GetExtension(path);
        for (var n = 2; n < 100; n++)
        {
            var candidate = Path.Combine(dir, $"{name} ({n}){ext}");
            if (!File.Exists(candidate))
            {
                return candidate;
            }
        }

        return path;
    }

    /// <summary>Složka a základ názvu (bez tagu) pro hledání už stažených souborů.</summary>
    private static (string Dir, string BaseName) ExistingBase(string moviesPath, string seriesPath, QueueItem item)
    {
        if (item.MediaType == ScMediaType.Episode)
        {
            var clean = CleanTitle(item.SeriesTitle ?? item.Title);
            var season = item.Season ?? 1;
            var dir = Path.Combine(seriesPath, Sanitize(clean), $"Season {season:D2}");
            var baseName = Sanitize($"{FormatTitle(clean, item.Year)} - S{season:D2}E{item.Episode ?? 1:D2}");
            return (dir, baseName);
        }

        var movie = Sanitize(FormatTitle(CleanTitle(item.Title), item.Year));
        return (Path.Combine(moviesPath, movie), movie);
    }

    /// <summary>Kvalita z tagu v názvu souboru: „Film (2013) - [1080p CZ].mkv" → „1080p".</summary>
    private static string? QualityFromFileName(string path)
    {
        var name = Path.GetFileNameWithoutExtension(path);
        var matches = TagRe.Matches(name);
        if (matches.Count == 0)
        {
            return null;
        }

        var tag = matches[matches.Count - 1].Groups[1].Value.Trim();
        if (tag.Length == 0)
        {
            return null;
        }

        var space = tag.IndexOf(' ');
        return space > 0 ? tag.Substring(0, space) : tag;
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
