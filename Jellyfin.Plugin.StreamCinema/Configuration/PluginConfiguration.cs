using MediaBrowser.Model.Plugins;

namespace Jellyfin.Plugin.StreamCinema.Configuration;

/// <summary>
/// Nastavení pluginu. POZOR: Jellyfin ukládá jako plaintext XML
/// v /config/plugins/configurations/ — viz bezpečnostní poznámky v README.
/// </summary>
public class PluginConfiguration : BasePluginConfiguration
{
    // ── kra.sk účet ────────────────────────────────────────────────
    public string KraskaUsername { get; set; } = string.Empty;

    public string KraskaPassword { get; set; } = string.Empty;

    // ── Stream Cinema katalog ─────────────────────────────────────
    /// <summary>
    /// Ručně zadaný X-AUTH-TOKEN (32 znaků). Má přednost před auto-bootstrapem.
    /// Když je prázdný, plugin se pokusí token načíst ze sc.json na kra.sk úložišti.
    /// </summary>
    public string ManualAuthToken { get; set; } = string.Empty;

    /// <summary>Token získaný auto-bootstrapem (cache; NIKDY se negeneruje nový).</summary>
    public string BootstrappedAuthToken { get; set; } = string.Empty;

    /// <summary>Stabilní UUID zařízení (generuje se jednou při prvním startu).</summary>
    public string DeviceUuid { get; set; } = string.Empty;

    public string CatalogLanguage { get; set; } = "cs";

    /// <summary>
    /// User-Agent pro katalog. Formát reálného addonu: xbmc.getUserAgent() + " (lang; verVERZE_ADDONU)".
    /// KRITICKÉ: backend SC podle verze addonu v UA gate-uje resolve endpoint (/ws2) —
    /// smyšlená verze = prázdná 503. Držet v sync s aktuální verzí Kodi addonu!
    /// </summary>
    public string UserAgent { get; set; } = "Kodi/21.3 (Windows NT 10.0; Win64; x64) App_Bitness/64 (cs; ver2.6.7.1+k19)";

    // ── Automatický výběr streamu (⚡ Stáhnout auto + budoucí Trakt watcher) ──
    /// <summary>Jazyk s nejvyšší prioritou (porovnává se s linfo streamu, např. CZ).</summary>
    public string PreferredLang1 { get; set; } = "CZ";

    public string PreferredLang2 { get; set; } = "EN";

    public string PreferredLang3 { get; set; } = "SK";

    /// <summary>Když stream nemá žádný z preferovaných jazyků: true = nestahovat, false = vzít cokoliv.</summary>
    public bool SkipWithoutPreferredLang { get; set; }

    /// <summary>Maximální kvalita (SD/720p/1080p/4K/8K, "-" = bez limitu). Vyšší kvalita se vyřadí.</summary>
    public string MaxQuality { get; set; } = "4K";

    /// <summary>Maximální velikost souboru v GB (0 = bez limitu). Větší soubory se vyřadí.</summary>
    public int MaxFileSizeGb { get; set; } = 30;

    /// <summary>
    /// Maximální bitrate v Mbit/s (0 = bez limitu). Klíčové pro vzdálené sledování:
    /// bitrate souboru nesmí přesáhnout upload linky NASu, jinak buffering/transkódování.
    /// Na rozdíl od velikosti funguje stejně pro 20min epizodu i 3h film.
    /// </summary>
    public int MaxBitrateMbps { get; set; }

    /// <summary>Preferované video kodeky oddělené |, první = nejvyšší priorita.</summary>
    public string CodecPreference { get; set; } = "hevc|h264|av1";

    /// <summary>HDR: "prefer" | "ignore" | "avoid".</summary>
    public string HdrMode { get; set; } = "prefer";

    /// <summary>Dolby Vision: "prefer" | "ignore" | "avoid".</summary>
    public string DvMode { get; set; } = "avoid";

    /// <summary>Dolby Atmos: "prefer" | "ignore" | "avoid".</summary>
    public string AtmosMode { get; set; } = "prefer";

    // ── Cílové cesty (uvnitř Jellyfin kontejneru!) ────────────────
    public string MoviesPath { get; set; } = "/media/movies";

    public string SeriesPath { get; set; } = "/media/tvshows";

    // ── Anti-ban / throttling ─────────────────────────────────────
    /// <summary>Minimální pauza mezi stahováními (minuty).</summary>
    public int PauseMinMinutes { get; set; } = 5;

    /// <summary>Maximální pauza mezi stahováními (minuty).</summary>
    public int PauseMaxMinutes { get; set; } = 15;

    /// <summary>Limit rychlosti v Mbit/s. 0 = bez limitu.</summary>
    public int SpeedLimitMbps { get; set; } = 50;

    /// <summary>Denní strop stažených dat v GB. 0 = vypnuto.</summary>
    public int DailyCapGb { get; set; } = 100;

    /// <summary>Minimální volné místo v GB — pod tuto hodnotu se nezačne další stahování.</summary>
    public int MinFreeSpaceGb { get; set; } = 50;

    /// <summary>Stahovat jen v časovém okně (0-23). Když From == To, okno je vypnuté.</summary>
    public int WindowFromHour { get; set; }

    public int WindowToHour { get; set; }

    /// <summary>Po dokončení stahování spustit sken knihovny.</summary>
    public bool TriggerLibraryScan { get; set; } = true;

    // ── SC Helper sidecar (resolve v1: identů) ────────────────────
    /// <summary>
    /// Resolvovat streamy přes externí SC helper (sidecar). NUTNÉ pro reálné stažení:
    /// stream identy `v1:` jsou RSA-podepsané a rozluští je jen helper Stream Cinemy.
    /// Bez helperu funguje jen hledání/výběr, ne samotné stažení.
    /// </summary>
    public bool UseHelper { get; set; }

    /// <summary>URL sidecar helperu, např. `http://sc-helper:65007` (bez lomítka na konci).</summary>
    public string HelperUrl { get; set; } = string.Empty;
}
