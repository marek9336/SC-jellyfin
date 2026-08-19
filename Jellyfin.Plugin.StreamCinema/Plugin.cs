using Jellyfin.Plugin.StreamCinema.Configuration;
using MediaBrowser.Common.Configuration;
using MediaBrowser.Common.Plugins;
using MediaBrowser.Model.Plugins;
using MediaBrowser.Model.Serialization;

namespace Jellyfin.Plugin.StreamCinema;

/// <summary>
/// Vstupní bod pluginu. Drží konfiguraci a registruje konfigurační stránku.
/// (Vazba na Jellyfin API — při migraci na JF 12 se upravuje tenhle soubor, ne Core/.)
/// </summary>
public class Plugin : BasePlugin<PluginConfiguration>, IHasWebPages
{
    public static readonly Guid PluginId = Guid.Parse("d7c4b2a1-3f5e-4b8c-9a76-0e1f2d3c4b5a");

    public Plugin(IApplicationPaths applicationPaths, IXmlSerializer xmlSerializer)
        : base(applicationPaths, xmlSerializer)
    {
        Instance = this;

        // Stabilní UUID zařízení — generuje se jen jednou (anti-ban: jedna identita)
        if (string.IsNullOrEmpty(Configuration.DeviceUuid))
        {
            Configuration.DeviceUuid = Guid.NewGuid().ToString();
            SaveConfiguration();
        }
    }

    public static Plugin? Instance { get; private set; }

    public override string Name => "Stream Cinema";

    public override string Description =>
        "Vyhledávání v katalogu Stream Cinema a stahování z kra.sk do knihovny Jellyfinu.";

    public override Guid Id => PluginId;

    public IEnumerable<PluginPageInfo> GetPages()
    {
        return
        [
            new PluginPageInfo
            {
                Name = "streamcinema",
                EmbeddedResourcePath = $"{GetType().Namespace}.Configuration.configPage.html",
            },
        ];
    }
}
