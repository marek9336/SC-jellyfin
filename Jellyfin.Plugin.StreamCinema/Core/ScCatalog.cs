using System.Text.Json;

namespace Jellyfin.Plugin.StreamCinema.Core;

/// <summary>
/// Klient katalogu Stream Cinema — port resources/lib/api/sc.py z Kodi addonu.
/// Čistý C#, žádná závislost na Jellyfin API.
///
/// Hlavičky a parametry jsou identické s addonem (anti-ban):
///  User-Agent, X-Uuid, X-AUTH-TOKEN; query: ver, uid, lang, skin, pro=kraska.
/// </summary>
public sealed class ScCatalog
{
    public const string BaseUrl = "https://stream-cinema.online/kodi";
    public const string ApiVersion = "2.0";
    public const string BackupFileName = "sc.json";

    private readonly HttpClient _http;
    private readonly Action<string> _log;
    private readonly Func<string> _tokenProvider;
    private readonly Func<bool> _kraskaLoggedIn;

    public string Uuid { get; }

    public string Language { get; }

    public string UserAgent { get; }

    public ScCatalog(
        HttpClient http,
        string uuid,
        string language,
        string userAgent,
        Func<string> tokenProvider,
        Func<bool> kraskaLoggedIn,
        Action<string> log)
    {
        _http = http;
        Uuid = uuid;
        Language = string.IsNullOrWhiteSpace(language) ? "cs" : language;
        UserAgent = userAgent;
        _tokenProvider = tokenProvider;
        _kraskaLoggedIn = kraskaLoggedIn;
        _log = log;
    }

    /// <summary>
    /// GET na katalog. `path` je např. "/Search/search-movies" nebo "/Play/12345".
    /// Vrací surový JSON (menu-driven odpověď pro Kodi) — parsuje se defenzivně jinde.
    /// </summary>
    public async Task<JsonDocument> GetAsync(string path, IDictionary<string, string>? extraParams, CancellationToken ct)
    {
        // Cesta může nést vlastní query (resolve URL streamů z `strms`, např. /ws2/...?...).
        // Sloučit jako addon (sc.py prepare(): urlparse + parse_qs + update) — jinak by
        // vzniklo URL se dvěma '?' a backend vrací 503.
        var qIdx = path.IndexOf('?', StringComparison.Ordinal);
        var basePath = qIdx >= 0 ? path[..qIdx] : path;
        var query = new List<KeyValuePair<string, string>>();

        if (qIdx >= 0)
        {
            foreach (var pair in path[(qIdx + 1)..].Split('&', StringSplitOptions.RemoveEmptyEntries))
            {
                var eq = pair.IndexOf('=', StringComparison.Ordinal);
                query.Add(eq >= 0
                    ? new(Uri.UnescapeDataString(pair[..eq]), Uri.UnescapeDataString(pair[(eq + 1)..]))
                    : new(Uri.UnescapeDataString(pair), string.Empty));
            }
        }

        // Defaulty jako addon — nepřepisovat, co už v query je (viz default_params)
        AddIfMissing(query, "ver", ApiVersion);
        AddIfMissing(query, "uid", Uuid);
        AddIfMissing(query, "lang", Language);
        AddIfMissing(query, "skin", "estuary");
        AddIfMissing(query, "HDR", "1");
        AddIfMissing(query, "DV", "1");

        if (_kraskaLoggedIn())
        {
            AddIfMissing(query, "pro", "kraska");
        }

        if (extraParams != null)
        {
            foreach (var kv in extraParams)
            {
                query.RemoveAll(q => q.Key == kv.Key);
                query.Add(new(kv.Key, kv.Value));
            }
        }

        // Seřazení podle klíče — addon posílá parametry seřazené (sorted(query.items()))
        query.Sort((a, b) => string.CompareOrdinal(a.Key, b.Key));

        var qs = string.Join("&", query.Select(kv =>
            $"{Uri.EscapeDataString(kv.Key)}={Uri.EscapeDataString(kv.Value)}"));

        var url = $"{BaseUrl}{basePath}?{qs}";

        using var req = new HttpRequestMessage(HttpMethod.Get, url);
        ApplyHeaders(req);

        _log($"sc: GET {basePath}");
        using var resp = await _http.SendAsync(req, ct).ConfigureAwait(false);

        if (!resp.IsSuccessStatusCode)
        {
            // Diagnostika: SC vrací 5xx i pro odmítnuté requesty — tělo odpovědi
            // je klíč k příčině. UUID v query maskovat, tokeny v query nejsou.
            var errBody = await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
            var snippet = errBody.Length > 300 ? errBody[..300] : errBody;
            var safeQs = string.IsNullOrEmpty(Uuid) ? qs : qs.Replace(Uuid, "***", StringComparison.Ordinal);
            _log($"sc: HTTP {(int)resp.StatusCode} GET {basePath}?{safeQs} — tělo: {snippet}");
        }

        resp.EnsureSuccessStatusCode();
        var text = await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
        return JsonDocument.Parse(text);
    }

    /// <summary>Fulltext hledání. type = "search-movies" | "search-series".</summary>
    public Task<JsonDocument> SearchAsync(string type, string queryText, CancellationToken ct)
    {
        var p = new Dictionary<string, string>
        {
            ["search"] = queryText,
            ["id"] = type,
        };
        return GetAsync($"/Search/{type}", p, ct);
    }

    /// <summary>
    /// Vytáhne pole `strms` z odpovědi /Play/... — defenzivně, chybějící klíče nevadí.
    /// Reálný tvar streamu (ověřeno proti item.py addonu): `url` (resolve URL, NE ident!),
    /// `lang`, `quality`, `size`, `vinfo`, `ainfo`, `bitrate`, `linfo` (pole jazyků),
    /// `subs`, `provider`, `stream_info` {video{codec,width,height}, HDR, DV, Atmos, grp, src}.
    /// </summary>
    public static List<StreamOption> ParseStreams(JsonDocument playResponse)
    {
        var result = new List<StreamOption>();
        if (!playResponse.RootElement.TryGetProperty("strms", out var strms)
            || strms.ValueKind != JsonValueKind.Array)
        {
            return result;
        }

        var index = 0;
        foreach (var s in strms.EnumerateArray())
        {
            var opt = new StreamOption
            {
                Index = index++,
                Url = GetString(s, "url") ?? string.Empty,
                Ident = GetString(s, "ident") ?? string.Empty,
                Provider = GetString(s, "provider"),
                Language = GetString(s, "lang"),
                Quality = GetString(s, "quality"),
                SizeText = GetString(s, "size"),
                VideoInfo = GetString(s, "vinfo"),
                AudioInfo = GetString(s, "ainfo"),
                SubsUrl = GetString(s, "subs"),
                SizeBytes = GetLong(s, "size"),
                Bitrate = GetLong(s, "bitrate"),
            };

            // Číselnou velikost přeformátovat na čitelný text (API posílá bajty)
            if (opt.SizeBytes is > 0)
            {
                opt.SizeText = FormatBytes(opt.SizeBytes.Value);
            }

            if (s.TryGetProperty("linfo", out var linfo) && linfo.ValueKind == JsonValueKind.Array)
            {
                foreach (var l in linfo.EnumerateArray())
                {
                    if (l.ValueKind == JsonValueKind.String && l.GetString() is { Length: > 0 } lang)
                    {
                        opt.Languages.Add(lang);
                    }
                }
            }

            if (s.TryGetProperty("stream_info", out var si) && si.ValueKind == JsonValueKind.Object)
            {
                opt.Hdr = GetBool(si, "HDR");
                opt.Dv = GetBool(si, "DV");
                opt.Atmos = GetBool(si, "Atmos") || GetBool(si, "atmos");
                opt.Group = GetString(si, "grp");
                opt.Source = GetString(si, "src");

                if (si.TryGetProperty("video", out var vid) && vid.ValueKind == JsonValueKind.Object)
                {
                    opt.Codec = GetString(vid, "codec");
                    opt.Width = (int?)GetLong(vid, "width");
                    opt.Height = (int?)GetLong(vid, "height");
                }
            }

            // Stream je použitelný, když má resolve URL (běžný tvar) nebo přímý ident (starší tvar)
            if (!string.IsNullOrEmpty(opt.Url) || !string.IsNullOrEmpty(opt.Ident))
            {
                result.Add(opt);
            }
        }

        return result;
    }

    /// <summary>
    /// Druhý krok resolve: GET na `url` streamu vrátí {"version": N, "vN": "..."}
    /// a kra.sk ident je "vN:hodnota". Port SCPlayItem._get_resolve_data z item.py.
    /// </summary>
    public async Task<string> ResolveStreamIdentAsync(string streamUrl, CancellationToken ct)
    {
        using var doc = await GetAsync(streamUrl, null, ct).ConfigureAwait(false);
        var root = doc.RootElement;

        var version = GetString(root, "version");
        if (string.IsNullOrEmpty(version))
        {
            throw new KraskaException("Katalog nevrátil verzi streamu — stream je možná nedostupný.");
        }

        var key = "v" + version;
        var value = GetString(root, key);
        if (string.IsNullOrEmpty(value))
        {
            throw new KraskaException($"Katalog nevrátil klíč {key} — stream je možná nedostupný.");
        }

        var ident = $"{key}:{value}";
        _log($"sc: stream resolvován na ident {key}:***");
        return ident;
    }

    /// <summary>Z hodnoty `subs` (URL) vytáhne kra.sk ident — část za "/file/". Port z gui/item.py.</summary>
    public static string? SubsIdentFromUrl(string? subsUrl)
    {
        if (string.IsNullOrEmpty(subsUrl))
        {
            return null;
        }

        var parts = subsUrl.Split("/file/", 2);
        return parts.Length > 1 && parts[1].Length > 0 ? parts[1] : null;
    }

    /// <summary>
    /// Bootstrap X-AUTH-TOKENu ze zálohy sc.json na kra.sk úložišti.
    /// KRITICKÉ: token se schvaluje ručně na serveru SC — NIKDY negenerujeme nový.
    /// Vrací token, nebo null (a uživateli se má zobrazit návod na ruční zadání).
    /// </summary>
    public async Task<string?> BootstrapTokenAsync(KraskaClient kraska, CancellationToken ct)
    {
        try
        {
            var files = await kraska.ListFilesAsync(BackupFileName, ct).ConfigureAwait(false);

            // Exact match na jméno — server filter může vrátit i jiné soubory (viz sc.py)
            var match = files.FirstOrDefault(f => f.Name == BackupFileName);
            if (match.Ident == null)
            {
                _log("sc: sc.json na úložišti nenalezen");
                return null;
            }

            var url = await kraska.ResolveAsync(match.Ident, ct).ConfigureAwait(false);
            var text = (await _http.GetStringAsync(url, ct).ConfigureAwait(false)).Trim();

            if (text.Length == 32)
            {
                _log("sc: token načten ze zálohy sc.json");
                return text;
            }

            _log($"sc: sc.json má neplatný obsah (délka {text.Length}) — token NEgeneruji");
            return null;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            _log($"sc: bootstrap tokenu selhal: {ex.Message}");
            return null;
        }
    }

    private void ApplyHeaders(HttpRequestMessage req)
    {
        req.Headers.TryAddWithoutValidation("User-Agent", UserAgent);
        req.Headers.TryAddWithoutValidation("X-Uuid", Uuid);

        var token = _tokenProvider();
        if (!string.IsNullOrEmpty(token))
        {
            req.Headers.TryAddWithoutValidation("X-AUTH-TOKEN", token);
        }
    }

    private static void AddIfMissing(List<KeyValuePair<string, string>> query, string key, string value)
    {
        if (!query.Any(q => q.Key == key))
        {
            query.Add(new(key, value));
        }
    }

    private static string? GetString(JsonElement el, string prop)
    {
        if (el.ValueKind == JsonValueKind.Object && el.TryGetProperty(prop, out var v))
        {
            return v.ValueKind switch
            {
                JsonValueKind.String => v.GetString(),
                JsonValueKind.Number => v.ToString(),
                _ => null,
            };
        }

        return null;
    }

    private static long? GetLong(JsonElement el, string prop)
    {
        if (el.ValueKind != JsonValueKind.Object || !el.TryGetProperty(prop, out var v))
        {
            return null;
        }

        return v.ValueKind switch
        {
            JsonValueKind.Number when v.TryGetInt64(out var n) => n,
            JsonValueKind.String when long.TryParse(v.GetString(), out var n) => n,
            _ => null,
        };
    }

    private static bool GetBool(JsonElement el, string prop)
    {
        if (el.ValueKind != JsonValueKind.Object || !el.TryGetProperty(prop, out var v))
        {
            return false;
        }

        return v.ValueKind switch
        {
            JsonValueKind.True => true,
            JsonValueKind.Number => v.TryGetInt64(out var n) && n != 0,
            JsonValueKind.String => v.GetString() is "1" or "true" or "True",
            _ => false,
        };
    }

    private static string FormatBytes(long bytes)
    {
        string[] units = { "B", "KB", "MB", "GB", "TB" };
        double val = bytes;
        var i = 0;
        while (val >= 1024 && i < units.Length - 1)
        {
            val /= 1024;
            i++;
        }

        return $"{val:0.##} {units[i]}";
    }
}
