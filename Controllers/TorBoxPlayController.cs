using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;

namespace Jellyfin.Plugin.TorBoxSync.Controllers;

[ApiController]
[Route("torboxsync")]
public sealed class TorBoxPlayController : ControllerBase
{
    private static readonly Dictionary<string, string> _idField = new(StringComparer.OrdinalIgnoreCase)
    {
        { "torrents", "torrent_id" },
        { "usenet",   "usenet_id"  },
        { "webdl",    "web_id"     },
    };

    /// <summary>
    /// Unauthenticated play endpoint — Infuse (and other Jellyfin clients) make the
    /// stream request without Jellyfin credentials, so we cannot require auth here.
    /// The TorBox API key is kept server-side; the redirect target requires TorBox
    /// auth, so the content itself is still protected.
    /// The {**name} catch-all is human-readable metadata only; the redirect uses
    /// only torboxType, itemId, and fileId.
    /// </summary>
    [HttpGet("play/{torboxType}/{itemId}/{fileId}/{**name}")]
    [ProducesResponseType(StatusCodes.Status302Found)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status503ServiceUnavailable)]
    public IActionResult Play(string torboxType, string itemId, string fileId)
    {
        if (!_idField.TryGetValue(torboxType, out var idParam))
            return BadRequest($"Unknown TorBox type: {torboxType}");

        var apiKey = Plugin.Instance.Configuration.TorBoxApiKey;
        if (string.IsNullOrWhiteSpace(apiKey))
            return StatusCode(503, "TorBox API key not configured.");

        var redirectUrl =
            $"https://api.torbox.app/v1/api/{torboxType}/requestdl"
            + $"?token={Uri.EscapeDataString(apiKey)}"
            + $"&{idParam}={Uri.EscapeDataString(itemId)}"
            + $"&file_id={Uri.EscapeDataString(fileId)}"
            + "&redirect=true";

        return Redirect(redirectUrl);
    }
}
