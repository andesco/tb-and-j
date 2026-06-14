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
    public async Task<IActionResult> Play(
        string torboxType,
        string itemId,
        string fileId,
        [FromQuery(Name = "s")] string? secret,
        CancellationToken cancellationToken)
    {
        var expectedSecret = Plugin.Instance.Configuration.PlaySecret;
        if (string.IsNullOrWhiteSpace(expectedSecret) || secret != expectedSecret)
            return NotFound();

        if (!_idField.TryGetValue(torboxType, out var idParam))
            return NotFound();

        var apiKey = Plugin.Instance.Configuration.TorBoxApiKey;
        if (string.IsNullOrWhiteSpace(apiKey))
            return StatusCode(503, "TorBox API key not configured.");

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
            if (!response.IsSuccessStatusCode)
            {
                _logger.LogWarning(
                    "TorBoxPlay: TorBox returned {StatusCode} for {Type}/{Item}/{File}",
                    (int)response.StatusCode,
                    torboxType,
                    itemId,
                    fileId);
                return StatusCode(502, "TorBox did not return a download URL.");
            }

            using var document = JsonDocument.Parse(body);
            var cdnUrl = document.RootElement.TryGetProperty("data", out var dataElement)
                ? dataElement.GetString()
                : null;
            if (string.IsNullOrWhiteSpace(cdnUrl)
                || !Uri.TryCreate(cdnUrl, UriKind.Absolute, out var cdnUri)
                || !string.Equals(cdnUri.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase))
            {
                _logger.LogWarning("TorBoxPlay: no CDN URL returned for {Type}/{Item}/{File}", torboxType, itemId, fileId);
                return StatusCode(502, "TorBox did not return a download URL.");
            }

            _logger.LogDebug("TorBoxPlay: {Type}/{Item}/{File} resolved to CDN URL", torboxType, itemId, fileId);
            return Redirect(cdnUri.ToString());
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "TorBoxPlay: request failed for {Type}/{Item}/{File}", torboxType, itemId, fileId);
            return StatusCode(502, "Failed to resolve TorBox download URL.");
        }
    }
}
