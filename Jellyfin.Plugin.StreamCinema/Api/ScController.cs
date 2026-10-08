using System.ComponentModel.DataAnnotations;
using Jellyfin.Plugin.StreamCinema.Core;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.StreamCinema.Api;

/// <summary>
/// REST API pro konfigurační stránku pluginu. Jen pro adminy.
/// Všechny endpointy jsou pod /Plugins/StreamCinema/...
/// </summary>
[ApiController]
[Authorize(Policy = "RequiresElevation")]
[Route("Plugins/StreamCinema")]
[Produces("application/json")]
public class ScController : ControllerBase
{
    private readonly ScState _state;
    private readonly ILogger<ScController> _logger;

    public ScController(ScState state, ILogger<ScController> logger)
    {
        _state = state;
        _logger = logger;
    }

    /// <summary>DIAGNOSTIKA: zaloguje celý první stream (hledáme, zda ident nekape nešifrovaně jinde).</summary>
    private void LogFirstStream(System.Text.Json.JsonDocument doc)
    {
        if (doc.RootElement.TryGetProperty("strms", out var strms)
            && strms.ValueKind == System.Text.Json.JsonValueKind.Array
            && strms.GetArrayLength() > 0)
        {
            _logger.LogInformation("StreamCinema: strms[0] plný objekt: {Json}", strms[0].GetRawText());
        }
    }

    /// <summary>Test přihlášení ke kra.sk + info o předplatném.</summary>
    [HttpPost("TestLogin")]
    public async Task<ActionResult> TestLogin(CancellationToken ct)
    {
        _state.Kraska.InvalidateSession();
        var ok = await _state.Kraska.LoginAsync(ct).ConfigureAwait(false);
        if (!ok)
        {
            var reason = _state.Kraska.LastError ?? "zkontroluj jméno a heslo.";
            _logger.LogWarning("StreamCinema: TestLogin selhal — {Reason}", reason);
            return Ok(new { success = false, message = "Přihlášení selhalo — " + reason });
        }

        var info = await _state.Kraska.UserInfoAsync(ct).ConfigureAwait(false);
        return Ok(new
        {
            success = true,
            daysLeft = info?.DaysLeft,
            subscribedUntil = info?.SubscribedUntil,
            message = info == null
                ? "Přihlášeno, ale info o předplatném se nepodařilo načíst."
                : $"Přihlášeno. Předplatné: {info.DaysLeft} dní.",
        });
    }

    /// <summary>
    /// Vygeneruje nové UUID zařízení (X-Uuid) ve tvaru, jaký používá Kodi addon.
    /// Neukládá ho — to udělá GUI uložením konfigurace.
    /// </summary>
    [HttpPost("NewDeviceUuid")]
    public ActionResult NewDeviceUuid()
    {
        return Ok(new { uuid = DeviceId.Generate() });
    }

    /// <summary>
    /// Pokus o auto-bootstrap X-AUTH-TOKENu ze sc.json na kra.sk úložišti.
    /// Nikdy negeneruje nový token.
    /// </summary>
    [HttpPost("BootstrapToken")]
    public async Task<ActionResult> BootstrapToken(CancellationToken ct)
    {
        var ok = await _state.TryBootstrapTokenAsync(ct).ConfigureAwait(false);
        return Ok(new
        {
            success = ok,
            message = ok
                ? "Token načten ze zálohy sc.json na tvém kra.sk úložišti."
                : "sc.json se nepodařilo načíst. Zadej token ručně: přihlas se na kra.sk → Úložiště → "
                  + "stáhni soubor sc.json → otevři ho v poznámkovém bloku → zkopíruj 32 znaků do pole „Ruční token“.",
        });
    }

    /// <summary>
    /// Generické procházení katalogu — vrací surovou JSON odpověď SC API.
    /// GUI funguje jako prohlížeč: zobrazí položky a následuje jejich `url`.
    /// </summary>
    [HttpPost("Browse")]
    public async Task<ActionResult> Browse([FromBody] BrowseRequest request, CancellationToken ct)
    {
        if (string.IsNullOrEmpty(_state.GetAuthToken()))
        {
            return Ok(new { error = "missing_token" });
        }

        try
        {
            using var doc = await _state.Catalog
                .GetAsync(request.Path, request.Params, ct).ConfigureAwait(false);
            return Content(doc.RootElement.GetRawText(), "application/json");
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "StreamCinema: browse {Path} selhal", request.Path);
            return Ok(new { error = ex.Message });
        }
    }

    /// <summary>
    /// Procházení katalogu (root "/", kategorie, seznamy jako MARVEL/Netflix).
    /// Statické cesty nepotřebují token, hlubší úrovně jdou přes API.
    /// </summary>
    [HttpPost("Menu")]
    public async Task<ActionResult> Menu([FromBody] BrowseRequest request, CancellationToken ct)
    {
        var path = string.IsNullOrWhiteSpace(request.Path) ? "/" : request.Path;
        try
        {
            var isStatic = request.Params == null
                && await _state.Catalog.IsStaticAsync(path, ct).ConfigureAwait(false);

            if (!isStatic && string.IsNullOrEmpty(_state.GetAuthToken()))
            {
                return Ok(new { error = "missing_token" });
            }

            using var doc = isStatic
                ? await _state.Catalog.GetMenuAsync(path, ct).ConfigureAwait(false)
                : await _state.Catalog.GetAsync(path, request.Params, ct).ConfigureAwait(false);

            return Content(doc.RootElement.GetRawText(), "application/json");
        }
        catch (HttpRequestException hre) when (hre.StatusCode == System.Net.HttpStatusCode.NotFound)
        {
            // Katalog SC nabízí i sekce, které na serveru už neexistují (např. staré
            // seznamy /Search/getList/{id}). Uživateli to říct srozumitelně.
            _logger.LogInformation("StreamCinema: sekce {Path} na serveru SC neexistuje (404)", path);
            return Ok(new
            {
                error = "Tuhle sekci Stream Cinema už nenabízí (404) — bývá to u starých seznamů. "
                    + "Zkus jinou sekci, např. Filmy → Novinky / TOP 100 / Trendy.",
            });
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "StreamCinema: menu {Path} selhalo", path);
            return Ok(new { error = ex.Message });
        }
    }

    /// <summary>Hledání — zkratka nad Browse.</summary>
    [HttpPost("Search")]
    public Task<ActionResult> Search([FromBody] SearchRequest request, CancellationToken ct)
    {
        var type = request.Type == "series" ? "search-series" : "search-movies";
        return Browse(new BrowseRequest
        {
            Path = $"/Search/{type}",
            Params = new Dictionary<string, string> { ["search"] = request.Query, ["id"] = type },
        }, ct);
    }

    /// <summary>
    /// Načte /Play/... a vrátí seznam streamů k výběru.
    /// POZOR: Jellyfin serializuje typované objekty PascalCase, ale GUI čte camelCase
    /// → explicitní projekce na anonymní objekty (kontrakt nezávislý na JSON nastavení).
    /// </summary>
    [HttpPost("Streams")]
    public async Task<ActionResult> Streams([FromBody] BrowseRequest request, CancellationToken ct)
    {
        try
        {
            using var doc = await _state.Catalog.GetAsync(request.Path, request.Params, ct).ConfigureAwait(false);

            // Diagnostika tvaru API: klíče prvního streamu do logu (bez hodnot — žádné tokeny)
            if (doc.RootElement.TryGetProperty("strms", out var strmsEl)
                && strmsEl.ValueKind == System.Text.Json.JsonValueKind.Array
                && strmsEl.GetArrayLength() > 0)
            {
                var keys = string.Join(",", strmsEl[0].EnumerateObject().Select(p => p.Name));
                _logger.LogInformation("StreamCinema: strms[0] klíče: {Keys}", keys);

                // `headers` u streamu může nést hlavičky nutné pro resolve/download — zalogovat obsah
                if (strmsEl[0].TryGetProperty("headers", out var hdrs))
                {
                    _logger.LogInformation("StreamCinema: strms[0].headers: {Headers}", hdrs.GetRawText());
                }
            }

            LogFirstStream(doc);

            var streams = ScCatalog.ParseStreams(doc).Select(s => new
            {
                index = s.Index,
                ident = s.Ident,
                url = s.Url,
                provider = s.Provider,
                language = s.Language,
                languages = s.Languages,
                audioLangs = s.AudioLangs,
                subtitleLangs = s.SubtitleLangs,
                quality = s.Quality,
                sizeText = s.SizeText,
                sizeBytes = s.SizeBytes,
                bitrate = s.Bitrate,
                videoInfo = s.VideoInfo,
                audioInfo = s.AudioInfo,
                subsUrl = s.SubsUrl,
                codec = s.Codec,
                width = s.Width,
                height = s.Height,
                hdr = s.Hdr,
                dv = s.Dv,
                atmos = s.Atmos,
                group = s.Group,
                source = s.Source,
                durationSec = s.DurationSec,
            }).ToList();

            return Ok(new { streams });
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "StreamCinema: načtení streamů {Path} selhalo", request.Path);
            return Ok(new { error = ex.Message });
        }
    }

    /// <summary>Přidá vybraný stream do fronty stahování.</summary>
    [HttpPost("Queue")]
    public ActionResult AddToQueue([FromBody] AddQueueRequest request)
    {
        if (string.IsNullOrWhiteSpace(request.Ident) && string.IsNullOrWhiteSpace(request.StreamUrl))
        {
            return BadRequest(new { error = "Chybí stream (ident ani URL)" });
        }

        var item = new QueueItem
        {
            Title = MediaOrganizer.CleanTitle(request.Title ?? "Neznámý"),
            Year = request.Year,
            MediaType = request.MediaType == "episode" ? ScMediaType.Episode : ScMediaType.Movie,
            SeriesTitle = request.SeriesTitle == null ? null : MediaOrganizer.CleanTitle(request.SeriesTitle),
            Season = request.Season,
            Episode = request.Episode,
            Ident = request.Ident ?? string.Empty,
            StreamUrl = request.StreamUrl,
            SubsUrl = request.SubsUrl,
            SubsLang = request.SubsLang,
            Quality = request.Quality,
            Language = request.Language,
            SizeText = request.SizeText,
            SizeBytes = request.SizeBytes ?? 0,
            DurationSec = request.DurationSec,
            AudioLangs = request.AudioLangs,
            SubtitleLangs = request.SubtitleLangs,
            AudioInfo = request.AudioInfo,
        };

        var outcome = _state.Queue.Add(item);
        return Ok(new
        {
            success = outcome == AddOutcome.Added,
            id = item.Id,
            duplicate = outcome != AddOutcome.Added,
            message = DuplicateMessage(outcome),
        });
    }

    /// <summary>Hláška k výsledku zařazení (null = zařazeno).</summary>
    private static string? DuplicateMessage(AddOutcome outcome) => outcome switch
    {
        AddOutcome.DuplicateStream => "Tenhle stream už ve frontě nebo v historii je.",
        AddOutcome.DuplicateInQueue => "Totéž ve stejné kvalitě už ve frontě čeká nebo se stahuje.",
        _ => null,
    };

    /// <summary>
    /// ⚡ Automatický výběr: načte streamy z /Play/..., vybere nejlepší podle
    /// priorit v nastavení (jazyk → kvalita/velikost → kodek → HDR/DV/Atmos)
    /// a rovnou ho zařadí do fronty. Vrací popis vybraného streamu.
    /// </summary>
    [HttpPost("QueueAuto")]
    public async Task<ActionResult> QueueAuto([FromBody] QueueAutoRequest request, CancellationToken ct)
    {
        try
        {
            using var doc = await _state.Catalog.GetAsync(request.Path, null, ct).ConfigureAwait(false);
            LogFirstStream(doc);
            var streams = ScCatalog.ParseStreams(doc);
            if (streams.Count == 0)
            {
                return Ok(new { error = "Žádné streamy k dispozici." });
            }

            var cfg = Plugin.Instance?.Configuration ?? new Configuration.PluginConfiguration();
            var options = new StreamSelectorOptions
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

            var (best, reason) = StreamSelector.SelectBest(streams, options);
            if (best == null)
            {
                return Ok(new { error = "Autoselect: " + reason });
            }

            var item = new QueueItem
            {
                Title = MediaOrganizer.CleanTitle(request.Title ?? "Neznámý"),
                Year = request.Year,
                MediaType = request.MediaType == "episode" ? ScMediaType.Episode : ScMediaType.Movie,
                SeriesTitle = request.SeriesTitle == null ? null : MediaOrganizer.CleanTitle(request.SeriesTitle),
                Season = request.Season,
                Episode = request.Episode,
                Ident = best.Ident,
                StreamUrl = best.Url,
                SubsUrl = best.SubsUrl,
                Quality = best.Quality,
                Language = best.Languages.Count > 0 ? string.Join(",", best.Languages) : best.Language,
                SizeText = best.SizeText,
                SizeBytes = best.SizeBytes ?? 0,
                DurationSec = best.DurationSec,
                AudioLangs = best.AudioLangs,
                SubtitleLangs = best.SubtitleLangs,
                AudioInfo = best.AudioInfo,
            };

            var outcome = _state.Queue.Add(item);
            _logger.LogInformation("StreamCinema: autoselect \"{Title}\" → {Reason}", item.Title, reason);
            return Ok(new
            {
                success = outcome == AddOutcome.Added,
                id = item.Id,
                picked = reason,
                duplicate = outcome != AddOutcome.Added,
                message = DuplicateMessage(outcome),
            });
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "StreamCinema: QueueAuto {Path} selhal", request.Path);
            return Ok(new { error = ex.Message });
        }
    }

    /// <summary>Obsah fronty. Explicitní camelCase projekce — viz poznámka u Streams.</summary>
    [HttpGet("Queue")]
    public ActionResult GetQueue()
    {
        // Čekající/stahované v pořadí stahování (ForceNow → ruční pořadí → čas),
        // dokončené a chybné pod nimi (nejnovější nahoře).
        var all = _state.Queue.GetAll();
        var cfg = Plugin.Instance?.Configuration ?? new Configuration.PluginConfiguration();
        var (eta, speedBps, speedSource) = EstimateEta(all, cfg);

        var active = all
            .Where(i => i.Status is QueueItemStatus.Queued or QueueItemStatus.Downloading)
            .OrderByDescending(i => i.Status == QueueItemStatus.Downloading)
            .ThenByDescending(i => i.ForceNow)
            .ThenBy(i => i.SortIndex)
            .ThenBy(i => i.AddedUtc);
        var rest = all
            .Where(i => i.Status is not (QueueItemStatus.Queued or QueueItemStatus.Downloading))
            .OrderByDescending(i => i.CompletedUtc ?? i.AddedUtc);

        var items = active.Concat(rest)
            .Select(i => new
            {
                id = i.Id,
                title = i.Title,
                year = i.Year,
                mediaType = i.MediaType.ToString(),
                seriesTitle = i.SeriesTitle,
                season = i.Season,
                episode = i.Episode,
                quality = i.Quality,
                language = i.Language,
                sizeText = i.SizeText,
                durationSec = i.DurationSec,
                audioLangs = i.AudioLangs,
                subtitleLangs = i.SubtitleLangs,
                audioInfo = i.AudioInfo,
                hasSubsFile = !string.IsNullOrEmpty(i.SubsUrl),
                status = i.Status.ToString(),
                forceNow = i.ForceNow,
                errorMessage = i.ErrorMessage,
                bytesDone = i.BytesDone,
                bytesTotal = i.BytesTotal,
                sizeBytes = i.SizeBytes,
                failCount = i.FailCount,
                stoppedPercent = i.StoppedPercent,
                targetPath = i.TargetPath,
                completedUtc = i.CompletedUtc,
                addedUtc = i.AddedUtc,
                etaStartUtc = Lookup(eta.StartUtc, i.Id),
                etaUtc = Lookup(eta.FinishUtc, i.Id),
            })
            .ToList();

        return Ok(new
        {
            items,
            eta = new
            {
                allDoneUtc = eta.AllDoneUtc,
                speedBps,
                speedSource,
                noWindow = eta.NoWindow,
                truncated = eta.Truncated,
                paused = _state.Queue.WorkerPaused,
                pauseMinMinutes = cfg.PauseMinMinutes,
                pauseMaxMinutes = cfg.PauseMaxMinutes,
                paranoia = cfg.ParanoiaMode,
                windowed = cfg.UseWeeklyWindow || cfg.WindowFromHour != cfg.WindowToHour,
                dailyCapGb = cfg.DailyCapGb,
            },
        });
    }

    private static DateTime? Lookup(Dictionary<Guid, DateTime> map, Guid id) =>
        map.TryGetValue(id, out var value) ? value : null;

    /// <summary>
    /// Odhad, kdy se co stáhne (viz Core/QueueEta). Rychlost: průměr posledních
    /// stahování → aktuální rychlost → limit rychlosti → 50 Mbit/s.
    /// </summary>
    private (EtaResult Result, long SpeedBps, string SpeedSource) EstimateEta(
        List<QueueItem> all, Configuration.PluginConfiguration cfg)
    {
        var status = _state.Status;
        var limitBps = cfg.SpeedLimitMbps > 0 ? (long)cfg.SpeedLimitMbps * 1024 * 1024 / 8 : 0;
        var avg = _state.Queue.AvgSpeedBps;

        long speed;
        string source;
        if (avg > 0)
        {
            speed = limitBps > 0 ? Math.Min(avg, limitBps) : avg;
            source = "measured";
        }
        else if (status.CurrentSpeedBps > 0)
        {
            speed = status.CurrentSpeedBps;
            source = "current";
        }
        else if (limitBps > 0)
        {
            speed = limitBps;
            source = "limit";
        }
        else
        {
            speed = 50L * 1024 * 1024 / 8;
            source = "default";
        }

        var current = status.CurrentItemId is Guid currentId
            ? all.FirstOrDefault(i => i.Id == currentId && i.Status == QueueItemStatus.Downloading)
            : null;
        var queued = all.Where(i => i.Status == QueueItemStatus.Queued).ToList();

        // Worker zrovna čeká (pauza mezi soubory, další pokus, výpadek) — od kdy zase pojede.
        DateTime? resumeAt = null;
        var outage = _state.OutageRemaining();
        if (outage > TimeSpan.Zero)
        {
            resumeAt = DateTime.Now.Add(outage);
        }

        if (current == null && status.NextActionUtc is DateTime nextUtc && nextUtc > DateTime.UtcNow)
        {
            var next = nextUtc.ToLocalTime();
            resumeAt = resumeAt == null || next > resumeAt ? next : resumeAt;
        }

        var settings = new EtaSettings
        {
            UseWeeklyWindow = cfg.UseWeeklyWindow,
            WeeklyWindow = cfg.WeeklyWindow,
            WindowFromHour = cfg.WindowFromHour,
            WindowToHour = cfg.WindowToHour,
            WindowExtraRanges = cfg.WindowExtraRanges,
            StartJitterMinutes = cfg.WindowJitterMinutes,
            EndJitterMinutes = cfg.WindowEndJitterMinutes,
            PauseMinMinutes = cfg.PauseMinMinutes,
            PauseMaxMinutes = cfg.PauseMaxMinutes,
            DailyCapBytes = cfg.DailyCapGb > 0 ? (long)cfg.DailyCapGb * 1024 * 1024 * 1024 : 0,
            Paranoia = cfg.ParanoiaMode,
            SpeedBps = speed,
        };

        var result = QueueEta.Estimate(
            queued,
            current,
            status.CurrentBytesDone,
            status.CurrentBytesTotal,
            status.CurrentSpeedBps,
            settings,
            DateTime.Now,
            resumeAt,
            _state.Queue.GetDailyBytes());
        return (result, speed, source);
    }

    /// <summary>
    /// „Seřadit seriály" — díly každého seriálu dá ve frontě k sobě (S01E01 → …).
    /// Seriál zůstane na místě svého prvního dílu, filmy se nehýbou.
    /// </summary>
    [HttpPost("Queue/GroupSeries")]
    public ActionResult GroupSeries()
    {
        return Ok(new { success = _state.Queue.GroupSeries() });
    }

    /// <summary>
    /// „Upřednostnit seriál" — všechny čekající díly na začátek fronty. Když je seriál
    /// v Hlídaných, přesune se tam taky nahoru, ať ho nové díly z hlídače nepředběhnou.
    /// </summary>
    [HttpPost("Queue/PrioritizeSeries")]
    public ActionResult PrioritizeSeries([FromBody] PrioritizeSeriesRequest request)
    {
        if (string.IsNullOrWhiteSpace(request.SeriesTitle))
        {
            return BadRequest(new { error = "Chybí název seriálu" });
        }

        var moved = _state.Queue.PrioritizeSeries(request.SeriesTitle);
        var key = "ep|" + QueueOrder.SeriesKey(request.SeriesTitle);
        var watched = _state.Watch.GetAll().FirstOrDefault(w => QueueOrder.GroupKey(w) == key);
        var watchTop = watched != null && _state.Watch.Move(watched.Id, "top");
        return Ok(new { success = moved > 0, moved, watchTop });
    }

    /// <summary>
    /// Odebere položku z fronty. Když se právě stahuje, přenos se nejdřív utne
    /// (.part zůstává na disku). Funguje i během čekání na další pokus.
    /// </summary>
    [HttpDelete("Queue/{id}")]
    public ActionResult RemoveFromQueue([FromRoute] Guid id)
    {
        _state.CancelDownload(id); // pokud zrovna běží, zastavit přenos
        return Ok(new { success = _state.Queue.Remove(id, force: true) });
    }

    /// <summary>
    /// Vyčistí dokončené a přeskočené z historie (sekce „Dokončeno").
    /// Položky v „Problémech" zůstávají.
    /// </summary>
    [HttpPost("Queue/ClearCompleted")]
    public ActionResult ClearCompleted()
    {
        return Ok(new { removed = _state.Queue.ClearCompleted() });
    }

    /// <summary>
    /// „↻ Zkusit znovu vše" — vrátí do fronty všechno ze sekce Problémy
    /// (typicky po výpadku serveru SC) a zruší případné čekání na konec výpadku.
    /// </summary>
    [HttpPost("Queue/RetryAll")]
    public ActionResult RetryAllProblems()
    {
        _state.ClearOutage();
        return Ok(new { retried = _state.Queue.RetryAll() });
    }

    /// <summary>Zruší „⚡ přednost" u položky (překliknutí / chci napřed něco jiného).</summary>
    [HttpPost("Queue/{id}/Unforce")]
    public ActionResult UnforceItem([FromRoute] Guid id)
    {
        return Ok(new { success = _state.Queue.Unforce(id) });
    }

    /// <summary>Vrátí chybnou položku zpět do fronty.</summary>
    [HttpPost("Queue/{id}/Retry")]
    public ActionResult RetryQueueItem([FromRoute] Guid id)
    {
        return Ok(new { success = _state.Queue.Retry(id) });
    }

    /// <summary>
    /// „Stáhnout teď" — položka dostane přednost, obejde časové okno a pauzy
    /// mezi soubory a worker se probudí. Denní strop a volné místo platí dál.
    /// </summary>
    [HttpPost("Queue/{id}/Now")]
    public ActionResult ForceNowItem([FromRoute] Guid id)
    {
        return Ok(new { success = _state.Queue.ForceNow(id) });
    }

    /// <summary>
    /// ↻ „Stáhnout znovu" — vrátí položku z historie do fronty; existující soubor
    /// se při stažení přepíše.
    /// </summary>
    [HttpPost("Queue/{id}/Redownload")]
    public ActionResult RedownloadQueueItem([FromRoute] Guid id)
    {
        return Ok(new { success = _state.Queue.Redownload(id) });
    }

    /// <summary>
    /// Nové pořadí fronty po přetažení — ID čekajících položek v pořadí,
    /// v jakém se mají stahovat.
    /// </summary>
    [HttpPost("Queue/Reorder")]
    public ActionResult ReorderQueue([FromBody] ReorderQueueRequest request)
    {
        return Ok(new { success = _state.Queue.Reorder(request.Ids ?? new List<Guid>()) });
    }

    /// <summary>Posun položky ve frontě nahoru (▲) / dolů (▼) — ruční priorita.</summary>
    [HttpPost("Queue/{id}/Move/{direction}")]
    public ActionResult MoveQueueItem([FromRoute] Guid id, [FromRoute] string direction)
    {
        return Ok(new { success = _state.Queue.Move(id, direction.Equals("up", StringComparison.OrdinalIgnoreCase)) });
    }

    /// <summary>
    /// ⏹ Zastaví právě běžící stahování této položky. Položka se vrátí do fronty
    /// (bez přednosti), .part zůstává — příště se naváže přes HTTP Range.
    /// </summary>
    [HttpPost("Queue/{id}/Stop")]
    public ActionResult StopQueueItem([FromRoute] Guid id)
    {
        return Ok(new { success = _state.CancelDownload(id) });
    }

    /// <summary>Pozastaví / obnoví worker.</summary>
    [HttpPost("Worker/{action}")]
    public ActionResult WorkerControl([FromRoute][Required] string action)
    {
        switch (action.ToLowerInvariant())
        {
            case "pause":
                _state.Queue.WorkerPaused = true;
                // Pauza zastaví i právě běžící přenos (.part zůstává, naváže se přes Range)
                _state.CancelDownload();
                _state.Status.NextActionUtc = null;
                _state.Status.LastMessage =
                    "Pozastaveno — samo se nic stahovat nebude, dokud nedáš ▶ Obnovit";
                _logger.LogInformation("StreamCinema: worker pozastaven uživatelem");
                return Ok(new { success = true, paused = true });
            case "resume":
                _state.Queue.WorkerPaused = false;
                _state.Status.LastMessage = "Pokračuji…";
                _state.Queue.Wake();
                _logger.LogInformation("StreamCinema: worker obnoven uživatelem");
                return Ok(new { success = true, paused = false });
            default:
                return BadRequest(new { error = "Neznámá akce" });
        }
    }

    /// <summary>Stav workeru pro GUI (poll ~5 s).</summary>
    [HttpGet("Status")]
    public ActionResult GetStatus()
    {
        var s = _state.Status;
        return Ok(new
        {
            paused = _state.Queue.WorkerPaused,
            hasToken = !string.IsNullOrEmpty(_state.GetAuthToken()),
            currentItemId = s.CurrentItemId,
            currentItemTitle = s.CurrentItemTitle,
            currentBytesDone = s.CurrentBytesDone,
            currentBytesTotal = s.CurrentBytesTotal,
            currentSpeedBps = s.CurrentSpeedBps,
            nextActionUtc = s.NextActionUtc,
            lastMessage = s.LastMessage,
            dailyBytes = _state.Queue.GetDailyBytes(),
            freeSpaceBytes = s.FreeSpaceBytes,
            kraskaDaysLeft = s.KraskaDaysLeft,
            outageReason = _state.OutageReason,
            outageUntilUtc = _state.OutageRemaining() > TimeSpan.Zero
                ? DateTime.UtcNow.Add(_state.OutageRemaining())
                : (DateTime?)null,
        });
    }

    // ── Hlídač ────────────────────────────────────────────────────

    /// <summary>Seznam sledovaných položek (camelCase projekce).</summary>
    [HttpGet("Watch")]
    public ActionResult GetWatch()
    {
        // Pořadí seznamu = priorita stahování (první = nejdřív).
        var items = _state.Watch.GetAll().Select(w => new
        {
            id = w.Id,
            type = w.Type,
            title = w.Title,
            year = w.Year,
            url = w.Url,
            intervalDays = w.IntervalDays,
            enabled = w.Enabled,
            hasBacklog = w.HasBacklog,
            lastResult = w.LastResult,
            maxQuality = w.MaxQuality,
            maxFileSizeGb = w.MaxFileSizeGb,
            completed = w.Completed,
            completedUtc = w.CompletedUtc,
            keepWatching = w.KeepWatching,
            recheckIntervalDays = w.RecheckIntervalDays,
            grabbedCount = w.Type == "series" ? w.Grabbed.Count : (w.MovieGrabbed ? 1 : 0),
            lastCheckedUtc = w.LastCheckedUtc,
        }).ToList();
        return Ok(new { items });
    }

    /// <summary>Přidá položku do hlídaných.</summary>
    [HttpPost("Watch")]
    public ActionResult AddWatch([FromBody] AddWatchRequest request)
    {
        if (string.IsNullOrWhiteSpace(request.Url))
        {
            return BadRequest(new { error = "Chybí url" });
        }

        var cfg = Plugin.Instance?.Configuration;
        var item = new WatchItem
        {
            Type = request.Type == "series" ? "series" : "movie",
            Title = request.Title ?? "?",
            Year = request.Year,
            Url = request.Url,
            IntervalDays = request.IntervalDays ?? cfg?.WatchDefaultIntervalDays ?? 7,
        };
        var id = _state.Watch.Add(item);
        return Ok(new { success = id != null, id });
    }

    /// <summary>Úprava sledované položky (interval / zapnutí).</summary>
    [HttpPost("Watch/{id}")]
    public ActionResult UpdateWatch([FromRoute] Guid id, [FromBody] UpdateWatchRequest request)
    {
        _state.Watch.Update(id, w =>
        {
            if (request.IntervalDays is > 0)
            {
                w.IntervalDays = request.IntervalDays.Value;
            }

            if (request.Enabled != null)
            {
                w.Enabled = request.Enabled.Value;
            }

            // prázdný řetězec = zpět na globální nastavení
            if (request.MaxQuality != null)
            {
                w.MaxQuality = string.IsNullOrWhiteSpace(request.MaxQuality) ? null : request.MaxQuality;
            }

            if (request.MaxFileSizeGb != null)
            {
                w.MaxFileSizeGb = request.MaxFileSizeGb < 0 ? null : request.MaxFileSizeGb;
            }

            // Dokončený běžící seriál: hlídat dál nové epizody (jinak se už nekontroluje).
            if (request.KeepWatching != null)
            {
                w.KeepWatching = request.KeepWatching.Value;
            }

            if (request.RecheckIntervalDays != null)
            {
                w.RecheckIntervalDays = request.RecheckIntervalDays < 1 ? null : request.RecheckIntervalDays;
            }
        });
        return Ok(new { success = true });
    }

    /// <summary>
    /// ⚡ Zkontrolovat teď — obejde interval i denní limit epizod a zařadí do fronty
    /// vše, co projde autoselectem. Hlídač se hned probudí.
    /// </summary>
    [HttpPost("Watch/{id}/CheckNow")]
    public ActionResult CheckWatchNow([FromRoute] Guid id)
    {
        var found = false;
        _state.Watch.Update(id, w =>
        {
            w.ForceCheck = true;
            w.Enabled = true;
            w.LastResult = "kontroluji…";
            found = true;
        });

        if (found)
        {
            _state.WakeWatcher();
        }

        return Ok(new { success = found });
    }

    /// <summary>
    /// Pořadí v Hlídaných (▲ / ▼ / ⏫ úplně nahoru). Fronta se hned přerovná: díly
    /// hlídaných titulů se seřadí podle nového pořadí, ručně přidané položky zůstanou.
    /// </summary>
    [HttpPost("Watch/{id}/Move/{direction}")]
    public ActionResult MoveWatch([FromRoute] Guid id, [FromRoute] string direction)
    {
        if (!_state.Watch.Move(id, direction))
        {
            return Ok(new { success = false });
        }

        var keys = _state.Watch.GetAll().Select(QueueOrder.GroupKey).ToList();
        var queueChanged = _state.Queue.ApplyGroupPriority(keys);
        return Ok(new { success = true, queueChanged });
    }

    /// <summary>Odebere sledovanou položku.</summary>
    [HttpDelete("Watch/{id}")]
    public ActionResult RemoveWatch([FromRoute] Guid id)
    {
        return Ok(new { success = _state.Watch.Remove(id) });
    }
}

public class AddWatchRequest
{
    /// <summary>"series" | "movie".</summary>
    public string? Type { get; set; }

    public string? Title { get; set; }

    public int? Year { get; set; }

    [Required]
    public string Url { get; set; } = string.Empty;

    public int? IntervalDays { get; set; }
}

public class UpdateWatchRequest
{
    public int? IntervalDays { get; set; }

    public bool? Enabled { get; set; }

    /// <summary>U dokončené položky: hlídat dál nové epizody.</summary>
    public bool? KeepWatching { get; set; }

    /// <summary>Interval kontroly dokončené položky (dny); &lt; 1 = globální nastavení.</summary>
    public int? RecheckIntervalDays { get; set; }

    /// <summary>Prázdné = zpět na globální nastavení.</summary>
    public string? MaxQuality { get; set; }

    /// <summary>Záporné = zpět na globální nastavení; 0 = bez limitu.</summary>
    public int? MaxFileSizeGb { get; set; }
}

public class BrowseRequest
{
    [Required]
    public string Path { get; set; } = string.Empty;

    public Dictionary<string, string>? Params { get; set; }
}

public class SearchRequest
{
    [Required]
    public string Query { get; set; } = string.Empty;

    /// <summary>"movies" | "series".</summary>
    public string Type { get; set; } = "movies";
}

public class QueueAutoRequest
{
    [Required]
    public string Path { get; set; } = string.Empty;

    public string? Title { get; set; }

    public int? Year { get; set; }

    /// <summary>"movie" | "episode".</summary>
    public string? MediaType { get; set; }

    public string? SeriesTitle { get; set; }

    public int? Season { get; set; }

    public int? Episode { get; set; }
}

public class PrioritizeSeriesRequest
{
    /// <summary>Název seriálu, jak ho má položka fronty (porovnává se normalizovaně).</summary>
    public string? SeriesTitle { get; set; }
}

public class ReorderQueueRequest
{
    /// <summary>ID čekajících položek v novém pořadí (shora dolů).</summary>
    public List<Guid>? Ids { get; set; }
}

public class AddQueueRequest
{
    /// <summary>Přímý kra.sk ident (starší tvar API). Stačí jeden z Ident/StreamUrl.</summary>
    public string? Ident { get; set; }

    /// <summary>Resolve URL streamu z katalogu (běžný tvar) — ident se získá při stahování.</summary>
    public string? StreamUrl { get; set; }

    public string? Title { get; set; }

    public int? Year { get; set; }

    /// <summary>"movie" | "episode".</summary>
    public string? MediaType { get; set; }

    public string? SeriesTitle { get; set; }

    public int? Season { get; set; }

    public int? Episode { get; set; }

    public string? SubsUrl { get; set; }

    public string? SubsLang { get; set; }

    public string? Quality { get; set; }

    public string? Language { get; set; }

    public string? SizeText { get; set; }

    /// <summary>Velikost streamu v bajtech — pro rozpoznání „tenhle soubor už mám".</summary>
    public long? SizeBytes { get; set; }

    public int? DurationSec { get; set; }

    /// <summary>Jazyky zvukových stop (dabing) — jen pro zobrazení ve frontě.</summary>
    public List<string>? AudioLangs { get; set; }

    /// <summary>Ke kterým zvukovým stopám jsou titulky („EN+tit" → „EN").</summary>
    public List<string>? SubtitleLangs { get; set; }

    /// <summary>Popis zvukových stop z katalogu.</summary>
    public string? AudioInfo { get; set; }
}
