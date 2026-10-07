using System.Globalization;
using System.Text;

namespace Jellyfin.Plugin.StreamCinema.Core;

/// <summary>
/// Časová okna stahování. Buď globální (od–do + volitelně další okna), nebo rozvrh
/// po dnech v týdnu. Den může mít víc oken — třeba přes den do 16:00 a pak znovu
/// v noci od 22:00 do 3:00.
///
/// Formát rozvrhu (jeden řetězec v konfiguraci): "1:8-16,22-3;2:0-0;6:off"
/// <list type="bullet">
/// <item>klíč = den podle ISO (1 = pondělí … 7 = neděle),</item>
/// <item>okna dne oddělená čárkou ("8-16,22-3"),</item>
/// <item>"off" = ten den nestahovat, "0-0" (od == do) = celý den,</item>
/// <item>okno smí přecházet přes půlnoc ("22-3" = od 22:00 do 3:00 druhého dne).</item>
/// </list>
/// Nevyplněný den = celý den. Globální „další okna" mají stejný zápis bez dne: "22-3,12-13".
/// </summary>
public static class Schedule
{
    /// <summary>České názvy dnů pro hlášky (index 1–7 = pondělí–neděle).</summary>
    public static readonly string[] DayNames =
        ["", "pondělí", "úterý", "středa", "čtvrtek", "pátek", "sobota", "neděle"];

    /// <summary>Jedno okno v rámci dne (hodiny 0–23). From == To = celý den.</summary>
    public sealed class Range
    {
        public int From { get; set; }

        public int To { get; set; }

        public bool AllDay => From == To;
    }

    /// <summary>Okna pro jeden den v týdnu.</summary>
    public sealed class DayWindow
    {
        /// <summary>Stahovat tento den vůbec?</summary>
        public bool Enabled { get; set; } = true;

        /// <summary>Okna dne. Prázdné = celý den.</summary>
        public List<Range> Ranges { get; set; } = new();

        /// <summary>Celý den (bez omezení hodinami).</summary>
        public bool AllDay => Ranges.Count == 0 || Ranges.Any(r => r.AllDay);
    }

    /// <summary>ISO číslo dne (1 = pondělí … 7 = neděle).</summary>
    public static int IsoDay(DateTime dt) =>
        dt.DayOfWeek == DayOfWeek.Sunday ? 7 : (int)dt.DayOfWeek;

    /// <summary>Rozparsuje seznam oken "8-16,22-3". Vadné kusy přeskočí.</summary>
    public static List<Range> ParseRanges(string? spec)
    {
        var result = new List<Range>();
        if (string.IsNullOrWhiteSpace(spec))
        {
            return result;
        }

        foreach (var part in spec.Split(',', StringSplitOptions.RemoveEmptyEntries))
        {
            var hours = part.Trim().Split('-', 2);
            if (hours.Length == 2
                && int.TryParse(hours[0].Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var from)
                && int.TryParse(hours[1].Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var to))
            {
                result.Add(new Range { From = Math.Clamp(from, 0, 23), To = Math.Clamp(to, 0, 23) });
            }
        }

        return result;
    }

    /// <summary>
    /// Rozparsuje rozvrh. Vždy vrátí všech 7 dní — chybějící/vadné jsou „celý den".
    /// </summary>
    public static Dictionary<int, DayWindow> Parse(string? spec)
    {
        var result = new Dictionary<int, DayWindow>();
        for (var d = 1; d <= 7; d++)
        {
            result[d] = new DayWindow();
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

            var ranges = ParseRanges(value);
            if (ranges.Count > 0)
            {
                result[day] = new DayWindow { Enabled = true, Ranges = ranges };
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
            if (!w.Enabled)
            {
                sb.Append("off");
            }
            else if (w.Ranges.Count == 0)
            {
                sb.Append("0-0");
            }
            else
            {
                sb.Append(string.Join(",", w.Ranges.Select(r =>
                    string.Create(CultureInfo.InvariantCulture, $"{r.From}-{r.To}"))));
            }
        }

        return sb.ToString();
    }

    /// <summary>
    /// Smí se právě teď stahovat? `endJitterMinutes` zkracuje konec každého okna o pár
    /// minut, aby stahování nekončilo přesně na hodinu. U „celého dne" se neuplatní.
    /// </summary>
    public static bool IsOpen(
        bool useWeekly, string? weeklySpec, int globalFrom, int globalTo, int endJitterMinutes, DateTime now,
        string? globalExtra = null) =>
        Containing(useWeekly, weeklySpec, globalFrom, globalTo, _ => endJitterMinutes, now, globalExtra) != null;

    /// <summary>
    /// Totéž, ale zkrácení konce se určuje pro každé okno zvlášť podle jeho začátku
    /// (worker si pro každé okno losuje vlastní, ať dvě okna nekončí se stejným posunem).
    /// </summary>
    public static bool IsOpen(
        bool useWeekly, string? weeklySpec, int globalFrom, int globalTo, Func<DateTime, int> endJitterFor,
        DateTime now, string? globalExtra = null) =>
        Containing(useWeekly, weeklySpec, globalFrom, globalTo, endJitterFor, now, globalExtra) != null;

    /// <summary>
    /// Začátek okna, ve kterém právě jsme (null = mimo okno). Slouží jako klíč pro
    /// rozptyl startu: ten se losuje u každého otevření okna, ne jen jednou za den —
    /// jinak by druhé (třeba noční) okno začínalo přesně na hodinu.
    /// </summary>
    public static DateTime? OpenRangeStart(
        bool useWeekly, string? weeklySpec, int globalFrom, int globalTo, int endJitterMinutes, DateTime now,
        string? globalExtra = null) =>
        Containing(useWeekly, weeklySpec, globalFrom, globalTo, _ => endJitterMinutes, now, globalExtra)?.Start;

    /// <summary>Totéž se zkrácením konce určovaným pro každé okno zvlášť.</summary>
    public static DateTime? OpenRangeStart(
        bool useWeekly, string? weeklySpec, int globalFrom, int globalTo, Func<DateTime, int> endJitterFor,
        DateTime now, string? globalExtra = null) =>
        Containing(useWeekly, weeklySpec, globalFrom, globalTo, endJitterFor, now, globalExtra)?.Start;

    /// <summary>Popis dnešních oken do stavové hlášky, např. „čtvrtek 8:00–16:00 + 22:00–3:00".</summary>
    public static string Describe(
        bool useWeekly, string? weeklySpec, int globalFrom, int globalTo, DateTime now, string? globalExtra = null)
    {
        var w = WindowFor(now.Date, useWeekly, weeklySpec, globalFrom, globalTo, globalExtra);
        var name = DayNames[IsoDay(now)];
        if (w == null || !w.Enabled)
        {
            return $"{name}: nestahovat";
        }

        if (w.AllDay)
        {
            return $"{name}: celý den";
        }

        return name + " " + string.Join(" + ", w.Ranges.Select(r => $"{r.From}:00–{r.To}:00"));
    }

    /// <summary>
    /// Nejbližší budoucí začátek okna (pro hlášku „další okno v …").
    /// Vrací null, když je v příštím týdnu vypnuto všechno.
    /// </summary>
    public static DateTime? NextOpen(
        bool useWeekly, string? weeklySpec, int globalFrom, int globalTo, DateTime now, string? globalExtra = null)
    {
        // Okna dne d začínají během dne d, takže první den s kandidátem má i minimum.
        for (var ahead = 0; ahead <= 7; ahead++)
        {
            DateTime? best = null;
            foreach (var (start, _, _) in WindowsOf(
                         now.Date.AddDays(ahead), useWeekly, weeklySpec, globalFrom, globalTo, globalExtra))
            {
                if (start > now && (best == null || start < best))
                {
                    best = start;
                }
            }

            if (best != null)
            {
                return best;
            }
        }

        return null;
    }

    /// <summary>Okno, které obsahuje `now` (se zkrácením konce), nebo null.</summary>
    private static (DateTime Start, DateTime End)? Containing(
        bool useWeekly, string? weeklySpec, int globalFrom, int globalTo, Func<DateTime, int> endJitterFor,
        DateTime now, string? globalExtra)
    {
        (DateTime Start, DateTime End)? found = null;

        // Okno může přecházet přes půlnoc → kontroluj i včerejšek.
        for (var back = 0; back <= 1; back++)
        {
            var day = now.Date.AddDays(-back);
            foreach (var (start, rawEnd, allDay) in WindowsOf(day, useWeekly, weeklySpec, globalFrom, globalTo, globalExtra))
            {
                var end = rawEnd;
                if (!allDay)
                {
                    var jitter = endJitterFor(start);
                    if (jitter > 0)
                    {
                        end = end.AddMinutes(-jitter);
                        if (end <= start)
                        {
                            continue; // zkrácení okno celé spolklo
                        }
                    }
                }

                // Při překryvu oken drž nejdřívější začátek — ať je klíč rozptylu stabilní.
                if (now >= start && now < end && (found == null || start < found.Value.Start))
                {
                    found = (start, end);
                }
            }
        }

        return found;
    }

    /// <summary>Okna jednoho dne jako konkrétní časy.</summary>
    private static IEnumerable<(DateTime Start, DateTime End, bool AllDay)> WindowsOf(
        DateTime day, bool useWeekly, string? weeklySpec, int globalFrom, int globalTo, string? globalExtra)
    {
        var w = WindowFor(day, useWeekly, weeklySpec, globalFrom, globalTo, globalExtra);
        if (w == null || !w.Enabled)
        {
            yield break;
        }

        if (w.Ranges.Count == 0)
        {
            yield return (day, day.AddDays(1), true);
            yield break;
        }

        foreach (var r in w.Ranges)
        {
            if (r.AllDay)
            {
                yield return (day, day.AddDays(1), true);
                continue;
            }

            var start = day.AddHours(r.From);
            var end = r.From < r.To ? day.AddHours(r.To) : day.AddDays(1).AddHours(r.To);
            yield return (start, end, false);
        }
    }

    private static DayWindow? WindowFor(
        DateTime day, bool useWeekly, string? weeklySpec, int globalFrom, int globalTo, string? globalExtra)
    {
        if (!useWeekly)
        {
            // Globálně: první okno od–do (od == do = celý den) + případná další okna.
            var ranges = new List<Range> { new() { From = globalFrom, To = globalTo } };
            ranges.AddRange(ParseRanges(globalExtra));
            return new DayWindow { Enabled = true, Ranges = ranges };
        }

        return Parse(weeklySpec).TryGetValue(IsoDay(day), out var w) ? w : null;
    }
}
