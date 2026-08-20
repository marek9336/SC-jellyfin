using System.Text.Json;
using System.Text.RegularExpressions;

namespace Jellyfin.Plugin.StreamCinema.Core;

/// <summary>
/// Klient sidecar SC helperu (`service.sc.helper` běžící vedle Jellyfinu).
/// Helper resolvne RSA-podepsaný `v1:` ident na stažitelný odkaz — tuhle logiku
/// dělá jejich vlastní podepsaný kód, my ji jen voláme přes HTTP (jako Kodi addon).
///
/// Kontrakt (z addonu, kraska.resolve_via_proxy):
///   GET {base}/play?ident=v1:&lt;blob&gt;&amp;token=&lt;kraska_session_id&gt; → {"url": "..."}
/// Vrácená URL bývá lokální proxy helperu `http://127.0.0.1:&lt;port&gt;/stream/&lt;sid&gt;`;
/// přepíšeme host na adresu sidecaru (cross-container) a stahujeme přes forwarder.
///
/// Čistý C#, žádná závislost na Jellyfin API.
/// </summary>
public sealed class HelperClient
{
    private static readonly Regex LocalHost = new(@"http://127\.0\.0\.1:\d+", RegexOptions.Compiled);

    private readonly HttpClient _http;
    private readonly Action<string> _log;

    public HelperClient(HttpClient http, Action<string> log)
    {
        _http = http;
        _log = log;
    }

    /// <summary>
    /// Resolvne `v1:` ident přes helper. Vrací přímý stažitelný odkaz.
    /// `baseUrl` = adresa sidecaru (např. http://sc-helper:65007).
    /// </summary>
    public async Task<string> ResolveAsync(string baseUrl, string ident, string sessionId, CancellationToken ct)
    {
        var root = baseUrl.TrimEnd('/');
        var url = $"{root}/play?ident={Uri.EscapeDataString(ident)}&token={Uri.EscapeDataString(sessionId)}";

        _log("helper: /play resolve");
        using var resp = await _http.GetAsync(url, ct).ConfigureAwait(false);
        resp.EnsureSuccessStatusCode();

        var text = await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
        using var doc = JsonDocument.Parse(text);
        var el = doc.RootElement;

        if (el.TryGetProperty("error", out var err))
        {
            throw new KraskaException($"SC helper resolve chyba: {err.GetRawText()}");
        }

        if (!el.TryGetProperty("url", out var u) || u.ValueKind != JsonValueKind.String)
        {
            throw new KraskaException("SC helper nevrátil pole `url`.");
        }

        var streamUrl = u.GetString()!;

        // Helper vrací self-proxy s 127.0.0.1 — přepsat na adresu sidecaru,
        // ať to funguje z jiného kontejneru (Jellyfin).
        streamUrl = LocalHost.Replace(streamUrl, root);
        _log("helper: resolvováno na stream URL");
        return streamUrl;
    }
}
