using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;

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

    private readonly ILogger<TorBoxPlayController> _logger;

    public TorBoxPlayController(ILogger<TorBoxPlayController> logger)
    {
        _logger = logger;
    }

    /// <summary>
    /// Redirects to TorBox CDN. The {secret} path segment is a random token stored in
    /// plugin config and embedded in every STRM URL — wrong or missing secret returns
    /// 404 so the endpoint does not reveal its own existence to scanners.
    /// No Jellyfin auth is required here because Infuse sends stream requests without
    /// credentials; the secret provides the access control instead.
    /// </summary>
    [HttpGet("play/{secret}/{torboxType}/{itemId}/{fileId}/{**name}")]
    [ProducesResponseType(StatusCodes.Status302Found)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType(StatusCodes.Status503ServiceUnavailable)]
    public IActionResult Play(string secret, string torboxType, string itemId, string fileId)
    {
        var expectedSecret = Plugin.Instance.Configuration.PlaySecret;
        if (string.IsNullOrWhiteSpace(expectedSecret) || secret != expectedSecret)
            return NotFound();

        if (!_idField.TryGetValue(torboxType, out var idParam))
            return NotFound();

        var apiKey = Plugin.Instance.Configuration.TorBoxApiKey;
        if (string.IsNullOrWhiteSpace(apiKey))
            return StatusCode(503, "TorBox API key not configured.");

        var redirectUrl =
            $"https://api.torbox.app/v1/api/{torboxType}/requestdl"
            + $"?token={Uri.EscapeDataString(apiKey)}"
            + $"&{idParam}={Uri.EscapeDataString(itemId)}"
            + $"&file_id={Uri.EscapeDataString(fileId)}"
            + "&redirect=true";

        _logger.LogDebug("TorBoxPlay: {Type}/{Item}/{File} → TorBox", torboxType, itemId, fileId);
        return Redirect(redirectUrl);
    }
}
