using System.Diagnostics;
using System.Net;

namespace Jellyfin.Plugin.StreamCinema.Core;

/// <summary>
/// Vlastní stahování jednoho souboru:
///  - navazování přes HTTP Range (soubor .part),
///  - volitelný limit rychlosti (token-bucket přes sleep),
///  - průběžný callback progresu.
/// Čistý C#, žádná závislost na Jellyfin API.
/// </summary>
public sealed class DownloadEngine
{
    private const int BufferSize = 256 * 1024;

    private readonly HttpClient _http;
    private readonly Action<string> _log;

    public DownloadEngine(HttpClient http, Action<string> log)
    {
        _http = http;
        _log = log;
    }

    /// <summary>
    /// Stáhne URL do partPath (navazuje, pokud .part existuje).
    /// speedLimitBps: bajty/s, 0 = bez limitu.
    /// progress(bytesDone, bytesTotal, speedBps) — volá se cca 1× za sekundu.
    /// Vrací počet bajtů stažených V TÉTO relaci (kvůli dennímu počítadlu).
    /// </summary>
    public async Task<long> DownloadAsync(
        string url,
        string partPath,
        long speedLimitBps,
        Action<long, long, long> progress,
        CancellationToken ct)
    {
        var dir = Path.GetDirectoryName(partPath);
        if (!string.IsNullOrEmpty(dir))
        {
            Directory.CreateDirectory(dir);
        }

        long existing = File.Exists(partPath) ? new FileInfo(partPath).Length : 0;

        using var req = new HttpRequestMessage(HttpMethod.Get, url);
        if (existing > 0)
        {
            req.Headers.Range = new System.Net.Http.Headers.RangeHeaderValue(existing, null);
        }

        using var resp = await _http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);

        if (existing > 0 && resp.StatusCode == HttpStatusCode.OK)
        {
            // Server Range nepodporuje / link je nový — začínáme od nuly
            _log("download: server nevrátil 206, začínám od začátku");
            existing = 0;
        }
        else if (existing > 0 && resp.StatusCode == HttpStatusCode.RequestedRangeNotSatisfiable)
        {
            // .part je delší než soubor na serveru → zahodit a začít znovu
            _log("download: 416, .part neodpovídá — začínám od začátku");
            File.Delete(partPath);
            existing = 0;
            return await DownloadAsync(url, partPath, speedLimitBps, progress, ct).ConfigureAwait(false);
        }

        resp.EnsureSuccessStatusCode();

        var contentLength = resp.Content.Headers.ContentLength ?? 0;
        var total = existing + contentLength;

        var source = await resp.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
        await using var sourceDisposer = source;
        await using var target = new FileStream(
            partPath,
            existing > 0 ? FileMode.Append : FileMode.Create,
            FileAccess.Write,
            FileShare.Read,
            BufferSize);

        // Zrušení (⏹ / Pozastavit / 🗑): ReadAsync(ct) nemusí přerušit už BĚŽÍCÍ čtení
        // ze síťového streamu (ct se kontroluje mezi čteními). Proto při zrušení stream
        // rovnou zavřeme — čekající read okamžitě spadne a přenos se zastaví hned.
        using var ctReg = ct.Register(() =>
        {
            try
            {
                source.Dispose();
            }
            catch (Exception)
            {
                // ignore — cílem je jen odblokovat čekající read
            }
        });

        var buffer = new byte[BufferSize];
        long sessionBytes = 0;
        var overall = Stopwatch.StartNew();
        var reportWatch = Stopwatch.StartNew();
        long lastReportBytes = 0;

        try
        {
            while (true)
            {
                var read = await source.ReadAsync(buffer.AsMemory(0, buffer.Length), ct).ConfigureAwait(false);
                if (read == 0)
                {
                    break;
                }

                await target.WriteAsync(buffer.AsMemory(0, read), ct).ConfigureAwait(false);
                sessionBytes += read;

                // Limit rychlosti: pokud jsme napřed oproti ideálnímu času, počkáme
                if (speedLimitBps > 0)
                {
                    var idealMs = sessionBytes * 1000.0 / speedLimitBps;
                    var aheadMs = idealMs - overall.ElapsedMilliseconds;
                    if (aheadMs > 50)
                    {
                        await Task.Delay((int)Math.Min(aheadMs, 2000), ct).ConfigureAwait(false);
                    }
                }

                if (reportWatch.ElapsedMilliseconds >= 1000)
                {
                    var speed = (long)((sessionBytes - lastReportBytes) * 1000.0 / reportWatch.ElapsedMilliseconds);
                    progress(existing + sessionBytes, total, speed);
                    lastReportBytes = sessionBytes;
                    reportWatch.Restart();
                }
            }
        }
        catch (Exception) when (ct.IsCancellationRequested)
        {
            // zavření streamu kvůli zrušení → normalizovat na OperationCanceledException
            throw new OperationCanceledException(ct);
        }

        progress(existing + sessionBytes, total, 0);
        return sessionBytes;
    }

    /// <summary>Volné místo na filesystému, kde leží `path` (longest-prefix match přes mounty).</summary>
    public static long GetFreeSpace(string path)
    {
        try
        {
            var full = Path.GetFullPath(path);
            DriveInfo? best = null;
            foreach (var d in DriveInfo.GetDrives())
            {
                if (!d.IsReady)
                {
                    continue;
                }

                var root = d.RootDirectory.FullName;
                if (full.StartsWith(root, StringComparison.OrdinalIgnoreCase)
                    && (best == null || root.Length > best.RootDirectory.FullName.Length))
                {
                    best = d;
                }
            }

            return best?.AvailableFreeSpace ?? 0;
        }
        catch
        {
            return 0;
        }
    }
}
