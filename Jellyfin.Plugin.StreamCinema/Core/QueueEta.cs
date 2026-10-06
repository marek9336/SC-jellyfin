namespace Jellyfin.Plugin.StreamCinema.Core;

/// <summary>Vstupy odhadu — nastavení, která ovlivňují, kdy se co stáhne.</summary>
public sealed class EtaSettings
{
    public bool UseWeeklyWindow { get; set; }

    public string? WeeklyWindow { get; set; }

    public int WindowFromHour { get; set; }

    public int WindowToHour { get; set; }

    /// <summary>Rozptyl startu okna (min) — v odhadu se počítá polovina (průměr losu).</summary>
    public int StartJitterMinutes { get; set; }

    /// <summary>Zkrácení konce okna (min) — v odhadu polovina.</summary>
    public int EndJitterMinutes { get; set; }

    public int PauseMinMinutes { get; set; }

    public int PauseMaxMinutes { get; set; }

    /// <summary>Denní strop v bajtech (0 = bez stropu). Počítá se po dnech UTC jako ve workeru.</summary>
    public long DailyCapBytes { get; set; }

    public bool Paranoia { get; set; }

    /// <summary>Předpokládaná rychlost stahování (B/s).</summary>
    public long SpeedBps { get; set; }
}

/// <summary>Výsledek odhadu. Časy jsou v UTC.</summary>
public sealed class EtaResult
{
    /// <summary>Kdy se položka začne stahovat.</summary>
    public Dictionary<Guid, DateTime> StartUtc { get; } = new();

    /// <summary>Kdy bude položka stažená.</summary>
    public Dictionary<Guid, DateTime> FinishUtc { get; } = new();

    /// <summary>Kdy bude stažené všechno (null = nejde spočítat).</summary>
    public DateTime? AllDoneUtc { get; set; }

    /// <summary>Rozvrh nemá v příštím týdnu žádné okno → zbytek fronty nemá odhad.</summary>
    public bool NoWindow { get; set; }

    /// <summary>Odhad přesáhl horizont — u zbytku fronty se nepočítá.</summary>
    public bool Truncated { get; set; }
}

/// <summary>
/// Odhad, kdy se která položka fronty stáhne. Přehraje chování workeru nanečisto:
/// pořadí (QueueOrder.PickNext — vč. sekvenčních epizod), časové okno / rozvrh po
/// dnech, rozptyl startu a konce okna, pauzy mezi soubory, paranoia mód a denní strop.
/// Náhodné hodnoty se berou průměrem, takže je to odhad, ne slib.
/// Nepočítá s výpadky, chybami ani s přeskočením duplicit (to dopředu nejde poznat).
/// Čistý C#, žádná závislost na Jellyfin API.
/// </summary>
public static class QueueEta
{
    /// <summary>Dál než takhle dopředu se neodhaduje (u obří fronty s úzkým oknem).</summary>
    public static readonly TimeSpan Horizon = TimeSpan.FromDays(90);

    /// <summary>Předpokládaná velikost, když ji katalog neposlal.</summary>
    private const long FallbackEpisodeBytes = 1L * 1024 * 1024 * 1024;

    private const long FallbackMovieBytes = 4L * 1024 * 1024 * 1024;

    /// <param name="queued">Čekající položky (stav Queued).</param>
    /// <param name="current">Právě stahovaná položka (nebo null).</param>
    /// <param name="currentBytesDone">Kolik je z ní staženo.</param>
    /// <param name="currentBytesTotal">Celková velikost (0 = neznámá).</param>
    /// <param name="currentSpeedBps">Aktuální rychlost (0 = neznámá).</param>
    /// <param name="s">Nastavení.</param>
    /// <param name="nowLocal">Teď (místní čas serveru — v něm je i časové okno).</param>
    /// <param name="resumeAtLocal">Worker právě čeká (pauza, další pokus, výpadek) do tohohle času.</param>
    /// <param name="dailyBytesToday">Dnes už staženo (pro denní strop).</param>
    public static EtaResult Estimate(
        IReadOnlyList<QueueItem> queued,
        QueueItem? current,
        long currentBytesDone,
        long currentBytesTotal,
        long currentSpeedBps,
        EtaSettings s,
        DateTime nowLocal,
        DateTime? resumeAtLocal,
        long dailyBytesToday)
    {
        var result = new EtaResult();
        var speed = Math.Max(1, s.SpeedBps);
        var windowed = s.UseWeeklyWindow || s.WindowFromHour != s.WindowToHour;
        var endJitter = Math.Max(0, s.EndJitterMinutes) / 2;
        var startJitter = TimeSpan.FromMinutes(Math.Max(0, s.StartJitterMinutes) / 2.0);
        var horizon = nowLocal.Add(Horizon);

        var t = nowLocal;
        var capDay = nowLocal.ToUniversalTime().Date;
        var dayBytes = dailyBytesToday;

        // Rozptyl startu se losuje jednou denně u prvního stahování v okně. Když okno
        // zrovna běží, worker ho má dnes nejspíš za sebou.
        DateTime? jitterDay = IsOpen(s, endJitter, nowLocal) ? nowLocal.Date : null;

        var pending = queued.ToList();
        QueueItem? previous = null;
        var previousDuration = TimeSpan.Zero;

        if (current != null)
        {
            var total = currentBytesTotal > 0 ? currentBytesTotal : SizeOf(current, queued);
            var remaining = Math.Max(0, total - currentBytesDone);
            var curSpeed = currentSpeedBps > 0 ? currentSpeedBps : speed;
            var finish = t.AddSeconds((double)remaining / curSpeed);
            result.FinishUtc[current.Id] = finish.ToUniversalTime();
            CountBytes(finish, remaining, ref capDay, ref dayBytes);
            t = finish;
            previous = current;
            previousDuration = TimeSpan.FromSeconds((double)total / curSpeed);
        }
        else if (resumeAtLocal > t && pending.Count > 0 && QueueOrder.PickNext(pending)?.ForceNow != true)
        {
            // Pauza mezi soubory / čekání na další pokus / výpadek. ⚡ přednost ji přeruší.
            t = resumeAtLocal.Value;
        }

        var guard = 0;
        while (pending.Count > 0 && guard++ < 10_000)
        {
            var next = QueueOrder.PickNext(pending)!;

            // Pauza po předchozím souboru (průměr losu), paranoia = délka obsahu.
            if (previous != null)
            {
                t = t.Add(PauseAfter(previous, previousDuration, next.ForceNow, s));
                previous = null;
            }

            if (t > horizon)
            {
                result.Truncated = true;
                break;
            }

            if (!next.ForceNow)
            {
                // Časové okno — počkat na nejbližší otevření.
                for (var w = 0; w < 30 && !IsOpen(s, endJitter, t); w++)
                {
                    var open = Schedule.NextOpen(s.UseWeeklyWindow, s.WeeklyWindow, s.WindowFromHour, s.WindowToHour, t);
                    if (open == null)
                    {
                        break;
                    }

                    t = open.Value;
                }

                if (!IsOpen(s, endJitter, t))
                {
                    result.NoWindow = true;
                    break;
                }

                // Rozptyl startu: jednou denně u prvního stahování (jako worker).
                if (windowed && jitterDay != t.Date)
                {
                    jitterDay = t.Date;
                    t = t.Add(startJitter);
                }
            }

            // Denní strop (dny UTC, jako ve workeru): když je vyčerpaný, čeká se
            // na půlnoc UTC a pak znovu na okno.
            if (s.DailyCapBytes > 0)
            {
                var day = t.ToUniversalTime().Date;
                if (day != capDay)
                {
                    capDay = day;
                    dayBytes = 0;
                }

                if (dayBytes >= s.DailyCapBytes)
                {
                    t = DateTime.SpecifyKind(capDay.AddDays(1), DateTimeKind.Utc).ToLocalTime();
                    continue;
                }
            }

            var size = RemainingOf(next, queued);
            var duration = TimeSpan.FromSeconds((double)size / speed);
            result.StartUtc[next.Id] = t.ToUniversalTime();
            t = t.Add(duration);
            result.FinishUtc[next.Id] = t.ToUniversalTime();

            // Worker přičítá bajty k dennímu počítadlu až po dokončení souboru.
            CountBytes(t, size, ref capDay, ref dayBytes);

            pending.Remove(next);
            previous = next;
            previousDuration = duration;
        }

        if (pending.Count == 0)
        {
            result.AllDoneUtc = result.FinishUtc.Count > 0 ? result.FinishUtc.Values.Max() : null;
        }

        return result;
    }

    private static bool IsOpen(EtaSettings s, int endJitter, DateTime t) =>
        Schedule.IsOpen(s.UseWeeklyWindow, s.WeeklyWindow, s.WindowFromHour, s.WindowToHour, endJitter, t);

    /// <summary>Pauza po staženém souboru — stejná pravidla jako ve workeru, náhoda průměrem.</summary>
    private static TimeSpan PauseAfter(QueueItem done, TimeSpan downloadTime, bool nextIsForced, EtaSettings s)
    {
        if (nextIsForced)
        {
            return TimeSpan.FromSeconds(40); // worker: 20–60 s
        }

        if (s.Paranoia && done.DurationSec is > 0)
        {
            return TimeSpan.FromSeconds(Math.Max(60, done.DurationSec.Value - downloadTime.TotalSeconds));
        }

        var min = Math.Max(0, s.PauseMinMinutes);
        var max = Math.Max(min, s.PauseMaxMinutes);
        return TimeSpan.FromMinutes((min + max) / 2.0);
    }

    private static void CountBytes(DateTime atLocal, long bytes, ref DateTime capDay, ref long dayBytes)
    {
        var day = atLocal.ToUniversalTime().Date;
        if (day != capDay)
        {
            capDay = day;
            dayBytes = 0;
        }

        dayBytes += bytes;
    }

    /// <summary>Kolik zbývá stáhnout — u přerušeného stahování se navazuje (HTTP Range).</summary>
    private static long RemainingOf(QueueItem item, IReadOnlyList<QueueItem> all)
    {
        if (item.BytesTotal > 0 && item.BytesDone > 0 && item.BytesDone < item.BytesTotal)
        {
            return item.BytesTotal - item.BytesDone;
        }

        return SizeOf(item, all);
    }

    /// <summary>Velikost z katalogu; když chybí, průměr známých velikostí stejného typu.</summary>
    private static long SizeOf(QueueItem item, IReadOnlyList<QueueItem> all)
    {
        if (item.SizeBytes > 0)
        {
            return item.SizeBytes;
        }

        if (item.BytesTotal > 0)
        {
            return item.BytesTotal;
        }

        var known = all.Where(i => i.MediaType == item.MediaType && i.SizeBytes > 0).ToList();
        if (known.Count > 0)
        {
            return (long)known.Average(i => i.SizeBytes);
        }

        return item.MediaType == ScMediaType.Episode ? FallbackEpisodeBytes : FallbackMovieBytes;
    }
}
