using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;

namespace Jellyfin.Plugin.TorBoxSync.Controllers;

[ApiController]
[Authorize(AuthenticationSchemes = "CustomAuthentication")]
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
    /// Authenticated Jellyfin play endpoint — redirects to the TorBox CDN download URL.
    /// The trailing {**name} segment is human-readable and ignored by the redirect logic.
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
