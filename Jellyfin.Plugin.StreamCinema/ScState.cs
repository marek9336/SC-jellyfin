using Jellyfin.Plugin.StreamCinema.Configuration;
using Jellyfin.Plugin.StreamCinema.Core;
using MediaBrowser.Common.Configuration;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.StreamCinema;

/// <summary>
/// Most mezi Jellyfinem a Core/ vrstvou. Singleton v DI.
/// Staví KraskaClient/ScCatalog z aktuální konfigurace a přestaví je,
/// když se přihlašovací údaje změní. Drží frontu a stav workeru.
/// </summary>
public sealed class ScState : IDisposable
{
    private readonly ILogger<ScState> _logger;
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly object _lock = new();

    private KraskaClient? _kraska;
    private ScCatalog? _catalog;
    private string _snapshot = string.Empty;

    public DownloadQueue Queue { get; }

    /// <summary>Seznam sledovaných položek (Hlídač).</summary>
    public WatchStore Watch { get; }

    public DownloadEngine Engine { get; }

    /// <summary>Klient sidecar SC helperu (resolve v1: identů). Stateless — bezpečné sdílet.</summary>
    public HelperClient Helper { get; }

    /// <summary>Runtime stav workeru pro /status endpoint (aktualizuje worker).</summary>
    public WorkerStatus Status { get; } = new();

    // ── Zrušení běžícího stahování (⏹ Zastavit / Pozastavit) ──────
    private readonly object _dlLock = new();
    private CancellationTokenSource? _dlCts;

    /// <summary>Worker: založí zrušitelný scope pro jedno stahování. Vrací token pro resolve+download.</summary>
    public CancellationToken BeginDownload(CancellationToken outer)
    {
        lock (_dlLock)
        {
            _dlCts?.Dispose();
            _dlCts = CancellationTokenSource.CreateLinkedTokenSource(outer);
            return _dlCts.Token;
        }
    }

    /// <summary>Worker: ukončí scope stahování.</summary>
    public void EndDownload()
    {
        lock (_dlLock)
        {
            _dlCts?.Dispose();
            _dlCts = null;
        }
    }

    /// <summary>
    /// Zruší běžící stahování. Když je zadané id, ruší jen pokud se právě stahuje tato položka.
    /// Vrací true, když bylo co zrušit.
    /// </summary>
    public bool CancelDownload(Guid? id = null)
    {
        lock (_dlLock)
        {
            if (_dlCts == null || _dlCts.IsCancellationRequested)
            {
                return false;
            }

            if (id != null && Status.CurrentItemId != id)
            {
                return false;
            }

            _dlCts.Cancel();
            return true;
        }
    }

    public ScState(IApplicationPaths applicationPaths, IHttpClientFactory httpClientFactory, ILogger<ScState> logger)
    {
        _logger = logger;
        _httpClientFactory = httpClientFactory;

        var dataDir = Path.Combine(applicationPaths.DataPath, "streamcinema");
        Queue = new DownloadQueue(Path.Combine(dataDir, "state.json"), Log);
        Watch = new WatchStore(Path.Combine(dataDir, "watchlist.json"), Log);

        var downloadClient = httpClientFactory.CreateClient("StreamCinemaDownload");
        downloadClient.Timeout = Timeout.InfiniteTimeSpan;
        Engine = new DownloadEngine(downloadClient, Log);

        Helper = new HelperClient(httpClientFactory.CreateClient("StreamCinemaApi"), Log);
    }

    private static PluginConfiguration Config =>
        Plugin.Instance?.Configuration ?? new PluginConfiguration();

    /// <summary>kra.sk klient odpovídající aktuální konfiguraci.</summary>
    public KraskaClient Kraska
    {
        get
        {
            EnsureCurrent();
            return _kraska!;
        }
    }

    /// <summary>Katalog SC odpovídající aktuální konfiguraci.</summary>
    public ScCatalog Catalog
    {
        get
        {
            EnsureCurrent();
            return _catalog!;
        }
    }

    /// <summary>
    /// Aktivní X-AUTH-TOKEN: ruční má přednost, jinak bootstrapovaný.
    /// Prázdný string = token není k dispozici (GUI zobrazí návod).
    /// </summary>
    public string GetAuthToken()
    {
        var cfg = Config;
        if (!string.IsNullOrWhiteSpace(cfg.ManualAuthToken))
        {
            return cfg.ManualAuthToken.Trim();
        }

        return cfg.BootstrappedAuthToken?.Trim() ?? string.Empty;
    }

    /// <summary>
    /// Pokusí se bootstrapovat token ze sc.json na kra.sk úložišti a uložit ho.
    /// NIKDY negeneruje nový token. Vrací true při úspěchu.
    /// </summary>
    public async Task<bool> TryBootstrapTokenAsync(CancellationToken ct)
    {
        var token = await Catalog.BootstrapTokenAsync(Kraska, ct).ConfigureAwait(false);
        if (token == null)
        {
            return false;
        }

        var plugin = Plugin.Instance;
        if (plugin != null)
        {
            plugin.Configuration.BootstrappedAuthToken = token;
            plugin.SaveConfiguration();
        }

        return true;
    }

    private void EnsureCurrent()
    {
        var cfg = Config;
        var snapshot = string.Join("", cfg.KraskaUsername, cfg.KraskaPassword, cfg.DeviceUuid, cfg.UserAgent, cfg.CatalogLanguage);

        lock (_lock)
        {
            if (_kraska != null && snapshot == _snapshot)
            {
                return;
            }

            _snapshot = snapshot;

            var apiClient = _httpClientFactory.CreateClient("StreamCinemaApi");
            _kraska = new KraskaClient(apiClient, cfg.KraskaUsername, cfg.KraskaPassword, Log);

            var kraskaRef = _kraska;
            _catalog = new ScCatalog(
                apiClient,
                cfg.DeviceUuid,
                cfg.CatalogLanguage,
                cfg.UserAgent,
                GetAuthToken,
                () => kraskaRef.IsLoggedIn || kraskaRef.HasCredentials,
                Log);

            _logger.LogInformation("StreamCinema: klienti přestavěni (změna konfigurace)");
        }
    }

    private void Log(string message) => _logger.LogInformation("StreamCinema: {Message}", message);

    public void Dispose()
    {
    }
}
