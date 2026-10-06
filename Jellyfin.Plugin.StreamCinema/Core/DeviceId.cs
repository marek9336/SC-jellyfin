using System.Text;

namespace Jellyfin.Plugin.StreamCinema.Core;

/// <summary>
/// Identita zařízení vůči katalogu (hlavička X-Uuid).
///
/// Kodi addon Stream Cinema ji negeneruje jako hex UUID, ale jako skupiny
/// 8-4-4-4-12 ze znaků <c>0-9a-z</c> (viz <c>kodiutils._get_fake_uuid</c>).
/// Držíme se toho: hex uuid4 by na první pohled prozradil, že to není addon.
/// </summary>
public static class DeviceId
{
    private const string Alphabet = "0123456789abcdefghijklmnopqrstuvwxyz";

    private static readonly int[] Groups = [8, 4, 4, 4, 12];

    // Vzory, které si addon sám zahazuje a generuje znovu (viz _get_system_uuid).
    private static readonly string[] Rejected = ["-4a02-a401-", "-4a02-8000-", "-4000-8000-"];

    /// <summary>Nové UUID zařízení ve tvaru, jaký používá Kodi addon.</summary>
    public static string Generate()
    {
        string value;
        var attempt = 0;
        do
        {
            value = Build();
            attempt++;
        }
        while (attempt < 10 && Rejected.Any(r => value.Contains(r, StringComparison.Ordinal)));

        return value;
    }

    private static string Build()
    {
        var sb = new StringBuilder(36);
        for (var g = 0; g < Groups.Length; g++)
        {
            if (g > 0)
            {
                sb.Append('-');
            }

            for (var i = 0; i < Groups[g]; i++)
            {
                sb.Append(Alphabet[Random.Shared.Next(Alphabet.Length)]);
            }
        }

        return sb.ToString();
    }
}
