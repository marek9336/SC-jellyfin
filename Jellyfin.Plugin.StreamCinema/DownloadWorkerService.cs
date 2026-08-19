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

        // Časové okno (From == To → vypnuto). „Stáhnout teď" okno obchází.
        if (!item.ForceNow
            && cfg.WindowFromHour != cfg.WindowToHour
            && !InWindow(DateTime.Now.Hour, cfg.WindowFromHour, cfg.WindowToHour))
        {
            status.LastMessage = $"Mimo časové okno ({cfg.WindowFromHour}:00–{cfg.WindowToHour}:00), čekám";
            await _state.Queue.WaitOrWakeAsync(BlockedPoll, ct).ConfigureAwait(false);
            return;
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

        // ── Stahování ─────────────────────────────────────────────

        _state.Queue.Update(item.Id, i => i.Status = QueueItemStatus.Downloading);
        status.CurrentItemId = item.Id;
        status.CurrentItemTitle = DisplayTitle(item);
        status.LastMessage = null;

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
                    throw new KraskaException($"Katalog SC nedostupný (HTTP {(int?)hre.StatusCode ?? 0}) při resolve streamu");
                }
            }

            if (string.IsNullOrWhiteSpace(ident))
            {
                throw new KraskaException("Položka nemá ident ani resolve URL streamu");
            }

            var url = await _state.Kraska.ResolveAsync(ident, itemCt).ConfigureAwait(false);
            var extension = MediaOrganizer.ExtensionFromUrl(url);
            var finalPath = MediaOrganizer.BuildTargetPath(cfg.MoviesPath, cfg.SeriesPath, item, extension);
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
            TimeSpan pause;
            if (_state.Queue.GetNextQueued()?.ForceNow == true)
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
            status.LastMessage = $"Hotovo. Pauza {pause.TotalMinutes:F0} min před dalším stahováním";
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
            _state.Queue.Update(item.Id, i =>
            {
                i.Status = QueueItemStatus.Queued;
                i.ForceNow = false;
            });
            status.LastMessage = "Stahování zastaveno uživatelem";
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "StreamCinema: stahování \"{Title}\" selhalo", DisplayTitle(item));
            var failCount = item.FailCount + 1;
            _state.Queue.Update(item.Id, i =>
            {
                i.FailCount++;
                i.ErrorMessage = ex.Message;
                // Do 3 pokusů automatický retry (položka zůstane ve frontě), pak Error
                i.Status = i.FailCount >= 3 ? QueueItemStatus.Error : QueueItemStatus.Queued;
                if (i.Status == QueueItemStatus.Error)
                {
                    i.ForceNow = false; // definitivní chyba ruší přednost
                }
            });

            // Backoff s jitterem (anti-ban): 1. chyba ~2–3 min, 2. chyba ~8–12 min.
            // Status vyčistit PŘED čekáním, ať GUI neukazuje „Stahuji" u nečinného workeru.
            var baseMinutes = failCount >= 2 ? 8 : 2;
            var backoff = TimeSpan.FromSeconds(_random.Next(baseMinutes * 60, (int)(baseMinutes * 60 * 1.5)));
            status.CurrentItemId = null;
            status.CurrentItemTitle = null;
            status.CurrentSpeedBps = 0;
            status.NextActionUtc = DateTime.UtcNow.Add(backoff);
            status.LastMessage = $"Chyba: {ex.Message} — další pokus ~{backoff.TotalMinutes:F0} min";
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

    private static bool InWindow(int hour, int from, int to)
    {
        // Okno může přecházet přes půlnoc (např. 22–6)
        return from < to ? hour >= from && hour < to : hour >= from || hour < to;
    }

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
