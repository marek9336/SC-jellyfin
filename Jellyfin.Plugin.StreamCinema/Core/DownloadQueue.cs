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

    private PluginState _state = new();

    public DownloadQueue(string stateFile, Action<string> log)
    {
        _stateFile = stateFile;
        _log = log;
        Load();
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
            var item = _state.Items
                .Where(i => i.Status == QueueItemStatus.Queued)
                .OrderBy(i => i.AddedUtc)
                .FirstOrDefault();
            return item == null ? null : Clone(item);
        }
    }

    public void Add(QueueItem item)
    {
        lock (_lock)
        {
            // Duplicitní ident ve frontě nemá smysl
            if (_state.Items.Any(i => i.Ident == item.Ident
                && i.Status is QueueItemStatus.Queued or QueueItemStatus.Downloading or QueueItemStatus.Done))
            {
                _log($"queue: ident už ve frontě je, přeskakuji ({item.Title})");
                return;
            }

            _state.Items.Add(item);
            SaveLocked();
            _log($"queue: přidáno \"{item.Title}\" ({item.Quality})");
        }
    }

    public bool Remove(Guid id)
    {
        lock (_lock)
        {
            var removed = _state.Items.RemoveAll(i => i.Id == id && i.Status != QueueItemStatus.Downloading);
            if (removed > 0)
            {
                SaveLocked();
                return true;
            }

            return false;
        }
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
