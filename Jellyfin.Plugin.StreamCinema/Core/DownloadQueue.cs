using System.Text.Json;

namespace Jellyfin.Plugin.StreamCinema.Core;

/// <summary>
/// Persistentní fronta stahování + denní počítadlo. Stav žije v jednom JSON souboru,
/// zápis je atomický (temp + move), takže restart Jellyfinu frontu neztratí.
/// Čistý C#, žádná závislost na Jellyfin API.
/// </summary>
public sealed class DownloadQueue
{
    private static readonly JsonSerializerOptions JsonOpts = new() { WriteIndented = true };

    private readonly string _stateFile;
    private readonly Action<string> _log;
    private readonly object _lock = new();

    private readonly SemaphoreSlim _wake = new(0, 1);

    private PluginState _state = new();

    public DownloadQueue(string stateFile, Action<string> log)
    {
        _stateFile = stateFile;
        _log = log;
        Load();
    }

    /// <summary>Probudí worker z čekání (pauza/okno/idle), aby hned zkontroloval frontu.</summary>
    public void Wake()
    {
        try
        {
            _wake.Release();
        }
        catch (SemaphoreFullException)
        {
            // signál už čeká — stačí jeden
        }
    }

    /// <summary>
    /// Čekání workeru přerušitelné signálem Wake(). Vrací true, když bylo čekání
    /// přerušeno signálem (worker má hned znovu zkontrolovat frontu).
    /// </summary>
    public async Task<bool> WaitOrWakeAsync(TimeSpan delay, CancellationToken ct)
    {
        try
        {
            return await _wake.WaitAsync(delay, ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            return false;
        }
    }

    public bool WorkerPaused
    {
        get { lock (_lock) { return _state.WorkerPaused; } }
        set { lock (_lock) { _state.WorkerPaused = value; SaveLocked(); } }
    }

    public List<QueueItem> GetAll()
    {
        lock (_lock)
        {
            return _state.Items.Select(Clone).ToList();
        }
    }

    public QueueItem? GetNextQueued()
    {
        lock (_lock)
        {
            // Pořadí: „Stáhnout teď" → ruční pořadí (▲▼) → čas zařazení
            var item = _state.Items
                .Where(i => i.Status == QueueItemStatus.Queued)
                .OrderByDescending(i => i.ForceNow)
                .ThenBy(i => i.SortIndex)
                .ThenBy(i => i.AddedUtc)
                .FirstOrDefault();

            if (item == null)
            {
                return null;
            }

            // Epizody SEKVENČNĚ: když je na řadě epizoda, vezmi z téhož seriálu
            // nejnižší nestaženou (E01 → E02 → …), ať to vypadá jako reálné sledování.
            if (item.MediaType == ScMediaType.Episode && !item.ForceNow)
            {
                var series = item.SeriesTitle ?? item.Title;
                var first = _state.Items
                    .Where(i => i.Status == QueueItemStatus.Queued
                        && i.MediaType == ScMediaType.Episode
                        && !i.ForceNow
                        && (i.SeriesTitle ?? i.Title) == series)
                    .OrderBy(i => i.Season ?? 0)
                    .ThenBy(i => i.Episode ?? 0)
                    .FirstOrDefault();
                if (first != null)
                {
                    item = first;
                }
            }

            return Clone(item);
        }
    }

    /// <summary>Posun položky ve frontě nahoru/dolů (ruční priorita). Vrací true při změně.</summary>
    public bool Move(Guid id, bool up)
    {
        lock (_lock)
        {
            var queued = _state.Items
                .Where(i => i.Status == QueueItemStatus.Queued)
                .OrderByDescending(i => i.ForceNow)
                .ThenBy(i => i.SortIndex)
                .ThenBy(i => i.AddedUtc)
                .ToList();

            var idx = queued.FindIndex(i => i.Id == id);
            if (idx < 0)
            {
                return false;
            }

            var target = up ? idx - 1 : idx + 1;
            if (target < 0 || target >= queued.Count)
            {
                return false;
            }

            // Přerovnat SortIndex podle nového pořadí (0,1,2… po prohození dvojice)
            (queued[idx], queued[target]) = (queued[target], queued[idx]);
            for (var i = 0; i < queued.Count; i++)
            {
                queued[i].SortIndex = i;
            }

            SaveLocked();
            return true;
        }
    }

    /// <summary>
    /// „Stáhnout teď": označí položku k okamžitému stažení (obejde okno/pauzy)
    /// a probudí worker. Funguje i na chybové položky (retry + přednost).
    /// </summary>
    public bool ForceNow(Guid id)
    {
        lock (_lock)
        {
            var item = _state.Items.FirstOrDefault(i =>
                i.Id == id && i.Status is QueueItemStatus.Queued or QueueItemStatus.Error or QueueItemStatus.Skipped);
            if (item == null)
            {
                return false;
            }

            item.Status = QueueItemStatus.Queued;
            item.ErrorMessage = null;
            item.ForceNow = true;
            SaveLocked();
            _log($"queue: \"{item.Title}\" označeno Stáhnout teď");
        }

        Wake();
        return true;
    }

    public void Add(QueueItem item)
    {
        lock (_lock)
        {
            // Duplicitní stream ve frontě nemá smysl. Klíč = Ident, jinak StreamUrl
            // (položky z autoselectu mají Ident prázdný — nesmí se dedupovat mezi sebou!)
            var key = string.IsNullOrEmpty(item.Ident) ? item.StreamUrl : item.Ident;
            if (!string.IsNullOrEmpty(key) && _state.Items.Any(i =>
                (string.IsNullOrEmpty(i.Ident) ? i.StreamUrl : i.Ident) == key
                && i.Status is QueueItemStatus.Queued or QueueItemStatus.Downloading or QueueItemStatus.Done))
            {
                _log($"queue: stream už ve frontě je, přeskakuji ({item.Title})");
                return;
            }

            // Nová položka jde na konec fronty (ruční pořadí ▲▼ ji pak může posunout)
            item.SortIndex = _state.Items.Count > 0 ? _state.Items.Max(i => i.SortIndex) + 1 : 0;
            _state.Items.Add(item);
            SaveLocked();
            _log($"queue: přidáno \"{item.Title}\" ({item.Quality})");
        }
    }

    public bool Remove(Guid id, bool force = false)
    {
        bool removed;
        lock (_lock)
        {
            removed = _state.Items.RemoveAll(i =>
                i.Id == id && (force || i.Status != QueueItemStatus.Downloading)) > 0;
            if (removed)
            {
                SaveLocked();
            }
        }

        if (removed)
        {
            // Probudit worker: když čekal v backoffu na tuhle položku, ať hned
            // přehodnotí frontu a nezobrazuje „další pokus" u smazané položky
            Wake();
        }

        return removed;
    }

    public bool Retry(Guid id)
    {
        lock (_lock)
        {
            var item = _state.Items.FirstOrDefault(i => i.Id == id && i.Status == QueueItemStatus.Error);
            if (item == null)
            {
                return false;
            }

            item.Status = QueueItemStatus.Queued;
            item.ErrorMessage = null;
            SaveLocked();
            return true;
        }
    }

    /// <summary>Aplikuje změnu na položku podle Id a uloží stav.</summary>
    public void Update(Guid id, Action<QueueItem> mutate)
    {
        lock (_lock)
        {
            var item = _state.Items.FirstOrDefault(i => i.Id == id);
            if (item != null)
            {
                mutate(item);
                SaveLocked();
            }
        }
    }

    /// <summary>Přičte stažené bajty do denního počítadla (reset o půlnoci UTC).</summary>
    public void AddDailyBytes(long bytes)
    {
        lock (_lock)
        {
            var today = DateTime.UtcNow.ToString("yyyy-MM-dd");
            if (_state.DailyDate != today)
            {
                _state.DailyDate = today;
                _state.DailyBytes = 0;
            }

            _state.DailyBytes += bytes;
            SaveLocked();
        }
    }

    public long GetDailyBytes()
    {
        lock (_lock)
        {
            var today = DateTime.UtcNow.ToString("yyyy-MM-dd");
            return _state.DailyDate == today ? _state.DailyBytes : 0;
        }
    }

    private void Load()
    {
        try
        {
            if (File.Exists(_stateFile))
            {
                var json = File.ReadAllText(_stateFile);
                _state = JsonSerializer.Deserialize<PluginState>(json) ?? new PluginState();

                // Po restartu: rozdělané stahování vrátit do fronty (naváže se přes Range)
                foreach (var item in _state.Items.Where(i => i.Status == QueueItemStatus.Downloading))
                {
                    item.Status = QueueItemStatus.Queued;
                }
            }
        }
        catch (Exception ex)
        {
            _log($"queue: stav se nepodařilo načíst ({ex.Message}), začínám s prázdnou frontou");
            _state = new PluginState();
        }
    }

    private void SaveLocked()
    {
        try
        {
            var dir = Path.GetDirectoryName(_stateFile);
            if (!string.IsNullOrEmpty(dir))
            {
                Directory.CreateDirectory(dir);
            }

            var tmp = _stateFile + ".tmp";
            File.WriteAllText(tmp, JsonSerializer.Serialize(_state, JsonOpts));
            File.Move(tmp, _stateFile, overwrite: true);
        }
        catch (Exception ex)
        {
            _log($"queue: uložení stavu selhalo: {ex.Message}");
        }
    }

    private static QueueItem Clone(QueueItem i) =>
        (QueueItem)i.MemberwiseCloneCompat();
}

internal static class QueueItemExtensions
{
    /// <summary>Mělká kopie přes serializaci — QueueItem je čistě datový.</summary>
    public static QueueItem MemberwiseCloneCompat(this QueueItem item)
    {
        var json = JsonSerializer.Serialize(item);
        return JsonSerializer.Deserialize<QueueItem>(json)!;
    }
}
