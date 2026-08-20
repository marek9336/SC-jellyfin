using System.Text.Json;

namespace Jellyfin.Plugin.StreamCinema.Core;

/// <summary>
/// Persistentní seznam sledovaných položek (Hlídač). Vlastní JSON soubor, atomický zápis.
/// Čistý C#, žádná závislost na Jellyfin API.
/// </summary>
public sealed class WatchStore
{
    private static readonly JsonSerializerOptions JsonOpts = new() { WriteIndented = true };

    private readonly string _file;
    private readonly Action<string> _log;
    private readonly object _lock = new();

    private List<WatchItem> _items = new();

    public WatchStore(string file, Action<string> log)
    {
        _file = file;
        _log = log;
        Load();
    }

    public List<WatchItem> GetAll()
    {
        lock (_lock)
        {
            return _items.Select(Clone).ToList();
        }
    }

    /// <summary>Přidá položku (dedup podle Url). Vrací Id, nebo null když už je sledovaná.</summary>
    public Guid? Add(WatchItem item)
    {
        lock (_lock)
        {
            if (_items.Any(i => i.Url == item.Url))
            {
                return null;
            }

            _items.Add(item);
            SaveLocked();
            _log($"watch: přidáno \"{item.Title}\" ({item.Type})");
            return item.Id;
        }
    }

    public bool Remove(Guid id)
    {
        lock (_lock)
        {
            var removed = _items.RemoveAll(i => i.Id == id) > 0;
            if (removed)
            {
                SaveLocked();
            }

            return removed;
        }
    }

    /// <summary>Aplikuje změnu na položku podle Id a uloží.</summary>
    public void Update(Guid id, Action<WatchItem> mutate)
    {
        lock (_lock)
        {
            var item = _items.FirstOrDefault(i => i.Id == id);
            if (item != null)
            {
                mutate(item);
                SaveLocked();
            }
        }
    }

    private void Load()
    {
        try
        {
            if (File.Exists(_file))
            {
                _items = JsonSerializer.Deserialize<List<WatchItem>>(File.ReadAllText(_file)) ?? new();
            }
        }
        catch (Exception ex)
        {
            _log($"watch: stav se nepodařilo načíst ({ex.Message})");
            _items = new();
        }
    }

    private void SaveLocked()
    {
        try
        {
            var dir = Path.GetDirectoryName(_file);
            if (!string.IsNullOrEmpty(dir))
            {
                Directory.CreateDirectory(dir);
            }

            var tmp = _file + ".tmp";
            File.WriteAllText(tmp, JsonSerializer.Serialize(_items, JsonOpts));
            File.Move(tmp, _file, overwrite: true);
        }
        catch (Exception ex)
        {
            _log($"watch: uložení selhalo: {ex.Message}");
        }
    }

    private static WatchItem Clone(WatchItem i) =>
        JsonSerializer.Deserialize<WatchItem>(JsonSerializer.Serialize(i))!;
}
