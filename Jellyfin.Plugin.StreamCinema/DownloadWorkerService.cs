using Jellyfin.Plugin.StreamCinema.Core;
using MediaBrowser.Controller.Library;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.StreamCinema;

/// <summary>
/// Background worker: bere položky z fronty JEDNU PO DRUHÉ a stahuje je
/// s lidským chováním (náhodné pauzy, limit rychlosti, denní strop,
/// časové okno, hlídání volného místa). Viz NOTES.md → Anti-ban pravidla.
/// </summary>
public sealed class DownloadWorkerService : BackgroundService
{
    private static readonly TimeSpan IdlePoll = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan BlockedPoll = TimeSpan.FromMinutes(5);

    private readonly ScState _state;
    private readonly ILibraryManager _libraryManager;
    private readonly ILogger<DownloadWorkerService> _logger;
    private readonly Random _random = new();

    // Začátek okna, pro které už byl použitý náhodný rozptyl startu. Losuje se při
    // každém otevření okna (ne jen jednou za den) — jinak by druhé, třeba noční okno
    // začínalo přesně na hodinu.
    private DateTime? _windowJitterKey;

    // Náhodné zkrácení konce okna — každé okno má vlastní los (klíč = začátek okna),
    // ať dvě okna jednoho dne nekončí se stejným posunem (viz WindowEndJitterMinutes).
    private readonly Dictionary<DateTime, int> _endJitterByWindow = new();

    public DownloadWorkerService(ScState state, ILibraryManager libraryManager, ILogger<DownloadWorkerService> logger)
    {
        _state = state;
        _libraryManager = libraryManager;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        _logger.LogInformation("StreamCinema worker startuje");

        // Krátké zdržení po startu serveru, ať se všechno usadí
        await SafeDelay(TimeSpan.FromSeconds(15), ct).ConfigureAwait(false);

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
                _logger.LogError(ex, "StreamCinema worker: neočekávaná chyba, pokračuji");
                _state.Status.LastMessage = $"Chyba workeru: {ex.Message}";
                await SafeDelay(TimeSpan.FromMinutes(1), ct).ConfigureAwait(false);
            }
        }
    }

    private async Task Tick(CancellationToken ct)
    {
        var cfg = Plugin.Instance?.Configuration;
        var status = _state.Status;
        status.Paused = _state.Queue.WorkerPaused;
        status.DailyBytes = _state.Queue.GetDailyBytes();

        if (cfg == null || _state.Queue.WorkerPaused)
        {
            // Ať GUI neukazuje odpočet „další akce" u pozastaveného workeru.
            status.NextActionUtc = null;
            await _state.Queue.WaitOrWakeAsync(IdlePoll, ct).ConfigureAwait(false);
            return;
        }

        var item = _state.Queue.GetNextQueued();
        if (item == null)
        {
            status.LastMessage = "Fronta je prázdná";
            await _state.Queue.WaitOrWakeAsync(IdlePoll, ct).ConfigureAwait(false);
            return;
        }

        // ── Kontroly před stahováním ──────────────────────────────

        if (string.IsNullOrWhiteSpace(cfg.KraskaUsername))
        {
            status.LastMessage = "Chybí kra.sk účet v nastavení";
            await _state.Queue.WaitOrWakeAsync(BlockedPoll, ct).ConfigureAwait(false);
            return;
        }

        // Výpadek katalogu / helperu: nezkoušet to dokola u každé položky. Čeká se
        // v kuse (typicky hodinu), takže to nevypadá jako stroj bušící do serveru.
        // „Stáhnout teď" výpadek obchází — uživatel si to může vynutit sám.
        var outage = _state.OutageRemaining();
        if (!item.ForceNow && outage > TimeSpan.Zero)
        {
            status.LastMessage =
                $"{_state.OutageReason ?? "Služba je dočasně mimo"} — další pokus v {DateTime.Now.Add(outage):H:mm}";
            status.NextActionUtc = DateTime.UtcNow.Add(outage);
            await _state.Queue.WaitOrWakeAsync(outage, ct).ConfigureAwait(false);
            status.NextActionUtc = null;
            return;
        }

        // Časová okna — globální (od–do + další okna), nebo rozvrh po dnech v týdnu;
        // den může mít víc oken (třeba do 16:00 a pak 22:00–3:00). Konec každého okna
        // se náhodně zkracuje (WindowEndJitterMinutes). „Stáhnout teď" okna obchází.
        var endJitterMax = cfg.WindowEndJitterMinutes;
        Func<DateTime, int> endJitter = start => EndJitterFor(start, endJitterMax);
        if (!item.ForceNow && !Schedule.IsOpen(
                cfg.UseWeeklyWindow, cfg.WeeklyWindow, cfg.WindowFromHour, cfg.WindowToHour,
                endJitter, DateTime.Now, cfg.WindowExtraRanges))
        {
            var today = Schedule.Describe(
                cfg.UseWeeklyWindow, cfg.WeeklyWindow, cfg.WindowFromHour, cfg.WindowToHour,
                DateTime.Now, cfg.WindowExtraRanges);
            var next = Schedule.NextOpen(
                cfg.UseWeeklyWindow, cfg.WeeklyWindow, cfg.WindowFromHour, cfg.WindowToHour,
                DateTime.Now, cfg.WindowExtraRanges);
            status.LastMessage = next != null
                ? $"Mimo časové okno ({today}), další okno {next.Value:d.M. H:mm}"
                : $"Mimo časové okno ({today}), čekám";
            await _state.Queue.WaitOrWakeAsync(BlockedPoll, ct).ConfigureAwait(false);
            return;
        }

        // Rozptyl startu: první stahování v každém otevřeném okně odložit o náhodných
        // 0–N minut, ať to nezačíná přesně na začátku okna (anti-ban) — ráno i v noci.
        // „Stáhnout teď" obchází.
        if (!item.ForceNow
            && (cfg.UseWeeklyWindow || cfg.WindowFromHour != cfg.WindowToHour)
            && cfg.WindowJitterMinutes > 0)
        {
            var windowStart = Schedule.OpenRangeStart(
                cfg.UseWeeklyWindow, cfg.WeeklyWindow, cfg.WindowFromHour, cfg.WindowToHour,
                endJitter, DateTime.Now, cfg.WindowExtraRanges);
            if (windowStart != null && _windowJitterKey != windowStart)
            {
                _windowJitterKey = windowStart;
                var jitter = TimeSpan.FromSeconds(_random.Next(0, cfg.WindowJitterMinutes * 60 + 1));
                if (jitter.TotalSeconds > 5)
                {
                    status.LastMessage = $"Náhodné zpoždění startu {jitter.TotalMinutes:F0} min (anti-ban)";
                    status.NextActionUtc = DateTime.UtcNow.Add(jitter);
                    await _state.Queue.WaitOrWakeAsync(jitter, ct).ConfigureAwait(false);
                    status.NextActionUtc = null;
                    return;
                }
            }
        }

        // Denní strop — platí i pro „Stáhnout teď" (anti-ban pojistka)
        if (cfg.DailyCapGb > 0 && _state.Queue.GetDailyBytes() >= (long)cfg.DailyCapGb * 1024 * 1024 * 1024)
        {
            status.LastMessage = $"Denní limit {cfg.DailyCapGb} GB vyčerpán, pokračuji zítra";
            await _state.Queue.WaitOrWakeAsync(BlockedPoll, ct).ConfigureAwait(false);
            return;
        }

        // Volné místo — platí vždy
        var targetRoot = item.MediaType == ScMediaType.Episode ? cfg.SeriesPath : cfg.MoviesPath;
        var free = DownloadEngine.GetFreeSpace(targetRoot);
        status.FreeSpaceBytes = free;
        if (free > 0 && free < (long)cfg.MinFreeSpaceGb * 1024 * 1024 * 1024)
        {
            status.LastMessage = $"Málo volného místa ({free / (1024 * 1024 * 1024)} GB), stahování pozastaveno";
            await _state.Queue.WaitOrWakeAsync(BlockedPoll, ct).ConfigureAwait(false);
            return;
        }

        // Už staženo ve STEJNÉ kvalitě? Nestahovat znovu (šetří objem = anti-ban).
        // Jiná kvalita (Stalingrad ve 3D vs. 1080p) se stáhne — je to jiná verze.
        // Tlačítko ↻ v historii nastaví Overwrite a kontrola se přeskočí.
        if (!item.Overwrite)
        {
            var dup = MediaOrganizer.FindDuplicate(
                cfg.MoviesPath, cfg.SeriesPath, item, cfg.DuplicateSizeTolerancePercent);
            if (dup != null)
            {
                _logger.LogInformation(
                    "StreamCinema: \"{Title}\" už je v knihovně ve stejné kvalitě ({Path}) — přeskakuji",
                    DisplayTitle(item), dup.Path);
                _state.Queue.Update(item.Id, i =>
                {
                    i.Status = QueueItemStatus.Skipped;
                    i.TargetPath = dup.Path;
                    i.CompletedUtc = DateTime.UtcNow;
                    i.ErrorMessage =
                        $"Stejná kvalita už v knihovně ({dup.Quality ?? "?"}, {Dedup.FormatSize(dup.SizeBytes)}) "
                        + "— nestahuji (↻ stáhne znovu a přepíše)";
                    i.ForceNow = false;
                });
                status.LastMessage = $"Přeskočeno (už staženo): {DisplayTitle(item)}";

                // Nic se nestahovalo → žádná anti-ban pauza, hned na další položku.
                return;
            }
        }

        // ── Stahování ─────────────────────────────────────────────

        _state.Queue.Update(item.Id, i => i.Status = QueueItemStatus.Downloading);
        status.CurrentItemId = item.Id;
        status.CurrentItemTitle = DisplayTitle(item);
        status.LastMessage = null;
        var dlStart = DateTime.UtcNow;

        // Zrušitelný scope: ⏹ Zastavit / Pozastavit umí přerušit běžící přenos
        var itemCt = _state.BeginDownload(ct);

        try
        {
            // Info o předplatném (jako addon: varování při <14 dnech)
            var userInfo = await _state.Kraska.UserInfoAsync(itemCt).ConfigureAwait(false);
            if (userInfo != null)
            {
                status.KraskaDaysLeft = userInfo.DaysLeft;
                if (userInfo.DaysLeft <= 0)
                {
                    throw new KraskaException("kra.sk předplatné vypršelo");
                }
            }

            // Ident: buď přímý (starší tvar), nebo dvoukrokově přes resolve URL streamu
            // (GET katalogu → {version, vN} → "vN:hodnota"), viz ScCatalog.ResolveStreamIdentAsync.
            var ident = item.Ident;
            if (!string.IsNullOrWhiteSpace(item.StreamUrl))
            {
                try
                {
                    ident = await _state.Catalog.ResolveStreamIdentAsync(item.StreamUrl, itemCt).ConfigureAwait(false);
                }
                catch (HttpRequestException hre)
                {
                    var code = (int?)hre.StatusCode ?? 0;
                    if (code >= 500 || code == 0)
                    {
                        // 5xx = server SC je mimo; 0 = vůbec se nedovoláme (DNS/timeout).
                        throw new ScUnavailableException($"Katalog SC nedostupný (HTTP {code})");
                    }

                    throw new KraskaException($"Katalog SC odmítl resolve streamu (HTTP {code})");
                }
            }

            if (string.IsNullOrWhiteSpace(ident))
            {
                throw new KraskaException("Položka nemá ident ani resolve URL streamu");
            }

            // Resolve na stažitelný odkaz. Reálné streamy jsou `v1:` (RSA podpis) → nutný
            // SC helper (sidecar). Přímá kra.sk cesta funguje jen pro `v0:`/titulky.
            string url;
            if (cfg.UseHelper && !string.IsNullOrWhiteSpace(cfg.HelperUrl))
            {
                var sessionId = await _state.Kraska.GetSessionIdAsync(itemCt).ConfigureAwait(false);
                if (string.IsNullOrEmpty(sessionId))
                {
                    throw new KraskaException("Přihlášení ke kra.sk selhalo (helper resolve).");
                }

                try
                {
                    url = await _state.Helper.ResolveAsync(cfg.HelperUrl, ident, sessionId, itemCt).ConfigureAwait(false);
                }
                catch (HttpRequestException hre)
                {
                    var code = (int?)hre.StatusCode ?? 0;
                    var msg = $"SC helper nedostupný (HTTP {code}). Běží sidecar na {cfg.HelperUrl}?";
                    if (code >= 500 || code == 0)
                    {
                        throw new ScUnavailableException(msg);
                    }

                    throw new KraskaException(msg);
                }
            }
            else
            {
                url = await _state.Kraska.ResolveAsync(ident, itemCt).ConfigureAwait(false);
            }

            // Dostali jsme odkaz → služba zase jede, případný zápis o výpadku zahodit.
            _state.ClearOutage();

            var extension = MediaOrganizer.ExtensionFromUrl(url);
            var finalPath = MediaOrganizer.BuildTargetPath(cfg.MoviesPath, cfg.SeriesPath, item, extension);

            // Když na disku leží jiná verze se stejným tagem (stejná kvalita, jiná
            // velikost — duplicitní by se sem nedostala), nepřepisovat ji: „… (2).mkv".
            if (!item.Overwrite)
            {
                finalPath = MediaOrganizer.EnsureUniquePath(finalPath);
            }

            var partPath = finalPath + ".part";

            _logger.LogInformation("StreamCinema: stahuji \"{Title}\" → {Path}", DisplayTitle(item), finalPath);

            var speedLimitBps = cfg.SpeedLimitMbps > 0 ? (long)cfg.SpeedLimitMbps * 1024 * 1024 / 8 : 0;

            var lastSave = DateTime.UtcNow;
            long sessionBytes;
            try
            {
                sessionBytes = await _state.Engine.DownloadAsync(
                    url,
                    partPath,
                    speedLimitBps,
                    (done, total, speed) =>
                    {
                        status.CurrentBytesDone = done;
                        status.CurrentBytesTotal = total;
                        status.CurrentSpeedBps = speed;

                        // Progres do fronty ukládat střídmě (I/O)
                        if ((DateTime.UtcNow - lastSave).TotalSeconds >= 5)
                        {
                            lastSave = DateTime.UtcNow;
                            _state.Queue.Update(item.Id, i =>
                            {
                                i.BytesDone = done;
                                i.BytesTotal = total;
                            });
                        }
                    },
                    itemCt).ConfigureAwait(false);
            }
            catch (HttpRequestException hre)
            {
                throw new KraskaException($"kra.sk file server: HTTP {(int?)hre.StatusCode ?? 0} při stahování (server může být přetížený)");
            }

            File.Move(partPath, finalPath, overwrite: true);
            _state.Queue.AddDailyBytes(sessionBytes);

            // Skutečná rychlost (vč. resolve) pro odhad času ve frontě. Malé kousky
            // (navázání těsně před koncem) by průměr zkreslily, ty se nepočítají.
            var seconds = (DateTime.UtcNow - dlStart).TotalSeconds;
            if (sessionBytes >= 50L * 1024 * 1024 && seconds >= 10)
            {
                _state.Queue.RecordSpeed((long)(sessionBytes / seconds));
            }

            // Titulky (volitelné — jejich selhání nesmí shodit stahování, jako v addonu)
            await TryDownloadSubtitles(item, finalPath, itemCt).ConfigureAwait(false);

            _state.Queue.Update(item.Id, i =>
            {
                i.Status = QueueItemStatus.Done;
                i.CompletedUtc = DateTime.UtcNow;
                i.TargetPath = finalPath;
                i.BytesDone = i.BytesTotal;
                i.ErrorMessage = null;
                i.ForceNow = false;
            });

            _logger.LogInformation("StreamCinema: dokončeno \"{Title}\"", DisplayTitle(item));

            if (cfg.TriggerLibraryScan)
            {
                TriggerLibraryScan();
            }

            // ── Lidská pauza před dalším souborem ─────────────────
            // Když další položka čeká jako „Stáhnout teď", jen krátký oddech.
            var forceNext = _state.Queue.GetNextQueued()?.ForceNow == true;

            // Paranoia mód: další stahování až po uplynutí délky obsahu (mínus doba stahování).
            var paranoia = false;
            TimeSpan pause;
            if (cfg.ParanoiaMode && item.DurationSec is > 0 && !forceNext)
            {
                var elapsed = (DateTime.UtcNow - dlStart).TotalSeconds;
                pause = TimeSpan.FromSeconds(Math.Max(60, item.DurationSec.Value - elapsed));
                paranoia = true;
            }
            else if (forceNext)
            {
                pause = TimeSpan.FromSeconds(_random.Next(20, 61));
            }
            else
            {
                var min = Math.Max(0, cfg.PauseMinMinutes);
                var max = Math.Max(min, cfg.PauseMaxMinutes);
                pause = TimeSpan.FromSeconds(_random.Next(min * 60, max * 60 + 1));
            }

            status.NextActionUtc = DateTime.UtcNow.Add(pause);
            status.LastMessage = paranoia
                ? $"🎭 Paranoia: čekám {pause.TotalMinutes:F0} min (délka obsahu)"
                : $"Hotovo. Pauza {pause.TotalMinutes:F0} min před dalším stahováním";
            _logger.LogInformation("StreamCinema: pauza {Minutes:F0} min", pause.TotalMinutes);
            await _state.Queue.WaitOrWakeAsync(pause, ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            // Restart serveru — položka se při dalším startu vrátí do fronty (Load v DownloadQueue)
            throw;
        }
        catch (OperationCanceledException)
        {
            // Uživatelské zastavení (⏹ / Pozastavit) — vrátit do fronty bez přednosti,
            // .part zůstává, příště se naváže přes HTTP Range
            _logger.LogInformation("StreamCinema: stahování \"{Title}\" zastaveno uživatelem", DisplayTitle(item));
            var stoppedAt = Percent(status.CurrentBytesDone, status.CurrentBytesTotal);
            _state.Queue.Update(item.Id, i =>
            {
                i.Status = QueueItemStatus.Queued;
                i.ForceNow = false;
                i.StoppedPercent = stoppedAt;
            });
            status.LastMessage = "Stahování zastaveno uživatelem";
        }
        catch (ScUnavailableException ex)
        {
            // Výpadek služby není chyba položky — pokus se nezapočítá, jen se dýl počká.
            // Po probuzení platí normální pravidla (časové okno, pauzy), takže mimo
            // aktivní hodiny se stejně nic nezkouší.
            var wait = TimeSpan.FromMinutes(_random.Next(50, 76));
            _state.MarkOutage(wait, ex.Message);
            _state.Queue.Update(item.Id, i =>
            {
                i.Status = QueueItemStatus.Queued;
                i.ErrorMessage = ex.Message + " — počkám a zkusím znovu";
            });

            status.CurrentItemId = null;
            status.CurrentItemTitle = null;
            status.CurrentSpeedBps = 0;
            status.NextActionUtc = DateTime.UtcNow.Add(wait);
            status.LastMessage =
                $"{ex.Message} — další pokus v {DateTime.Now.Add(wait):H:mm} (výpadek se nepočítá do pokusů)";
            _logger.LogWarning(
                "StreamCinema: {Message} — pauza {Minutes:F0} min, pokusy se nepočítají", ex.Message, wait.TotalMinutes);
            await _state.Queue.WaitOrWakeAsync(wait, ct).ConfigureAwait(false);
        }
        catch (KraskaPermanentException ex)
        {
            // Trvalá chyba (vadný/šifrovaný ident) — žádné opakování, rovnou Error.
            _logger.LogWarning("StreamCinema: \"{Title}\" — trvalá chyba, neopakuji: {Msg}", DisplayTitle(item), ex.Message);
            var stoppedAt = Percent(status.CurrentBytesDone, status.CurrentBytesTotal);
            _state.Queue.Update(item.Id, i =>
            {
                i.Status = QueueItemStatus.Error;
                i.ErrorMessage = ex.Message;
                i.StoppedPercent = stoppedAt;
                i.ForceNow = false;
            });
            status.LastMessage = $"Problém: {ex.Message}";
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "StreamCinema: stahování \"{Title}\" selhalo", DisplayTitle(item));
            var failCount = item.FailCount + 1;
            var maxRetries = Math.Max(0, cfg.RetryAttempts);
            var stoppedAt = Percent(status.CurrentBytesDone, status.CurrentBytesTotal);
            var giveUp = failCount > maxRetries;
            _state.Queue.Update(item.Id, i =>
            {
                i.FailCount = failCount;
                i.ErrorMessage = ex.Message;
                i.StoppedPercent = stoppedAt;

                // Po pádu se stahování zkusí navázat (RetryAttempts krát), pak spadne
                // do „Problémů" i s důvodem a procentem, kde se to zaseklo.
                i.Status = giveUp ? QueueItemStatus.Error : QueueItemStatus.Queued;
                if (giveUp)
                {
                    i.ForceNow = false; // definitivní chyba ruší přednost
                }
            });

            status.CurrentItemId = null;
            status.CurrentItemTitle = null;
            status.CurrentSpeedBps = 0;

            if (giveUp)
            {
                _logger.LogWarning(
                    "StreamCinema: \"{Title}\" → Problémy po {N} pokusech (zastaveno na {Pct} %)",
                    DisplayTitle(item), failCount, stoppedAt);

                // Krátký oddech i tady — po sérii neúspěchů nemá smysl hned pálit další
                // požadavek na stejný server (anti-ban).
                var cooldown = TimeSpan.FromSeconds(_random.Next(120, 301));
                status.NextActionUtc = DateTime.UtcNow.Add(cooldown);
                status.LastMessage =
                    $"Problém: „{DisplayTitle(item)}“ se nepovedlo po {failCount} pokusech ({stoppedAt} %) — {ex.Message}";
                await _state.Queue.WaitOrWakeAsync(cooldown, ct).ConfigureAwait(false);
                return;
            }

            // Backoff s jitterem (anti-ban): 1. chyba ~2–3 min, 2. chyba ~8–12 min.
            // Status vyčistit PŘED čekáním, ať GUI neukazuje „Stahuji" u nečinného workeru.
            var baseMinutes = failCount >= 2 ? 8 : 2;
            var backoff = TimeSpan.FromSeconds(_random.Next(baseMinutes * 60, (int)(baseMinutes * 60 * 1.5)));
            status.NextActionUtc = DateTime.UtcNow.Add(backoff);
            status.LastMessage =
                $"Přerušeno na {stoppedAt} % ({ex.Message}) — pokus {failCount + 1} z {maxRetries + 1} za ~{backoff.TotalMinutes:F0} min";
            await _state.Queue.WaitOrWakeAsync(backoff, ct).ConfigureAwait(false);
        }
        finally
        {
            _state.EndDownload();
            status.CurrentItemId = null;
            status.CurrentItemTitle = null;
            status.CurrentSpeedBps = 0;
            status.NextActionUtc = null;
        }
    }

    private async Task TryDownloadSubtitles(QueueItem item, string videoPath, CancellationToken ct)
    {
        try
        {
            var subsIdent = ScCatalog.SubsIdentFromUrl(item.SubsUrl);
            if (subsIdent == null)
            {
                return;
            }

            var subsUrl = await _state.Kraska.ResolveAsync(subsIdent, ct).ConfigureAwait(false);
            var subsPath = MediaOrganizer.BuildSubtitlePath(videoPath, item.SubsLang ?? item.Language);
            await _state.Engine.DownloadAsync(subsUrl, subsPath + ".part", 0, (_, _, _) => { }, ct).ConfigureAwait(false);
            File.Move(subsPath + ".part", subsPath, overwrite: true);
            _logger.LogInformation("StreamCinema: titulky staženy → {Path}", subsPath);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogWarning("StreamCinema: titulky se nepodařilo stáhnout ({Message}), pokračuji bez nich", ex.Message);
        }
    }

    private void TriggerLibraryScan()
    {
        try
        {
            _libraryManager.QueueLibraryScan();
            _logger.LogInformation("StreamCinema: zařazen sken knihovny");
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "StreamCinema: sken knihovny se nepodařilo spustit");
        }
    }

    /// <summary>
    /// Kolik minut se ubere z konce okna, které začíná v `windowStart`. Každé okno má
    /// vlastní los — stahování tak nekončí přesně na hodinu a dvě okna jednoho dne
    /// nekončí se stejným posunem. Worker je jednovláknový, slovník nepotřebuje zámek.
    /// </summary>
    private int EndJitterFor(DateTime windowStart, int maxMinutes)
    {
        if (maxMinutes <= 0)
        {
            return 0;
        }

        if (!_endJitterByWindow.TryGetValue(windowStart, out var minutes))
        {
            if (_endJitterByWindow.Count > 32)
            {
                _endJitterByWindow.Clear(); // staré dny už nikoho nezajímají
            }

            minutes = _random.Next(0, maxMinutes + 1);
            _endJitterByWindow[windowStart] = minutes;
        }

        return minutes;
    }

    private static int Percent(long done, long total) =>
        total > 0 ? (int)Math.Clamp(done * 100 / total, 0L, 100L) : 0;

    private static string DisplayTitle(QueueItem item) =>
        item.MediaType == ScMediaType.Episode
            ? $"{item.SeriesTitle ?? item.Title} S{item.Season:D2}E{item.Episode:D2}"
            : item.Year.HasValue ? $"{item.Title} ({item.Year})" : item.Title;

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
