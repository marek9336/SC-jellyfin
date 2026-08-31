using System.Text.Json;
using System.Text.RegularExpressions;
using Jellyfin.Plugin.StreamCinema.Configuration;
using Jellyfin.Plugin.StreamCinema.Core;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.StreamCinema;

/// <summary>
/// Hlídač: periodicky kontroluje sledované položky. Když se objeví stream splňující
/// priority (dabing/titulky), zařadí ho do fronty. Seriály: pacing X–Y epizod/den
/// (při backlogu re-check denně, jinak dle intervalu položky).
/// </summary>
public sealed class WatcherService : BackgroundService
{
    private static readonly TimeSpan Poll = TimeSpan.FromMinutes(5);
    private static readonly Regex EpRe = new(@"^/Play/([^/]+)/(\d+)/(\d+)$", RegexOptions.Compiled);

    private readonly ScState _state;
    private readonly ILogger<WatcherService> _logger;
    private readonly Random _random = new();

    public WatcherService(ScState state, ILogger<WatcherService> logger)
    {
        _state = state;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        _logger.LogInformation("StreamCinema hlídač startuje");
        await SafeDelay(TimeSpan.FromSeconds(30), ct).ConfigureAwait(false);

        while (!ct.IsCancellationRequested)
        {
            try
            {
                await Tick(ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "StreamCinema hlídač: chyba, pokračuji");
            }

            // Čekání jde přerušit tlačítkem ⚡ „Zkontrolovat teď" v Hlídaných.
            await _state.WaitWatcherAsync(Poll, ct).ConfigureAwait(false);
        }
    }

    private async Task Tick(CancellationToken ct)
    {
        var cfg = Plugin.Instance?.Configuration;
        if (cfg == null || string.IsNullOrEmpty(_state.GetAuthToken()) || string.IsNullOrWhiteSpace(cfg.KraskaUsername))
        {
            return; // bez tokenu/účtu nemá smysl kontrolovat
        }

        // Výpadek katalogu — nezkoušet dokola. Zároveň se u položek NEzapisuje
        // „zkontrolováno", ať kvůli cizímu výpadku nečekají celý svůj interval.
        if (_state.OutageRemaining() > TimeSpan.Zero)
        {
            return;
        }

        var opts = BuildOptions(cfg);

        foreach (var item in _state.Watch.GetAll())
        {
            // ⚡ Vynucená kontrola obchází pauzu, interval i denní limit epizod.
            var force = item.ForceCheck;

            // Dokončeno = nezbývá co zařadit A fronta už z položky nic nestahuje.
            // Tahle kontrola je lokální (bez dotazu do katalogu), takže může běžet
            // každé kolo — položka se do „Dokončených" přesune hned po dostažení.
            if (!force && !item.Completed && NothingLeft(item) && !HasPendingDownloads(item))
            {
                _state.Watch.Update(item.Id, w =>
                {
                    w.Completed = true;
                    w.CompletedUtc ??= DateTime.UtcNow;
                    w.LastResult = w.Type == "series"
                        ? $"staženo {w.Grabbed.Count} {Eps(w.Grabbed.Count)}"
                        : "staženo";
                });
                continue;
            }

            if (!force)
            {
                if (!item.Enabled)
                {
                    continue;
                }

                // Dokončené se kontrolují jen se zapnutým „hlídat nové epizody"
                // (běžící seriál) — a to řidčeji, typicky jednou za měsíc.
                if (item.Completed && !item.KeepWatching)
                {
                    continue;
                }

                var baseDays = item.Completed
                    ? Math.Max(1, item.RecheckIntervalDays ?? cfg.WatchRecheckIntervalDays)
                    : item.HasBacklog ? 1 : Math.Max(1, item.IntervalDays);
                var due = (item.LastCheckedUtc ?? DateTime.MinValue).AddDays(baseDays);
                if (DateTime.UtcNow < due)
                {
                    continue;
                }
            }

            try
            {
                await Check(item, cfg, opts, force, ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex) when (IsUnavailable(ex))
            {
                // Server SC je mimo — počkat a zkusit později. LastCheckedUtc se nemění.
                var wait = TimeSpan.FromMinutes(_random.Next(50, 76));
                _state.MarkOutage(wait, "Katalog SC nedostupný");
                _logger.LogWarning(
                    "StreamCinema hlídač: katalog nedostupný ({Message}), pauza {Minutes:F0} min",
                    ex.Message, wait.TotalMinutes);
                return;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "StreamCinema hlídač: kontrola \"{Title}\" selhala", item.Title);
                _state.Watch.Update(item.Id, w => w.LastResult = "chyba: " + ex.Message);
            }

            _state.Watch.Update(item.Id, w =>
            {
                w.LastCheckedUtc = DateTime.UtcNow;
                w.ForceCheck = false;
            });
        }
    }

    private async Task Check(
        WatchItem item, PluginConfiguration cfg, StreamSelectorOptions globalOpts, bool force, CancellationToken ct)
    {
        // Per-položkový override kvality/velikosti (např. tenhle film chci ve 4K,
        // i když globálně stahuju 1080p).
        var opts = globalOpts;
        if (!string.IsNullOrWhiteSpace(item.MaxQuality) || item.MaxFileSizeGb != null)
        {
            opts = BuildOptions(cfg);
            if (!string.IsNullOrWhiteSpace(item.MaxQuality))
            {
                opts.MaxQuality = item.MaxQuality!;
            }

            if (item.MaxFileSizeGb != null)
            {
                opts.MaxFileSizeGb = item.MaxFileSizeGb.Value;
            }
        }

        // Klíče všeho, co je ve frontě (v jakémkoli stavu) — ať hlídač nezařazuje
        // podruhé to, co už tam leží nebo se stáhlo. Bere se jednou za kontrolu.
        var inQueue = new HashSet<string>(
            _state.Queue.GetAll().Select(Dedup.MediaKey), StringComparer.Ordinal);

        if (item.Type == "movie")
        {
            if (item.MovieGrabbed && !force)
            {
                return;
            }

            // Už stažený nebo už zařazený film znovu do fronty nepatří.
            if (!force && IsHandled(Probe(item, null, null), cfg, inQueue))
            {
                _state.Watch.Update(item.Id, w =>
                {
                    w.MovieGrabbed = true;
                    w.LastResult = "už stažené / ve frontě";
                });
                return;
            }

            var streams = await GetStreams(item.Url, ct).ConfigureAwait(false);
            var (best, reason) = StreamSelector.SelectBest(streams, opts);
            if (best != null)
            {
                var outcome = Enqueue(item, best, isEp: false, 0, 0);
                _state.Watch.Update(item.Id, w =>
                {
                    w.MovieGrabbed = true;
                    w.LastResult = outcome == AddOutcome.Added
                        ? "zařazeno do fronty: " + reason
                        : "už ve frontě / staženo (" + reason + ")";
                });
                _logger.LogInformation("StreamCinema hlídač: film \"{Title}\" → fronta ({Reason})", item.Title, reason);
            }
            else
            {
                _state.Watch.Update(item.Id, w => w.LastResult = "čekám (" + reason + ")");
            }

            return;
        }

        // ── seriál ──
        var eps = new List<(int Season, int Episode, string PlayUrl, string Key)>();
        await CollectEpisodes(item.Url, eps, 0, ct).ConfigureAwait(false);

        var grabbed = new HashSet<string>(item.Grabbed);
        var newEps = eps.Where(e => !grabbed.Contains(e.Key))
            .OrderBy(e => e.Season).ThenBy(e => e.Episode).ToList();

        var today = DateTime.Now.ToString("yyyy-MM-dd");
        var todayCount = item.TodayDate == today ? item.TodayCount : 0;
        var target = _random.Next(
            Math.Max(0, cfg.EpisodesPerDayMin),
            Math.Max(cfg.EpisodesPerDayMin, cfg.EpisodesPerDayMax) + 1);

        // ⚡ Vynuceně: zařadit všechny nalezené epizody naráz. Stahování je i tak
        // po jednom s pauzami a limity — denní strop epizod je jen o zařazování.
        var remaining = force ? int.MaxValue : Math.Max(0, target - todayCount);

        var queued = 0;
        var alreadyHave = 0;
        foreach (var e in newEps)
        {
            // Díl, který už je v knihovně nebo ve frontě, se jen odškrtne a jde se
            // na další — nečerpá denní limit, takže fronta postupuje dál a neplní se
            // pořád tím samým. (Typicky když se epizody stáhly ručně nebo hromadně.)
            if (IsHandled(Probe(item, e.Season, e.Episode), cfg, inQueue))
            {
                grabbed.Add(e.Key);
                alreadyHave++;
                continue;
            }

            if (queued >= remaining)
            {
                break;
            }

            var streams = await GetStreams(e.PlayUrl, ct).ConfigureAwait(false);
            var (best, _) = StreamSelector.SelectBest(streams, opts);
            if (best == null)
            {
                continue; // dabing/stream zatím není → zkusit příště
            }

            Enqueue(item, best, isEp: true, e.Season, e.Episode);
            grabbed.Add(e.Key);
            queued++;
        }

        var still = newEps.Any(e => !grabbed.Contains(e.Key));

        // Hotovo = všechny nalezené epizody jsou zařazené a nic nezbývá. Položka se
        // v GUI přesune do „Dokončené"; dál se kontroluje jen se zapnutým hlídáním
        // nových epizod (u běžících seriálů), a to jednou za měsíc.
        var completed = !still && grabbed.Count > 0 && !HasPendingDownloads(item);
        _state.Watch.Update(item.Id, w =>
        {
            w.Grabbed = grabbed.OrderBy(x => x).ToList();
            w.TodayDate = today;
            w.TodayCount = todayCount + queued;
            w.HasBacklog = still;
            w.Completed = completed;
            if (completed)
            {
                w.CompletedUtc ??= DateTime.UtcNow;
            }
            else
            {
                w.CompletedUtc = null;
            }

            w.LastResult = queued > 0
                ? $"zařazeno {queued} {Eps(queued)} (celkem {grabbed.Count}" + (still ? ", další čekají)" : ")")
                : completed
                    ? $"staženo {grabbed.Count} {Eps(grabbed.Count)}"
                    : still ? "čekám na dabing/stream u dalších epizod" : "žádná nová epizoda";
            if (alreadyHave > 0 && queued == 0 && !completed)
            {
                w.LastResult += $" ({alreadyHave} už staženo/ve frontě)";
            }
        });

        if (queued > 0)
        {
            _logger.LogInformation("StreamCinema hlídač: seriál \"{Title}\" → {N} epizod do fronty", item.Title, queued);
        }
    }

    private async Task CollectEpisodes(
        string url, List<(int, int, string, string)> acc, int depth, CancellationToken ct)
    {
        if (depth > 3)
        {
            return;
        }

        List<(string Url, JsonElement It)> items;
        try
        {
            using var doc = await _state.Catalog.GetAsync(url, null, ct).ConfigureAwait(false);
            items = FindMenuUrls(doc.RootElement);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception)
        {
            return;
        }

        foreach (var (u, _) in items)
        {
            var m = EpRe.Match(u);
            if (m.Success)
            {
                var s = int.Parse(m.Groups[2].Value, System.Globalization.CultureInfo.InvariantCulture);
                var e = int.Parse(m.Groups[3].Value, System.Globalization.CultureInfo.InvariantCulture);
                acc.Add((s, e, u, $"S{s:D2}E{e:D2}"));
            }
            else if (!u.Contains("/Play/", StringComparison.Ordinal))
            {
                await CollectEpisodes(u, acc, depth + 1, ct).ConfigureAwait(false);
            }
        }
    }

    private static List<(string Url, JsonElement It)> FindMenuUrls(JsonElement root)
    {
        var result = new List<(string, JsonElement)>();
        if (root.ValueKind != JsonValueKind.Object)
        {
            return result;
        }

        JsonElement arr;
        if (root.TryGetProperty("menu", out var menu) && menu.ValueKind == JsonValueKind.Array)
        {
            arr = menu;
        }
        else
        {
            arr = default;
            foreach (var prop in root.EnumerateObject())
            {
                if (prop.Value.ValueKind == JsonValueKind.Array && prop.Value.GetArrayLength() > 0)
                {
                    var first = prop.Value[0];
                    if (first.ValueKind == JsonValueKind.Object && first.TryGetProperty("url", out _))
                    {
                        arr = prop.Value;
                        break;
                    }
                }
            }

            if (arr.ValueKind != JsonValueKind.Array)
            {
                return result;
            }
        }

        foreach (var it in arr.EnumerateArray())
        {
            if (it.ValueKind == JsonValueKind.Object
                && it.TryGetProperty("url", out var uEl)
                && uEl.ValueKind == JsonValueKind.String)
            {
                result.Add((uEl.GetString()!, it));
            }
        }

        return result;
    }

    private async Task<List<StreamOption>> GetStreams(string url, CancellationToken ct)
    {
        using var doc = await _state.Catalog.GetAsync(url, null, ct).ConfigureAwait(false);
        return ScCatalog.ParseStreams(doc);
    }

    private AddOutcome Enqueue(WatchItem item, StreamOption best, bool isEp, int season, int episode)
    {
        var title = MediaOrganizer.CleanTitle(item.Title);
        var qi = new QueueItem
        {
            Title = title,
            Year = item.Year,
            MediaType = isEp ? ScMediaType.Episode : ScMediaType.Movie,
            SeriesTitle = isEp ? title : null,
            Season = isEp ? season : null,
            Episode = isEp ? episode : null,
            Ident = best.Ident,
            StreamUrl = best.Url,
            SubsUrl = best.SubsUrl,
            Quality = best.Quality,
            Language = best.Language ?? (best.Languages.Count > 0 ? string.Join(",", best.Languages) : null),
            SizeText = best.SizeText,
            SizeBytes = best.SizeBytes ?? 0,
            DurationSec = best.DurationSec,
        };
        return _state.Queue.Add(qi);
    }

    private static StreamSelectorOptions BuildOptions(PluginConfiguration cfg) => new()
    {
        Lang1 = cfg.PreferredLang1,
        Lang2 = cfg.PreferredLang2,
        Lang3 = cfg.PreferredLang3,
        SkipWithoutPreferredLang = cfg.SkipWithoutPreferredLang,
        PreferSubsWhenForeign = cfg.PreferSubsWhenForeign,
        MaxQuality = cfg.MaxQuality,
        MaxFileSizeGb = cfg.MaxFileSizeGb,
        MaxBitrateMbps = cfg.MaxBitrateMbps,
        CodecPreference = cfg.CodecPreference,
        HdrMode = cfg.HdrMode,
        DvMode = cfg.DvMode,
        AtmosMode = cfg.AtmosMode,
    };

    /// <summary>
    /// Zástupná položka fronty pro jeden titul/epizodu — slouží jen k hledání,
    /// jestli už to není stažené nebo zařazené (žádný stream se do ní neplní).
    /// </summary>
    private static QueueItem Probe(WatchItem item, int? season, int? episode)
    {
        var title = MediaOrganizer.CleanTitle(item.Title);
        return new QueueItem
        {
            Title = title,
            Year = item.Year,
            MediaType = season == null ? ScMediaType.Movie : ScMediaType.Episode,
            SeriesTitle = season == null ? null : title,
            Season = season,
            Episode = episode,
        };
    }

    /// <summary>
    /// Je titul/epizoda už vyřízená — leží v knihovně (jakákoli kvalita), nebo je
    /// v jakémkoli stavu ve frontě? Takovou hlídač znovu nezařazuje a jde na další díl.
    /// </summary>
    private static bool IsHandled(QueueItem probe, PluginConfiguration cfg, HashSet<string> inQueue)
    {
        if (inQueue.Contains(Dedup.MediaKey(probe)))
        {
            return true;
        }

        return MediaOrganizer.FindExistingAll(cfg.MoviesPath, cfg.SeriesPath, probe).Count > 0;
    }

    /// <summary>
    /// Nezbývá už co zařadit? (film stažený / u seriálu žádný backlog).
    /// Neznamená to ještě „dokončeno" — položky můžou pořád čekat ve frontě.
    /// </summary>
    private static bool NothingLeft(WatchItem item) =>
        item.Type == "series" ? !item.HasBacklog && item.Grabbed.Count > 0 : item.MovieGrabbed;

    /// <summary>Čeká/stahuje se ještě něco z téhle sledované položky ve frontě?</summary>
    private bool HasPendingDownloads(WatchItem item)
    {
        var title = MediaOrganizer.CleanTitle(item.Title);
        return _state.Queue.GetAll().Any(q =>
            (q.Status is QueueItemStatus.Queued or QueueItemStatus.Downloading)
            && string.Equals(
                MediaOrganizer.CleanTitle(q.SeriesTitle ?? q.Title), title, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>Je to výpadek služby (5xx / nedovoláme se), ne chyba položky?</summary>
    private static bool IsUnavailable(Exception ex) =>
        ex is ScUnavailableException
        || (ex is HttpRequestException hre && ((int?)hre.StatusCode ?? 0) is 0 or >= 500);

    /// <summary>Český tvar slova „epizoda" podle počtu (1 / 2–4 / 5+).</summary>
    private static string Eps(int n) => n == 1 ? "epizoda" : n >= 2 && n <= 4 ? "epizody" : "epizod";

    private static async Task SafeDelay(TimeSpan delay, CancellationToken ct)
    {
        try
        {
            await Task.Delay(delay, ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
        }
    }
}
