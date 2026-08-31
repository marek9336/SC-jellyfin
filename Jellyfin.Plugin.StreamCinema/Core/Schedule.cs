using System.Globalization;
using System.Text;

namespace Jellyfin.Plugin.StreamCinema.Core;

/// <summary>
/// Časové okno stahování. Buď jedno globální (od–do hodina), nebo rozvrh po dnech
/// v týdnu — v pondělí se může stahovat celý den, o víkendu třeba vůbec.
///
/// Formát rozvrhu (jeden řetězec v konfiguraci): "1:0-0;2:22-6;6:off"
/// <list type="bullet">
/// <item>klíč = den podle ISO (1 = pondělí … 7 = neděle),</item>
/// <item>"off" = ten den nestahovat,</item>
/// <item>"0-0" (od == do) = celý den,</item>
/// <item>okno smí přecházet přes půlnoc ("22-6" = od 22:00 do 6:00 druhého dne).</item>
/// </list>
/// Nevyplněný den = celý den (stejné chování jako globální okno 0-0).
/// </summary>
public static class Schedule
{
    /// <summary>České zkratky dnů pro hlášky a GUI (index 1–7 = pondělí–neděle).</summary>
    public static readonly string[] DayNames =
        ["", "pondělí", "úterý", "středa", "čtvrtek", "pátek", "sobota", "neděle"];

    /// <summary>Okno pro jeden den v týdnu.</summary>
    public sealed class DayWindow
    {
        /// <summary>Stahovat tento den vůbec?</summary>
        public bool Enabled { get; set; } = true;

        /// <summary>Hodina začátku (0–23).</summary>
        public int From { get; set; }

        /// <summary>Hodina konce (0–23). From == To znamená celý den.</summary>
        public int To { get; set; }

        /// <summary>Celý den (bez omezení hodinami).</summary>
        public bool AllDay => From == To;
    }

    /// <summary>ISO číslo dne (1 = pondělí … 7 = neděle).</summary>
    public static int IsoDay(DateTime dt) =>
        dt.DayOfWeek == DayOfWeek.Sunday ? 7 : (int)dt.DayOfWeek;

    /// <summary>
    /// Rozparsuje rozvrh. Vždy vrátí všech 7 dní — chybějící/vadné jsou „celý den".
    /// </summary>
    public static Dictionary<int, DayWindow> Parse(string? spec)
    {
        var result = new Dictionary<int, DayWindow>();
        for (var d = 1; d <= 7; d++)
        {
            result[d] = new DayWindow { Enabled = true, From = 0, To = 0 };
        }

        if (string.IsNullOrWhiteSpace(spec))
        {
            return result;
        }

        foreach (var part in spec.Split(';', StringSplitOptions.RemoveEmptyEntries))
        {
            var kv = part.Split(':', 2);
            if (kv.Length != 2
                || !int.TryParse(kv[0].Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var day)
                || day < 1 || day > 7)
            {
                continue;
            }

            var value = kv[1].Trim();
            if (value.Equals("off", StringComparison.OrdinalIgnoreCase))
            {
                result[day] = new DayWindow { Enabled = false };
                continue;
            }

            var hours = value.Split('-', 2);
            if (hours.Length == 2
                && int.TryParse(hours[0].Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var from)
                && int.TryParse(hours[1].Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var to))
            {
                result[day] = new DayWindow
                {
                    Enabled = true,
                    From = Math.Clamp(from, 0, 23),
                    To = Math.Clamp(to, 0, 23),
                };
            }
        }

        return result;
    }

    /// <summary>Složí rozvrh zpět do řetězce (pořadí pondělí → neděle).</summary>
    public static string Format(IReadOnlyDictionary<int, DayWindow> days)
    {
        var sb = new StringBuilder();
        for (var d = 1; d <= 7; d++)
        {
            if (!days.TryGetValue(d, out var w))
            {
                continue;
            }

            if (sb.Length > 0)
            {
                sb.Append(';');
            }

            sb.Append(d).Append(':');
            sb.Append(w.Enabled
                ? string.Create(CultureInfo.InvariantCulture, $"{w.From}-{w.To}")
                : "off");
        }

        return sb.ToString();
    }

    /// <summary>
    /// Smí se právě teď stahovat? `endJitterMinutes` zkracuje konec okna o náhodných
    /// pár minut (worker si je losuje jednou denně), aby stahování nekončilo přesně
    /// na hodinu. U okna „celý den" se jitter neuplatní — takové okno konec nemá.
    /// </summary>
    public static bool IsOpen(
        bool useWeekly, string? weeklySpec, int globalFrom, int globalTo, int endJitterMinutes, DateTime now)
    {
        // Okno může přecházet přes půlnoc → kontroluj i včerejšek.
        for (var back = 0; back <= 1; back++)
        {
            var day = now.Date.AddDays(-back);
            var w = WindowFor(day, useWeekly, weeklySpec, globalFrom, globalTo);
            if (w == null || !w.Enabled)
            {
                continue;
            }

            var (start, end) = Bounds(day, w);
            if (!w.AllDay && endJitterMinutes > 0)
            {
                end = end.AddMinutes(-endJitterMinutes);
                if (end <= start)
                {
                    continue; // jitter okno celé spolkl
                }
            }

            if (now >= start && now < end)
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>Popis dnešního okna do stavové hlášky, např. „čtvrtek 22–6".</summary>
    public static string Describe(bool useWeekly, string? weeklySpec, int globalFrom, int globalTo, DateTime now)
    {
        var w = WindowFor(now.Date, useWeekly, weeklySpec, globalFrom, globalTo);
        var name = DayNames[IsoDay(now)];
        if (w == null || !w.Enabled)
        {
            return $"{name}: nestahovat";
        }

        return w.AllDay ? $"{name}: celý den" : $"{name} {w.From}:00–{w.To}:00";
    }

    /// <summary>
    /// Nejbližší budoucí začátek okna (pro hlášku „další okno v …").
    /// Vrací null, když je v příštím týdnu vypnuto všechno.
    /// </summary>
    public static DateTime? NextOpen(
        bool useWeekly, string? weeklySpec, int globalFrom, int globalTo, DateTime now)
    {
        for (var ahead = 0; ahead <= 7; ahead++)
        {
            var day = now.Date.AddDays(ahead);
            var w = WindowFor(day, useWeekly, weeklySpec, globalFrom, globalTo);
            if (w == null || !w.Enabled)
            {
                continue;
            }

            var (start, _) = Bounds(day, w);
            if (start > now)
            {
                return start;
            }
        }

        return null;
    }

    private static DayWindow? WindowFor(
        DateTime day, bool useWeekly, string? weeklySpec, int globalFrom, int globalTo)
    {
        if (!useWeekly)
        {
            return new DayWindow { Enabled = true, From = globalFrom, To = globalTo };
        }

        return Parse(weeklySpec).TryGetValue(IsoDay(day), out var w) ? w : null;
    }

    private static (DateTime Start, DateTime End) Bounds(DateTime day, DayWindow w)
    {
        if (w.AllDay)
        {
            return (day, day.AddDays(1));
        }

        var start = day.AddHours(w.From);
        var end = w.From < w.To ? day.AddHours(w.To) : day.AddDays(1).AddHours(w.To);
        return (start, end);
    }
}
