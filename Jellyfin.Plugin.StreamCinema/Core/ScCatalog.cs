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
        var query = new List<KeyValuePair<string, string>>
        {
            new("ver", ApiVersion),
            new("uid", Uuid),
            new("lang", Language),
            new("skin", "estuary"),
        };

        if (_kraskaLoggedIn())
        {
            query.Add(new("pro", "kraska"));
        }

        if (extraParams != null)
        {
            foreach (var kv in extraParams)
            {
                query.Add(new(kv.Key, kv.Value));
            }
        }

        // Seřazení podle klíče — addon posílá parametry seřazené (sorted(query.items()))
        query.Sort((a, b) => string.CompareOrdinal(a.Key, b.Key));

        var qs = string.Join("&", query.Select(kv =>
            $"{Uri.EscapeDataString(kv.Key)}={Uri.EscapeDataString(kv.Value)}"));

        var url = $"{BaseUrl}{path}?{qs}";

        using var req = new HttpRequestMessage(HttpMethod.Get, url);
        ApplyHeaders(req);

        _log($"sc: GET {path}");
        using var resp = await _http.SendAsync(req, ct).ConfigureAwait(false);
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
                Ident = GetString(s, "ident") ?? string.Empty,
                Provider = GetString(s, "provider"),
                Language = GetString(s, "lang"),
                Quality = GetString(s, "quality"),
                SizeText = GetString(s, "size"),
                VideoInfo = GetString(s, "vinfo"),
                AudioInfo = GetString(s, "ainfo"),
                SubsUrl = GetString(s, "subs"),
            };

            if (!string.IsNullOrEmpty(opt.Ident))
            {
                result.Add(opt);
            }
        }

        return result;
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
}
