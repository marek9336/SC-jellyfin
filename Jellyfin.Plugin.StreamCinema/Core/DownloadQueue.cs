using System.Text.Json;

namespace Jellyfin.Plugin.StreamCinema.Core;

/// <summary>Výsledek pokusu o zařazení do fronty.</summary>
public enum AddOutcome
{
    /// <summary>Zařazeno.</summary>
    Added,

    /// <summary>Tentýž stream už ve frontě/historii je.</summary>
    DuplicateStream,

    /// <summary>Tentýž film/epizoda ve stejné kvalitě už čeká nebo se právě stahuje.</summary>
    DuplicateInQueue,
}

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
            // Pořadí: „Stáhnout teď" → ruční pořadí (▲▼) → čas zařazení; epizody
            // sekvenčně E01 → E02 → … (viz QueueOrder.PickNext — sdílí ho i odhad času).
            var item = QueueOrder.PickNext(
                _state.Items.Where(i => i.Status == QueueItemStatus.Queued).ToList());
            return item == null ? null : Clone(item);
        }
    }

    /// <summary>
    /// Průměrná rychlost stahování (B/s) z posledních dokončených stahování.
    /// 0 = zatím nic nestaženo. Slouží k odhadu, kdy se co stáhne.
    /// </summary>
    public long AvgSpeedBps
    {
        get { lock (_lock) { return _state.AvgSpeedBps; } }
    }

    /// <summary>
    /// Započítá rychlost dokončeného stahování do klouzavého průměru. Nové stahování
    /// má váhu 30 %, ať jeden pomalý večer odhad hned nerozhodí.
    /// </summary>
    public void RecordSpeed(long bytesPerSecond)
    {
        if (bytesPerSecond <= 0)
        {
            return;
        }

        lock (_lock)
        {
            _state.AvgSpeedBps = _state.AvgSpeedBps <= 0
                ? bytesPerSecond
                : (long)((_state.AvgSpeedBps * 0.7) + (bytesPerSecond * 0.3));
            SaveLocked();
        }
    }

    /// <summary>
    /// „Seřadit seriály": díly jednoho seriálu dá k sobě a seřadí je S01E01 → …
    /// Seriál zůstane tam, kde ve frontě stál jeho první díl; filmy se nehýbou.
    /// Položky s ⚡ předností se nepřesouvají (jdou na řadu první tak jako tak).
    /// Vrací true, když se pořadí změnilo.
    /// </summary>
    public bool GroupSeries()
    {
        lock (_lock)
        {
            var queued = QueuedForReorderLocked();
            var groups = new List<List<QueueItem>>();
            var bySeries = new Dictionary<string, List<QueueItem>>(StringComparer.Ordinal);
            foreach (var item in queued)
            {
                var key = QueueOrder.SeriesKey(item);
                if (key == null)
                {
                    groups.Add(new List<QueueItem> { item });
                    continue;
                }

                if (!bySeries.TryGetValue(key, out var group))
                {
                    group = new List<QueueItem>();
                    bySeries[key] = group;
                    groups.Add(group);
                }

                group.Add(item);
            }

            var ordered = groups
                .SelectMany(g => g.OrderBy(i => i.Season ?? 0).ThenBy(i => i.Episode ?? 0))
                .ToList();
            var changed = ApplyOrderLocked(queued, ordered);
            if (changed)
            {
                _log($"queue: seriály seřazeny ({bySeries.Count} seriálů)");
            }

            return changed;
        }
    }

    /// <summary>
    /// „Upřednostnit seriál": všechny čekající díly seriálu půjdou na začátek fronty
    /// (hned za položky s ⚡ předností), seřazené S01E01 → … Vrací počet dílů.
    /// </summary>
    public int PrioritizeSeries(string seriesTitle)
    {
        var key = QueueOrder.SeriesKey(seriesTitle);
        lock (_lock)
        {
            var queued = QueuedForReorderLocked();
            var episodes = queued
                .Where(i => QueueOrder.SeriesKey(i) == key)
                .OrderBy(i => i.Season ?? 0)
                .ThenBy(i => i.Episode ?? 0)
                .ToList();
            if (episodes.Count == 0)
            {
                return 0;
            }

            ApplyOrderLocked(queued, episodes.Concat(queued.Except(episodes)).ToList());
            _log($"queue: seriál \"{seriesTitle}\" upřednostněn ({episodes.Count} dílů na začátek fronty)");
            return episodes.Count;
        }
    }

    /// <summary>
    /// Pořadí podle Hlídaných: položky, které patří hlídaným titulům, se seřadí podle
    /// pořadí v Hlídaných (výš = dřív), ale jen mezi sebou — zůstanou na místech,
    /// která ve frontě zabíraly. Ručně přidané filmy a díly tak zůstanou, kde jsou.
    /// `groupKeys` = klíče hlídaných v pořadí priority (viz QueueOrder.GroupKey).
    /// </summary>
    public bool ApplyGroupPriority(IReadOnlyList<string> groupKeys)
    {
        var rank = RankMap(groupKeys);
        lock (_lock)
        {
            var queued = QueuedForReorderLocked();
            var slots = new List<int>();
            for (var i = 0; i < queued.Count; i++)
            {
                if (rank.ContainsKey(QueueOrder.GroupKey(queued[i])))
                {
                    slots.Add(i);
                }
            }

            // Stabilní řazení: v rámci jednoho titulu zůstane dosavadní pořadí,
            // epizody stejně jdou sekvenčně (PickNext).
            var sorted = slots
                .Select(i => queued[i])
                .OrderBy(i => rank[QueueOrder.GroupKey(i)])
                .ToList();
            var ordered = queued.ToList();
            for (var n = 0; n < slots.Count; n++)
            {
                ordered[slots[n]] = sorted[n];
            }

            return ApplyOrderLocked(queued, ordered);
        }
    }

    /// <summary>Pořadí → slovník klíč → index (první výskyt vyhrává).</summary>
    public static Dictionary<string, int> RankMap(IReadOnlyList<string> groupKeys)
    {
        var rank = new Dictionary<string, int>(StringComparer.Ordinal);
        for (var i = 0; i < groupKeys.Count; i++)
        {
            rank.TryAdd(groupKeys[i], i);
        }

        return rank;
    }

    /// <summary>Čekající položky bez ⚡ přednosti v aktuálním pořadí (ty jdou přetahovat).</summary>
    private List<QueueItem> QueuedForReorderLocked() =>
        _state.Items
            .Where(i => i.Status == QueueItemStatus.Queued && !i.ForceNow)
            .OrderBy(i => i.SortIndex)
            .ThenBy(i => i.AddedUtc)
            .ToList();

    /// <summary>Přepíše SortIndex podle nového pořadí a uloží. Vrací true při změně.</summary>
    private bool ApplyOrderLocked(List<QueueItem> before, List<QueueItem> after)
    {
        if (before.Select(i => i.Id).SequenceEqual(after.Select(i => i.Id)))
        {
            return false;
        }

        for (var i = 0; i < after.Count; i++)
        {
            after[i].SortIndex = i;
        }

        SaveLocked();
        return true;
    }

    /// <summary>
    /// „Stáhnout znovu": vrátí hotovou/přeskočenou položku zpět do fronty (na konec)
    /// a nastaví Overwrite — nepřeskočí se kvůli existujícímu souboru a ten se přepíše.
    /// </summary>
    public bool Redownload(Guid id)
    {
        bool ok;
        lock (_lock)
        {
            var item = _state.Items.FirstOrDefault(i => i.Id == id
                && i.Status is QueueItemStatus.Done or QueueItemStatus.Skipped or QueueItemStatus.Error);
            if (item == null)
            {
                return false;
            }

            item.Status = QueueItemStatus.Queued;
            item.Overwrite = true;
            item.ErrorMessage = null;
            item.CompletedUtc = null;
            item.TargetPath = null;
            item.BytesDone = 0;
            item.FailCount = 0;
            item.ForceNow = false;
            item.SortIndex = _state.Items.Count > 0 ? _state.Items.Max(i => i.SortIndex) + 1 : 0;
            SaveLocked();
            ok = true;
            _log($"queue: \"{item.Title}\" znovu zařazeno (přepíše existující soubor)");
        }

        Wake();
        return ok;
    }

    /// <summary>
    /// „Zkusit znovu vše" — vrátí všechny položky z Problémů zpět do fronty
    /// a vynuluje jim počítadlo pokusů. Vrací, kolik jich bylo.
    /// </summary>
    public int RetryAll()
    {
        int count;
        lock (_lock)
        {
            var broken = _state.Items.Where(i => i.Status == QueueItemStatus.Error).ToList();
            foreach (var item in broken)
            {
                item.Status = QueueItemStatus.Queued;
                item.ErrorMessage = null;
                item.FailCount = 0;
            }

            count = broken.Count;
            if (count > 0)
            {
                SaveLocked();
                _log($"queue: {count} položek z Problémů vráceno do fronty");
            }
        }

        if (count > 0)
        {
            Wake();
        }

        return count;
    }

    /// <summary>
    /// Zruší „⚡ přednost" (překliknutí, nebo chceš napřed něco jiného). Položka
    /// zůstane tam, kde byla v ručním pořadí, a jde zase přetahovat.
    /// </summary>
    public bool Unforce(Guid id)
    {
        lock (_lock)
        {
            var item = _state.Items.FirstOrDefault(i => i.Id == id && i.ForceNow);
            if (item == null)
            {
                return false;
            }

            item.ForceNow = false;
            SaveLocked();
            _log($"queue: \"{item.Title}\" — zrušena přednost");
            return true;
        }
    }

    /// <summary>Posun položky ve frontě nahoru/dolů (ruční priorita). Vrací true při změně.</summary>
    public bool Move(Guid id, bool up)
    {
        lock (_lock)
        {
            var queued = QueueOrder.ByPriority(
                _state.Items.Where(i => i.Status == QueueItemStatus.Queued)).ToList();

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
    /// Nové pořadí čekajících položek podle seznamu ID (přetažení ve frontě).
    /// Neznámá ID se ignorují; položky, které v seznamu chybí (například přibyly
    /// během přetahování), zůstanou za ním v dosavadním pořadí.
    /// Položky s „přednost" (ForceNow) se nepřetahují — stahují se stejně první.
    /// </summary>
    public bool Reorder(IReadOnlyList<Guid> orderedIds)
    {
        lock (_lock)
        {
            var queued = QueuedForReorderLocked();

            var byId = queued.ToDictionary(i => i.Id);
            var seen = new HashSet<Guid>();
            var ordered = new List<QueueItem>();
            foreach (var id in orderedIds)
            {
                if (byId.TryGetValue(id, out var item) && seen.Add(id))
                {
                    ordered.Add(item);
                }
            }

            if (ordered.Count == 0)
            {
                return false;
            }

            ordered.AddRange(queued.Where(i => !seen.Contains(i.Id)));
            for (var i = 0; i < ordered.Count; i++)
            {
                ordered[i].SortIndex = i;
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

    /// <summary>
    /// Zařadí položku do fronty. Odmítne dvě podoby duplicity:
    /// (1) tentýž stream už ve frontě/historii je, (2) tentýž film/epizoda ve stejné
    /// kvalitě už čeká nebo se právě stahuje — pátý sken téhož dílu nikdo nechce.
    /// Jiná kvalita (3D vs. 1080p) duplicita NENÍ a projde.
    /// <paramref name="groupRank"/> (z Hlídače): pořadí hlídaných titulů — položka se pak
    /// zařadí před díly hlídaných s nižší prioritou, ne až na konec fronty.
    /// </summary>
    public AddOutcome Add(QueueItem item, IReadOnlyDictionary<string, int>? groupRank = null)
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
                return AddOutcome.DuplicateStream;
            }

            // Tentýž obsah ve stejné kvalitě už čeká / se stahuje → nezařazovat znovu.
            var media = Dedup.MediaKey(item);
            var quality = Dedup.NormQuality(item.Quality);
            if (_state.Items.Any(i =>
                (i.Status is QueueItemStatus.Queued or QueueItemStatus.Downloading)
                && Dedup.MediaKey(i) == media
                && Dedup.NormQuality(i.Quality) == quality))
            {
                _log($"queue: \"{item.Title}\" ({item.Quality}) už ve frontě je, přeskakuji");
                return AddOutcome.DuplicateInQueue;
            }

            // Nová položka jde na konec fronty (ruční pořadí ▲▼ ji pak může posunout)
            var sortIndex = _state.Items.Count > 0 ? _state.Items.Max(i => i.SortIndex) + 1 : 0;

            // Hlídaný titul s vyšší prioritou předběhne díly hlídaných s nižší prioritou.
            if (groupRank != null && groupRank.TryGetValue(QueueOrder.GroupKey(item), out var myRank))
            {
                var lower = QueuedForReorderLocked().FirstOrDefault(q =>
                    groupRank.TryGetValue(QueueOrder.GroupKey(q), out var r) && r > myRank);
                if (lower != null)
                {
                    sortIndex = lower.SortIndex;
                    foreach (var q in _state.Items.Where(q => q.SortIndex >= sortIndex))
                    {
                        q.SortIndex++;
                    }
                }
            }

            item.SortIndex = sortIndex;
            _state.Items.Add(item);
            SaveLocked();
            _log($"queue: přidáno \"{item.Title}\" ({item.Quality})");
            return AddOutcome.Added;
        }
    }

    /// <summary>
    /// Vyčistí dokončené (Done) a přeskočené (Skipped) z historie. Vrací počet.
    /// Chybné položky („Problémy") zůstávají — ty se řeší ručně.
    /// </summary>
    public int ClearCompleted()
    {
        lock (_lock)
        {
            var removed = _state.Items.RemoveAll(i =>
                i.Status is QueueItemStatus.Done or QueueItemStatus.Skipped);
            if (removed > 0)
            {
                SaveLocked();
                _log($"queue: vyčištěno {removed} dokončených položek");
            }

            return removed;
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
