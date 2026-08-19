using System.Text;
using System.Text.Json;

namespace Jellyfin.Plugin.StreamCinema.Core;

/// <summary>
/// Klient pro api.kra.sk — přímý port logiky z Kodi addonu (resources/lib/api/kraska.py).
/// Čistý C#, žádná závislost na Jellyfin API.
///
/// Chování shodné s addonem:
///  - tělo requestu: {"data": {...}, "session_id": "..."} (session_id na top-level),
///  - při chybě resolve/user-info se session invaliduje a proběhne PRÁVĚ JEDEN retry s novým loginem,
///  - heslo ani session se nikdy nelogují.
/// </summary>
public sealed class KraskaClient
{
    private const string Base = "https://api.kra.sk";

    private readonly HttpClient _http;
    private readonly Action<string> _log;
    private readonly SemaphoreSlim _loginLock = new(1, 1);

    private string? _session;
    private KraskaUserInfo? _userInfoCache;

    public string Username { get; }

    private readonly string _password;

    public KraskaClient(HttpClient http, string username, string password, Action<string> log)
    {
        _http = http;
        Username = username;
        _password = password;
        _log = log;
    }

    public bool HasCredentials => !string.IsNullOrWhiteSpace(Username) && !string.IsNullOrWhiteSpace(_password);

    public bool IsLoggedIn => _session != null;

    /// <summary>Důvod poslední neúspěšné operace (login/resolve) — pro GUI a log. Heslo nikdy neobsahuje.</summary>
    public string? LastError { get; private set; }

    /// <summary>Přihlášení — získá session_id. Vrací true při úspěchu.</summary>
    public async Task<bool> LoginAsync(CancellationToken ct)
    {
        await _loginLock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (_session != null)
            {
                return true;
            }

            LastError = null;

            if (!HasCredentials)
            {
                LastError = "Nejsou vyplněné přihlašovací údaje — ulož nejdřív nastavení.";
                _log("kra: login FAILED — prázdné údaje");
                _session = null;
                return false;
            }

            _log("kra: login start");
            var data = await PostRawAsync("/api/user/login",
                new { data = new { username = Username, password = _password } }, ct).ConfigureAwait(false);

            if (data != null
                && data.Value.TryGetProperty("session_id", out var sid)
                && sid.ValueKind == JsonValueKind.String)
            {
                _session = sid.GetString();
                _userInfoCache = null;
                LastError = null;
                _log("kra: login OK");
                return _session != null;
            }

            LastError = DescribeError(data, "Přihlášení se nezdařilo — zkontroluj jméno a heslo.");
            _log($"kra: login FAILED — {LastError}");
            _session = null;
            return false;
        }
        finally
        {
            _loginLock.Release();
        }
    }

    public void InvalidateSession()
    {
        _session = null;
        _userInfoCache = null;
    }

    /// <summary>
    /// Resolvne ident na přímou URL souboru (POST /api/file/download → data.link).
    /// Jeden automatický retry s re-loginem, stejně jako addon.
    /// </summary>
    public async Task<string> ResolveAsync(string ident, CancellationToken ct)
    {
        for (var attempt = 0; attempt < 2; attempt++)
        {
            if (_session == null && !await LoginAsync(ct).ConfigureAwait(false))
            {
                throw new KraskaException("Přihlášení ke kra.sk selhalo — zkontroluj jméno a heslo.");
            }

            var data = await PostRawAsync("/api/file/download", new { data = new { ident } }, ct).ConfigureAwait(false);

            if (data != null
                && data.Value.TryGetProperty("data", out var fileData)
                && fileData.TryGetProperty("link", out var link))
            {
                var url = link.GetString();
                if (!string.IsNullOrEmpty(url))
                {
                    return url;
                }
            }

            _log($"kra: resolve selhal (pokus {attempt + 1}), invaliduji session");
            InvalidateSession();
        }

        throw new KraskaException("Soubor se nepodařilo resolvovat — chybný soubor nebo expirovaná session.");
    }

    /// <summary>Info o účtu (days_left, subscribed_until). Cachované do invalidace session.</summary>
    public async Task<KraskaUserInfo?> UserInfoAsync(CancellationToken ct)
    {
        if (_userInfoCache != null)
        {
            return _userInfoCache;
        }

        for (var attempt = 0; attempt < 2; attempt++)
        {
            if (_session == null && !await LoginAsync(ct).ConfigureAwait(false))
            {
                return null;
            }

            var data = await PostRawAsync("/api/user/info", null, ct).ConfigureAwait(false);
            if (data != null && data.Value.TryGetProperty("data", out var user))
            {
                var info = new KraskaUserInfo
                {
                    DaysLeft = user.TryGetProperty("days_left", out var dl) && dl.TryGetInt32(out var d) ? d : 0,
                    SubscribedUntil = user.TryGetProperty("subscribed_until", out var su) ? su.ToString() : null
                };
                _userInfoCache = info;
                return info;
            }

            InvalidateSession();
        }

        return null;
    }

    /// <summary>Výpis souborů na úložišti (pro nalezení sc.json).</summary>
    public async Task<List<(string Name, string Ident)>> ListFilesAsync(string? filter, CancellationToken ct)
    {
        var result = new List<(string, string)>();

        if (_session == null && !await LoginAsync(ct).ConfigureAwait(false))
        {
            return result;
        }

        var data = await PostRawAsync("/api/file/list",
            new { data = new { parent = (string?)null, filter } }, ct).ConfigureAwait(false);

        if (data != null && data.Value.TryGetProperty("data", out var files) && files.ValueKind == JsonValueKind.Array)
        {
            foreach (var f in files.EnumerateArray())
            {
                var name = f.TryGetProperty("name", out var n) ? n.GetString() : null;
                var ident = f.TryGetProperty("ident", out var i) ? i.GetString() : null;
                if (name != null && ident != null)
                {
                    result.Add((name, ident));
                }
            }
        }

        return result;
    }

    /// <summary>Vytáhne z odpovědi kra.sk čitelný důvod (msg + error kód). Nikdy neobsahuje heslo.</summary>
    private static string DescribeError(JsonElement? data, string fallback)
    {
        if (data == null)
        {
            return "Žádná odpověď z api.kra.sk (síť nebo neplatná odpověď serveru).";
        }

        var msg = data.Value.TryGetProperty("msg", out var m) && m.ValueKind == JsonValueKind.String
            ? m.GetString()
            : null;
        var code = data.Value.TryGetProperty("error", out var e) && e.TryGetInt32(out var ec)
            ? ec
            : (int?)null;

        if (string.IsNullOrEmpty(msg))
        {
            return fallback;
        }

        return code != null ? $"kra.sk: {msg} (kód {code})" : $"kra.sk: {msg}";
    }

    private async Task<JsonElement?> PostRawAsync(string endpoint, object? payload, CancellationToken ct)
    {
        try
        {
            // Tělo: serializovat payload a případně přidat session_id na top-level
            // (addon: data.update({'session_id': self.token})).
            JsonElement bodyElement;
            if (payload == null)
            {
                bodyElement = JsonSerializer.SerializeToElement(new Dictionary<string, object>());
            }
            else
            {
                bodyElement = JsonSerializer.SerializeToElement(payload);
            }

            var dict = new Dictionary<string, object?>();
            foreach (var prop in bodyElement.EnumerateObject())
            {
                dict[prop.Name] = prop.Value;
            }

            if (_session != null)
            {
                dict["session_id"] = _session;
            }

            var json = JsonSerializer.Serialize(dict);
            using var content = new StringContent(json, Encoding.UTF8, "application/json");
            using var resp = await _http.PostAsync(Base + endpoint, content, ct).ConfigureAwait(false);
            var text = await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);

            using var doc = JsonDocument.Parse(text);
            return doc.RootElement.Clone();
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            _log($"kra: chyba requestu {endpoint}: {ex.Message}");
            return null;
        }
    }
}

/// <summary>Chyba komunikace s kra.sk.</summary>
public class KraskaException : Exception
{
    public KraskaException(string message)
        : base(message)
    {
    }
}
