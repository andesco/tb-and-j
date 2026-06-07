using System.Text.Json;
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

    private readonly IHttpClientFactory _httpClientFactory;
    private readonly ILogger<TorBoxPlayController> _logger;

    public TorBoxPlayController(IHttpClientFactory httpClientFactory, ILogger<TorBoxPlayController> logger)
    {
        _httpClientFactory = httpClientFactory;
        _logger = logger;
    }

    /// <summary>
    /// Resolves a TorBox item to a time-limited CDN URL server-side, then issues a
    /// 302 to the CDN URL. The TorBox API key is never sent to the client — only the
    /// short-lived CDN link is exposed, which carries no account credentials.
    /// No Jellyfin auth is required because Infuse makes stream requests without
    /// credentials; the CDN URL itself is what limits access.
    /// </summary>
    [HttpGet("play/{torboxType}/{itemId}/{fileId}/{**name}")]
    [ProducesResponseType(StatusCodes.Status302Found)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status502BadGateway)]
    [ProducesResponseType(StatusCodes.Status503ServiceUnavailable)]
    public async Task<IActionResult> Play(string torboxType, string itemId, string fileId, CancellationToken cancellationToken)
    {
        if (!_idField.TryGetValue(torboxType, out var idParam))
            return BadRequest($"Unknown TorBox type: {torboxType}");

        var apiKey = Plugin.Instance.Configuration.TorBoxApiKey;
        if (string.IsNullOrWhiteSpace(apiKey))
            return StatusCode(503, "TorBox API key not configured.");

        // Call requestdl WITHOUT redirect=true — TorBox returns JSON with the CDN URL.
        // This keeps the API key server-side; the client only ever sees the CDN link.
        var resolveUrl =
            $"https://api.torbox.app/v1/api/{torboxType}/requestdl"
            + $"?token={Uri.EscapeDataString(apiKey)}"
            + $"&{idParam}={Uri.EscapeDataString(itemId)}"
            + $"&file_id={Uri.EscapeDataString(fileId)}";

        try
        {
            var client = _httpClientFactory.CreateClient();
            using var response = await client.GetAsync(resolveUrl, cancellationToken).ConfigureAwait(false);
            var body = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);

            using var doc = JsonDocument.Parse(body);
            var cdnUrl = doc.RootElement.TryGetProperty("data", out var data) ? data.GetString() : null;

            if (!string.IsNullOrWhiteSpace(cdnUrl))
            {
                _logger.LogDebug("TorBoxPlay: {Type}/{Item}/{File} → CDN redirect", torboxType, itemId, fileId);
                return Redirect(cdnUrl);
            }

            _logger.LogWarning("TorBoxPlay: no CDN URL in response for {Type}/{Item}/{File}: {Body}",
                torboxType, itemId, fileId, body[..Math.Min(300, body.Length)]);
            return StatusCode(502, "TorBox did not return a download URL.");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "TorBoxPlay: requestdl failed for {Type}/{Item}/{File}", torboxType, itemId, fileId);
            return StatusCode(502, "Failed to resolve TorBox download URL.");
        }
    }
}
