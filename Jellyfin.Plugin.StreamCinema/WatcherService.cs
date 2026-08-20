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

            await SafeDelay(Poll, ct).ConfigureAwait(false);
        }
    }

    private async Task Tick(CancellationToken ct)
    {
        var cfg = Plugin.Instance?.Configuration;
        if (cfg == null || string.IsNullOrEmpty(_state.GetAuthToken()) || string.IsNullOrWhiteSpace(cfg.KraskaUsername))
        {
            return; // bez tokenu/účtu nemá smysl kontrolovat
        }

        var opts = BuildOptions(cfg);

        foreach (var item in _state.Watch.GetAll())
        {
            if (!item.Enabled)
            {
                continue;
            }

            var baseDays = item.HasBacklog ? 1 : Math.Max(1, item.IntervalDays);
            var due = (item.LastCheckedUtc ?? DateTime.MinValue).AddDays(baseDays);
            if (DateTime.UtcNow < due)
            {
                continue;
            }

            try
            {
                await Check(item, cfg, opts, ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "StreamCinema hlídač: kontrola \"{Title}\" selhala", item.Title);
                _state.Watch.Update(item.Id, w => w.LastResult = "chyba: " + ex.Message);
            }

            _state.Watch.Update(item.Id, w => w.LastCheckedUtc = DateTime.UtcNow);
        }
    }

    private async Task Check(WatchItem item, PluginConfiguration cfg, StreamSelectorOptions opts, CancellationToken ct)
    {
        if (item.Type == "movie")
        {
            if (item.MovieGrabbed)
            {
                return;
            }

            var streams = await GetStreams(item.Url, ct).ConfigureAwait(false);
            var (best, reason) = StreamSelector.SelectBest(streams, opts);
            if (best != null)
            {
                Enqueue(item, best, isEp: false, 0, 0);
                _state.Watch.Update(item.Id, w =>
                {
                    w.MovieGrabbed = true;
                    w.Enabled = false;
                    w.LastResult = "staženo: " + reason;
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
        var remaining = Math.Max(0, target - todayCount);

        var queued = 0;
        foreach (var e in newEps)
        {
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
        _state.Watch.Update(item.Id, w =>
        {
            w.Grabbed = grabbed.OrderBy(x => x).ToList();
            w.TodayDate = today;
            w.TodayCount = todayCount + queued;
            w.HasBacklog = still;
            w.LastResult = queued > 0 ? $"zařazeno {queued} epizod dnes" : "žádná nová epizoda ke stažení";
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

    private void Enqueue(WatchItem item, StreamOption best, bool isEp, int season, int episode)
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
            DurationSec = best.DurationSec,
        };
        _state.Queue.Add(qi);
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
