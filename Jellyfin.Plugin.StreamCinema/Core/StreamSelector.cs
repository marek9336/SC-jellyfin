using System.Text.RegularExpressions;

namespace Jellyfin.Plugin.StreamCinema.Core;

/// <summary>Uživatelské priority pro automatický výběr streamu. Čistý C#, bez Jellyfin API.</summary>
public sealed class StreamSelectorOptions
{
    public string Lang1 { get; set; } = "CZ";

    public string Lang2 { get; set; } = "EN";

    public string Lang3 { get; set; } = "SK";

    /// <summary>true = bez preferovaného jazyka nestahovat; false = fallback na cokoliv.</summary>
    public bool SkipWithoutPreferredLang { get; set; }

    /// <summary>SD/720p/1080p/4K/8K, "-" nebo prázdné = bez limitu.</summary>
    public string MaxQuality { get; set; } = "4K";

    /// <summary>0 = bez limitu.</summary>
    public int MaxFileSizeGb { get; set; } = 30;

    /// <summary>Max bitrate v Mbit/s, 0 = bez limitu (limit uploadu linky pro vzdálené sledování).</summary>
    public int MaxBitrateMbps { get; set; }

    /// <summary>Kodeky oddělené |, první = nejvyšší priorita.</summary>
    public string CodecPreference { get; set; } = "hevc|h264|av1";

    public string HdrMode { get; set; } = "ignore";

    public string DvMode { get; set; } = "ignore";

    public string AtmosMode { get; set; } = "ignore";

    /// <summary>
    /// Když stream NENÍ v primárním jazyce (jen sekundární/žádný dabing), preferovat
    /// verzi s titulky. Bonus je menší než rozdíl mezi jazykovými úrovněmi, takže
    /// primární dabing vždy vyhraje — rozhoduje jen mezi stejnou jazykovou úrovní.
    /// </summary>
    public bool PreferSubsWhenForeign { get; set; } = true;
}

/// <summary>
/// Automatický výběr streamu podle priorit — port scoring logiky Kodi addonu
/// (SCPlayItem.filter/video_score/audio_score), přizpůsobený stahování:
/// jazyk je dominantní (přebije kvalitu), tvrdé limity (kvalita/velikost)
/// stream vyřadí, kodek a HDR/DV/Atmos jsou jemné preference.
/// </summary>
public static class StreamSelector
{
    private static readonly Dictionary<string, int> QualityLevels = new(StringComparer.OrdinalIgnoreCase)
    {
        ["SD"] = 1,
        ["720p"] = 2,
        ["1080p"] = 3,
        ["3D-SBS"] = 3,
        ["4K"] = 4,
        ["8K"] = 5,
    };

    // Jazyk dominuje (viz addon: LANG_PRIMARY_WEIGHT=100 vs. video bonusy ~25)
    private const int Lang1Weight = 300;
    private const int Lang2Weight = 200;
    private const int Lang3Weight = 100;

    // Titulky u ne-primárního jazyka: menší než mezera mezi jazyk. úrovněmi (100),
    // ale větší než součet video bonusů (~55) → rozhodne mezi stejnou jaz. úrovní.
    private const int SubsBonus = 70;

    private static readonly Regex ExtendedRe = new(
        "extended|director|prodlou|uncut|unrated",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    /// <summary>
    /// Vybere nejlepší stream. Vrací (null, důvod), když žádný nesplní tvrdá pravidla.
    /// </summary>
    public static (StreamOption? Best, string Reason) SelectBest(
        IReadOnlyList<StreamOption> streams, StreamSelectorOptions o)
    {
        if (streams.Count == 0)
        {
            return (null, "žádné streamy");
        }

        var maxLevel = QualityLevels.TryGetValue(o.MaxQuality ?? string.Empty, out var ml) ? ml : int.MaxValue;
        var maxBytes = o.MaxFileSizeGb > 0 ? (long)o.MaxFileSizeGb * 1024 * 1024 * 1024 : long.MaxValue;
        var codecPrefs = (o.CodecPreference ?? string.Empty)
            .Split('|', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(c => c.ToLowerInvariant())
            .ToList();

        StreamOption? best = null;
        double bestScore = double.MinValue;
        var excluded = 0;

        foreach (var s in streams)
        {
            // ── Tvrdé limity: vyřazení ────────────────────────────
            var level = QualityLevels.TryGetValue(s.Quality ?? string.Empty, out var l) ? l : 0;
            if (level > maxLevel)
            {
                excluded++;
                continue;
            }

            if (s.SizeBytes is > 0 && s.SizeBytes.Value > maxBytes)
            {
                excluded++;
                continue;
            }

            // Max bitrate: tvrdý limit (upload linky). Stream bez údaje o bitrate
            // se nevyřazuje — nelze ho posoudit (a nedostane ani bitrate bonus).
            if (o.MaxBitrateMbps > 0 && s.Bitrate is > 0 && s.Bitrate.Value > (long)o.MaxBitrateMbps * 1_000_000)
            {
                excluded++;
                continue;
            }

            var langScore = LangScore(s, o);
            if (langScore == 0 && o.SkipWithoutPreferredLang)
            {
                excluded++;
                continue;
            }

            // ── Skóre ─────────────────────────────────────────────
            double score = langScore;

            // Titulky u ne-primárního jazyka (např. jen anglicky) → preferovat s titulky.
            // Neaplikuje se na primární dabing (ten titulky nepotřebuje).
            if (o.PreferSubsWhenForeign && langScore < Lang1Weight && HasSubtitles(s))
            {
                score += SubsBonus;
            }

            // Kvalita v rámci limitu: vyšší je lepší
            score += level * 10;

            // Bitrate: vyšší = lepší encode (stahujeme, buffering neřešíme), strop 60 Mbit/s
            if (s.Bitrate is > 0)
            {
                score += Math.Min(s.Bitrate.Value / 1e6, 60) / 6; // 0–10 bodů
            }

            // Kodek podle pořadí preference: první +5, druhý +4, … min +1
            var codec = s.Codec?.ToLowerInvariant();
            if (codec != null)
            {
                var idx = codecPrefs.IndexOf(codec);
                if (idx >= 0)
                {
                    score += Math.Max(1, 5 - idx);
                }
            }

            score += ModeScore(o.HdrMode, s.Hdr);
            score += ModeScore(o.DvMode, s.Dv);
            score += ModeScore(o.AtmosMode, s.Atmos);

            // Prodloužené/režisérské verze: mírný bonus (best-effort z metadat)
            var meta = $"{s.Group} {s.Source} {s.VideoInfo}";
            if (ExtendedRe.IsMatch(meta))
            {
                score += 8;
            }

            if (score > bestScore)
            {
                bestScore = score;
                best = s;
            }
        }

        if (best == null)
        {
            return (null, $"žádný z {streams.Count} streamů nesplnil pravidla "
                + $"(vyřazeno {excluded}: kvalita > {o.MaxQuality}, velikost > {o.MaxFileSizeGb} GB"
                + (o.MaxBitrateMbps > 0 ? $", bitrate > {o.MaxBitrateMbps} Mbit/s" : string.Empty)
                + (o.SkipWithoutPreferredLang ? ", chybí preferovaný jazyk)" : ")"));
        }

        return (best, Describe(best, bestScore));
    }

    private static int LangScore(StreamOption s, StreamSelectorOptions o)
    {
        // linfo položky normalizovat: lowercase, bez "+tit" (titulková stopa)
        IEnumerable<string> langs = s.Languages.Count > 0
            ? s.Languages
            : (s.Language ?? string.Empty).Split(',', StringSplitOptions.RemoveEmptyEntries);

        var set = new HashSet<string>(
            langs.Select(x => Norm(x.Replace("+tit", string.Empty, StringComparison.OrdinalIgnoreCase))));

        if (Match(set, o.Lang1))
        {
            return Lang1Weight;
        }

        if (Match(set, o.Lang2))
        {
            return Lang2Weight;
        }

        if (Match(set, o.Lang3))
        {
            return Lang3Weight;
        }

        return 0;

        static bool Match(HashSet<string> set, string lang) =>
            !string.IsNullOrWhiteSpace(lang) && set.Contains(Norm(lang));
    }

    /// <summary>Normalizace jazykového kódu: SC používá "cz", ISO je "cs" → sjednotit.</summary>
    private static string Norm(string lang)
    {
        var l = lang.Trim().ToLowerInvariant();
        return l == "cs" ? "cz" : l;
    }

    /// <summary>Stream má titulky: buď URL titulků, nebo jazyková varianta s "+tit".</summary>
    private static bool HasSubtitles(StreamOption s) =>
        !string.IsNullOrWhiteSpace(s.SubsUrl)
        || s.Languages.Any(l => l.Contains("+tit", StringComparison.OrdinalIgnoreCase));

    private static double ModeScore(string mode, bool has) => mode switch
    {
        "prefer" when has => 15,
        "avoid" when has => -40,
        _ => 0,
    };

    private static string Describe(StreamOption s, double score)
    {
        var parts = new List<string>();
        var langs = s.Languages.Count > 0 ? string.Join(",", s.Languages).ToUpperInvariant() : s.Language;
        if (!string.IsNullOrEmpty(langs))
        {
            parts.Add(langs!);
        }

        if (!string.IsNullOrEmpty(s.Quality))
        {
            parts.Add(s.Quality!);
        }

        if (!string.IsNullOrEmpty(s.SizeText))
        {
            parts.Add(s.SizeText!);
        }

        if (s.Bitrate is > 0)
        {
            parts.Add($"{s.Bitrate.Value / 1e6:F1} Mbit/s");
        }

        if (!string.IsNullOrEmpty(s.Codec))
        {
            parts.Add(s.Codec!.ToUpperInvariant());
        }

        if (s.Hdr)
        {
            parts.Add("HDR");
        }

        if (s.Dv)
        {
            parts.Add("DV");
        }

        if (s.Atmos)
        {
            parts.Add("Atmos");
        }

        if (HasSubtitles(s))
        {
            parts.Add("titulky");
        }

        return $"{string.Join(" · ", parts)} (skóre {score:F0})";
    }
}
