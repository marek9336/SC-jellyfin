using System.ComponentModel.DataAnnotations;
using Jellyfin.Plugin.StreamCinema.Core;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.StreamCinema.Api;

/// <summary>
/// REST API pro konfigurační stránku pluginu. Jen pro adminy.
/// Všechny endpointy jsou pod /Plugins/StreamCinema/...
/// </summary>
[ApiController]
[Authorize(Policy = "RequiresElevation")]
[Route("Plugins/StreamCinema")]
[Produces("application/json")]
public class ScController : ControllerBase
{
    private readonly ScState _state;
    private readonly ILogger<ScController> _logger;

    public ScController(ScState state, ILogger<ScController> logger)
    {
        _state = state;
        _logger = logger;
    }

    /// <summary>Test přihlášení ke kra.sk + info o předplatném.</summary>
    [HttpPost("TestLogin")]
    public async Task<ActionResult> TestLogin(CancellationToken ct)
    {
        _state.Kraska.InvalidateSession();
        var ok = await _state.Kraska.LoginAsync(ct).ConfigureAwait(false);
        if (!ok)
        {
            var reason = _state.Kraska.LastError ?? "zkontroluj jméno a heslo.";
            _logger.LogWarning("StreamCinema: TestLogin selhal — {Reason}", reason);
            return Ok(new { success = false, message = "Přihlášení selhalo — " + reason });
        }

        var info = await _state.Kraska.UserInfoAsync(ct).ConfigureAwait(false);
        return Ok(new
        {
            success = true,
            daysLeft = info?.DaysLeft,
            subscribedUntil = info?.SubscribedUntil,
            message = info == null
                ? "Přihlášeno, ale info o předplatném se nepodařilo načíst."
                : $"Přihlášeno. Předplatné: {info.DaysLeft} dní.",
        });
    }

    /// <summary>
    /// Pokus o auto-bootstrap X-AUTH-TOKENu ze sc.json na kra.sk úložišti.
    /// Nikdy negeneruje nový token.
    /// </summary>
    [HttpPost("BootstrapToken")]
    public async Task<ActionResult> BootstrapToken(CancellationToken ct)
    {
        var ok = await _state.TryBootstrapTokenAsync(ct).ConfigureAwait(false);
        return Ok(new
        {
            success = ok,
            message = ok
                ? "Token načten ze zálohy sc.json na tvém kra.sk úložišti."
                : "sc.json se nepodařilo načíst. Zadej token ručně: přihlas se na kra.sk → Úložiště → "
                  + "stáhni soubor sc.json → otevři ho v poznámkovém bloku → zkopíruj 32 znaků do pole „Ruční token“.",
        });
    }

    /// <summary>
    /// Generické procházení katalogu — vrací surovou JSON odpověď SC API.
    /// GUI funguje jako prohlížeč: zobrazí položky a následuje jejich `url`.
    /// </summary>
    [HttpPost("Browse")]
    public async Task<ActionResult> Browse([FromBody] BrowseRequest request, CancellationToken ct)
    {
        if (string.IsNullOrEmpty(_state.GetAuthToken()))
        {
            return Ok(new { error = "missing_token" });
        }

        try
        {
            using var doc = await _state.Catalog
                .GetAsync(request.Path, request.Params, ct).ConfigureAwait(false);
            return Content(doc.RootElement.GetRawText(), "application/json");
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "StreamCinema: browse {Path} selhal", request.Path);
            return Ok(new { error = ex.Message });
        }
    }

    /// <summary>Hledání — zkratka nad Browse.</summary>
    [HttpPost("Search")]
    public Task<ActionResult> Search([FromBody] SearchRequest request, CancellationToken ct)
    {
        var type = request.Type == "series" ? "search-series" : "search-movies";
        return Browse(new BrowseRequest
        {
            Path = $"/Search/{type}",
            Params = new Dictionary<string, string> { ["search"] = request.Query, ["id"] = type },
        }, ct);
    }

    /// <summary>Načte /Play/... a vrátí seznam streamů k výběru.</summary>
    [HttpPost("Streams")]
    public async Task<ActionResult> Streams([FromBody] BrowseRequest request, CancellationToken ct)
    {
        try
        {
            using var doc = await _state.Catalog.GetAsync(request.Path, request.Params, ct).ConfigureAwait(false);
            var streams = ScCatalog.ParseStreams(doc);
            return Ok(new { streams });
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "StreamCinema: načtení streamů {Path} selhalo", request.Path);
            return Ok(new { error = ex.Message });
        }
    }

    /// <summary>Přidá vybraný stream do fronty stahování.</summary>
    [HttpPost("Queue")]
    public ActionResult AddToQueue([FromBody] AddQueueRequest request)
    {
        if (string.IsNullOrWhiteSpace(request.Ident) && string.IsNullOrWhiteSpace(request.StreamUrl))
        {
            return BadRequest(new { error = "Chybí stream (ident ani URL)" });
        }

        var item = new QueueItem
        {
            Title = request.Title ?? "Neznámý",
            Year = request.Year,
            MediaType = request.MediaType == "episode" ? ScMediaType.Episode : ScMediaType.Movie,
            SeriesTitle = request.SeriesTitle,
            Season = request.Season,
            Episode = request.Episode,
            Ident = request.Ident ?? string.Empty,
            StreamUrl = request.StreamUrl,
            SubsUrl = request.SubsUrl,
            SubsLang = request.SubsLang,
            Quality = request.Quality,
            Language = request.Language,
            SizeText = request.SizeText,
        };

        _state.Queue.Add(item);
        return Ok(new { success = true, id = item.Id });
    }

    /// <summary>
    /// ⚡ Automatický výběr: načte streamy z /Play/..., vybere nejlepší podle
    /// priorit v nastavení (jazyk → kvalita/velikost → kodek → HDR/DV/Atmos)
    /// a rovnou ho zařadí do fronty. Vrací popis vybraného streamu.
    /// </summary>
    [HttpPost("QueueAuto")]
    public async Task<ActionResult> QueueAuto([FromBody] QueueAutoRequest request, CancellationToken ct)
    {
        try
        {
            using var doc = await _state.Catalog.GetAsync(request.Path, null, ct).ConfigureAwait(false);
            var streams = ScCatalog.ParseStreams(doc);
            if (streams.Count == 0)
            {
                return Ok(new { error = "Žádné streamy k dispozici." });
            }

            var cfg = Plugin.Instance?.Configuration ?? new Configuration.PluginConfiguration();
            var options = new StreamSelectorOptions
            {
                Lang1 = cfg.PreferredLang1,
                Lang2 = cfg.PreferredLang2,
                Lang3 = cfg.PreferredLang3,
                SkipWithoutPreferredLang = cfg.SkipWithoutPreferredLang,
                MaxQuality = cfg.MaxQuality,
                MaxFileSizeGb = cfg.MaxFileSizeGb,
                CodecPreference = cfg.CodecPreference,
                HdrMode = cfg.HdrMode,
                DvMode = cfg.DvMode,
                AtmosMode = cfg.AtmosMode,
            };

            var (best, reason) = StreamSelector.SelectBest(streams, options);
            if (best == null)
            {
                return Ok(new { error = "Autoselect: " + reason });
            }

            var item = new QueueItem
            {
                Title = request.Title ?? "Neznámý",
                Year = request.Year,
                MediaType = request.MediaType == "episode" ? ScMediaType.Episode : ScMediaType.Movie,
                SeriesTitle = request.SeriesTitle,
                Season = request.Season,
                Episode = request.Episode,
                Ident = best.Ident,
                StreamUrl = best.Url,
                SubsUrl = best.SubsUrl,
                Quality = best.Quality,
                Language = best.Languages.Count > 0 ? string.Join(",", best.Languages) : best.Language,
                SizeText = best.SizeText,
            };

            _state.Queue.Add(item);
            _logger.LogInformation("StreamCinema: autoselect \"{Title}\" → {Reason}", item.Title, reason);
            return Ok(new { success = true, id = item.Id, picked = reason });
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "StreamCinema: QueueAuto {Path} selhal", request.Path);
            return Ok(new { error = ex.Message });
        }
    }

    /// <summary>Obsah fronty.</summary>
    [HttpGet("Queue")]
    public ActionResult GetQueue()
    {
        return Ok(new { items = _state.Queue.GetAll().OrderByDescending(i => i.AddedUtc) });
    }

    /// <summary>Odebere položku z fronty (probíhající stahování odebrat nejde).</summary>
    [HttpDelete("Queue/{id}")]
    public ActionResult RemoveFromQueue([FromRoute] Guid id)
    {
        return Ok(new { success = _state.Queue.Remove(id) });
    }

    /// <summary>Vrátí chybnou položku zpět do fronty.</summary>
    [HttpPost("Queue/{id}/Retry")]
    public ActionResult RetryQueueItem([FromRoute] Guid id)
    {
        return Ok(new { success = _state.Queue.Retry(id) });
    }

    /// <summary>Pozastaví / obnoví worker.</summary>
    [HttpPost("Worker/{action}")]
    public ActionResult WorkerControl([FromRoute][Required] string action)
    {
        switch (action.ToLowerInvariant())
        {
            case "pause":
                _state.Queue.WorkerPaused = true;
                return Ok(new { success = true, paused = true });
            case "resume":
                _state.Queue.WorkerPaused = false;
                return Ok(new { success = true, paused = false });
            default:
                return BadRequest(new { error = "Neznámá akce" });
        }
    }

    /// <summary>Stav workeru pro GUI (poll ~5 s).</summary>
    [HttpGet("Status")]
    public ActionResult GetStatus()
    {
        var s = _state.Status;
        return Ok(new
        {
            paused = _state.Queue.WorkerPaused,
            hasToken = !string.IsNullOrEmpty(_state.GetAuthToken()),
            currentItemId = s.CurrentItemId,
            currentItemTitle = s.CurrentItemTitle,
            currentBytesDone = s.CurrentBytesDone,
            currentBytesTotal = s.CurrentBytesTotal,
            currentSpeedBps = s.CurrentSpeedBps,
            nextActionUtc = s.NextActionUtc,
            lastMessage = s.LastMessage,
            dailyBytes = _state.Queue.GetDailyBytes(),
            freeSpaceBytes = s.FreeSpaceBytes,
            kraskaDaysLeft = s.KraskaDaysLeft,
        });
    }
}

public class BrowseRequest
{
    [Required]
    public string Path { get; set; } = string.Empty;

    public Dictionary<string, string>? Params { get; set; }
}

public class SearchRequest
{
    [Required]
    public string Query { get; set; } = string.Empty;

    /// <summary>"movies" | "series".</summary>
    public string Type { get; set; } = "movies";
}

public class QueueAutoRequest
{
    [Required]
    public string Path { get; set; } = string.Empty;

    public string? Title { get; set; }

    public int? Year { get; set; }

    /// <summary>"movie" | "episode".</summary>
    public string? MediaType { get; set; }

    public string? SeriesTitle { get; set; }

    public int? Season { get; set; }

    public int? Episode { get; set; }
}

public class AddQueueRequest
{
    /// <summary>Přímý kra.sk ident (starší tvar API). Stačí jeden z Ident/StreamUrl.</summary>
    public string? Ident { get; set; }

    /// <summary>Resolve URL streamu z katalogu (běžný tvar) — ident se získá při stahování.</summary>
    public string? StreamUrl { get; set; }

    public string? Title { get; set; }

    public int? Year { get; set; }

    /// <summary>"movie" | "episode".</summary>
    public string? MediaType { get; set; }

    public string? SeriesTitle { get; set; }

    public int? Season { get; set; }

    public int? Episode { get; set; }

    public string? SubsUrl { get; set; }

    public string? SubsLang { get; set; }

    public string? Quality { get; set; }

    public string? Language { get; set; }

    public string? SizeText { get; set; }
}
