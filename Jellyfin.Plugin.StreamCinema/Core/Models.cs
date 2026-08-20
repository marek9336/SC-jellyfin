using System.Text.Json.Serialization;

namespace Jellyfin.Plugin.StreamCinema.Core;

/// <summary>Stav položky ve frontě stahování.</summary>
public enum QueueItemStatus
{
    Queued,
    Downloading,
    Done,
    Error,
    Skipped
}

/// <summary>Typ média — určuje cílovou složku.</summary>
public enum ScMediaType
{
    Movie,
    Episode
}

/// <summary>Jedna položka fronty stahování. Serializuje se do JSON stavového souboru.</summary>
public class QueueItem
{
    public Guid Id { get; set; } = Guid.NewGuid();

    /// <summary>Název filmu / seriálu (bez roku).</summary>
    public string Title { get; set; } = string.Empty;

    public int? Year { get; set; }

    public ScMediaType MediaType { get; set; } = ScMediaType.Movie;

    /// <summary>Jen pro epizody.</summary>
    public string? SeriesTitle { get; set; }

    public int? Season { get; set; }

    public int? Episode { get; set; }

    /// <summary>kra.sk ident vybraného streamu (pokud byl znám při zařazení).</summary>
    public string Ident { get; set; } = string.Empty;

    /// <summary>
    /// Resolve URL streamu z katalogu. Když je vyplněná, worker z ní před stažením
    /// získá ident druhým krokem (GET → {version, vN} → "vN:hodnota").
    /// </summary>
    public string? StreamUrl { get; set; }

    /// <summary>URL titulků z katalogu (ident je část za /file/), volitelné.</summary>
    public string? SubsUrl { get; set; }

    /// <summary>Jazyk titulků (pro pojmenování .srt), default cs.</summary>
    public string? SubsLang { get; set; }

    // Popisné údaje streamu — jen pro zobrazení ve frontě.
    public string? Quality { get; set; }
    public string? Language { get; set; }
    public string? SizeText { get; set; }

    public QueueItemStatus Status { get; set; } = QueueItemStatus.Queued;

    public string? ErrorMessage { get; set; }

    public DateTime AddedUtc { get; set; } = DateTime.UtcNow;

    public DateTime? CompletedUtc { get; set; }

    public long BytesTotal { get; set; }

    public long BytesDone { get; set; }

    /// <summary>Výsledná cesta po dokončení.</summary>
    public string? TargetPath { get; set; }

    /// <summary>Počet neúspěšných pokusů (pro automatický retry s odstupem).</summary>
    public int FailCount { get; set; }

    /// <summary>
    /// „Stáhnout teď" — položka má přednost, obejde časové okno a pauzy mezi soubory.
    /// Denní strop a min. volné místo platí dál (bezpečnostní limity).
    /// </summary>
    public bool ForceNow { get; set; }

    /// <summary>Délka obsahu v sekundách (z metadat streamu) — pro „paranoia" pauzu.</summary>
    public int? DurationSec { get; set; }

    /// <summary>
    /// Ruční pořadí ve frontě (nižší = dřív). Nastavuje se tlačítky ▲▼ v GUI.
    /// Při zařazení se dá na konec fronty.
    /// </summary>
    public long SortIndex { get; set; }

    /// <summary>
    /// „Stáhnout znovu": nepřeskakovat kvůli existujícímu souboru a případný
    /// existující soubor přepsat. Nastavuje tlačítko ↻ v historii.
    /// </summary>
    public bool Overwrite { get; set; }
}

/// <summary>
/// Jeden stream z pole `strms` odpovědi /Play.
/// POZOR: streamy NEMAJÍ přímý kra.sk ident — mají `url`, ze které se ident
/// resolvuje druhým krokem (GET url → {version, vN} → "vN:hodnota"). Viz item.py.
/// </summary>
public class StreamOption
{
    public int Index { get; set; }

    /// <summary>Resolve URL streamu (katalogový endpoint) — z něj se získá kra.sk ident.</summary>
    public string Url { get; set; } = string.Empty;

    /// <summary>Přímý kra.sk ident, pokud ho API pošle (starší tvar). Jinak prázdné.</summary>
    public string Ident { get; set; } = string.Empty;

    public string? Provider { get; set; }
    public string? Language { get; set; }
    public string? Quality { get; set; }
    public string? SizeText { get; set; }
    public string? VideoInfo { get; set; }
    public string? AudioInfo { get; set; }
    public string? SubsUrl { get; set; }

    // ── Metadata pro zobrazení a budoucí autoselect (z linfo/stream_info) ──
    public long? SizeBytes { get; set; }
    public long? Bitrate { get; set; }
    public List<string> Languages { get; set; } = new();
    public string? Codec { get; set; }
    public int? Width { get; set; }
    public int? Height { get; set; }
    public bool Hdr { get; set; }
    public bool Dv { get; set; }
    public bool Atmos { get; set; }
    public string? Group { get; set; }
    public string? Source { get; set; }

    /// <summary>Délka v sekundách (stream_info.video.duration), pro paranoia pauzu.</summary>
    public int? DurationSec { get; set; }
}

/// <summary>Sledovaná položka (Hlídač) — film nebo seriál, periodicky kontrolovaná.</summary>
public class WatchItem
{
    public Guid Id { get; set; } = Guid.NewGuid();

    /// <summary>"series" nebo "movie".</summary>
    public string Type { get; set; } = "movie";

    public string Title { get; set; } = string.Empty;

    public int? Year { get; set; }

    /// <summary>Katalogová URL: u filmu /Play/{id}, u seriálu procházecí root.</summary>
    public string Url { get; set; } = string.Empty;

    /// <summary>Interval kontroly ve dnech (per položka).</summary>
    public int IntervalDays { get; set; } = 7;

    /// <summary>
    /// Vlastní max. kvalita jen pro tuhle položku (SD/720p/1080p/4K/8K, "-" = bez limitu).
    /// Prázdné = použít globální nastavení. Pro tituly, které chceš v nejvyšší kvalitě.
    /// </summary>
    public string? MaxQuality { get; set; }

    /// <summary>
    /// Vlastní limit velikosti pro tuhle položku v GB (0 = bez limitu).
    /// null = použít globální. Hodí se při vyšší kvalitě, ať ji globální limit nevyřadí.
    /// </summary>
    public int? MaxFileSizeGb { get; set; }

    public bool Enabled { get; set; } = true;

    /// <summary>Klíče stažených epizod ("S01E03") / u filmu se používá MovieGrabbed.</summary>
    public List<string> Grabbed { get; set; } = new();

    public bool MovieGrabbed { get; set; }

    public DateTime? LastCheckedUtc { get; set; }

    /// <summary>Datum (yyyy-MM-dd) a počet epizod zařazených dnes (limit epizod/den).</summary>
    public string? TodayDate { get; set; }

    public int TodayCount { get; set; }

    /// <summary>Zbývají nezpracované epizody → re-check denně místo dle intervalu.</summary>
    public bool HasBacklog { get; set; }

    public string? LastResult { get; set; }
}

/// <summary>Info o kra.sk účtu.</summary>
public class KraskaUserInfo
{
    public int DaysLeft { get; set; }
    public string? SubscribedUntil { get; set; }
}

/// <summary>Persistentní stav pluginu (fronta + denní počítadlo). Jeden JSON soubor.</summary>
public class PluginState
{
    public List<QueueItem> Items { get; set; } = new();

    /// <summary>Datum (UTC, yyyy-MM-dd), ke kterému platí DailyBytes.</summary>
    public string? DailyDate { get; set; }

    public long DailyBytes { get; set; }

    public bool WorkerPaused { get; set; }
}

/// <summary>Snapshot pro /status endpoint.</summary>
public class WorkerStatus
{
    public bool Paused { get; set; }
    public string? CurrentItemTitle { get; set; }
    public Guid? CurrentItemId { get; set; }
    public long CurrentBytesDone { get; set; }
    public long CurrentBytesTotal { get; set; }
    public long CurrentSpeedBps { get; set; }
    public DateTime? NextActionUtc { get; set; }
    public string? LastMessage { get; set; }
    public long DailyBytes { get; set; }
    public long FreeSpaceBytes { get; set; }
    public int? KraskaDaysLeft { get; set; }
}
