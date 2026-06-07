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
    /// Redirects to TorBox. The ?s= query param is a per-instance secret stored in
    /// plugin config and appended to every STRM URL at sync time. Wrong or missing
    /// secret returns 404 so the endpoint does not reveal its own existence.
    /// No Jellyfin auth is required — Infuse sends stream requests as raw HTTP
    /// with no credentials; the secret provides the access control instead.
    /// </summary>
    [HttpGet("play/{torboxType}/{itemId}/{fileId}/{**name}")]
    [ProducesResponseType(StatusCodes.Status302Found)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType(StatusCodes.Status503ServiceUnavailable)]
    public IActionResult Play(
        string torboxType,
        string itemId,
        string fileId,
        [FromQuery(Name = "s")] string? secret)
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

        _logger.LogDebug("TorBoxPlay: {Type}/{Item}/{File}", torboxType, itemId, fileId);
        return Redirect(redirectUrl);
    }
}
