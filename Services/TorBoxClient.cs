using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using Jellyfin.Plugin.TorBoxSync.Models;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.TorBoxSync.Services;

public sealed class TorBoxClient
{
    private const string ApiBase = "https://api.torbox.app/v1/api";
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    private readonly IHttpClientFactory _httpClientFactory;
    private readonly ILogger<TorBoxClient> _logger;

    public TorBoxClient(IHttpClientFactory httpClientFactory, ILogger<TorBoxClient> logger)
    {
        _httpClientFactory = httpClientFactory;
        _logger = logger;
    }

    public async Task<IReadOnlyList<TorBoxFileCandidate>> GetManagedVideoFilesAsync(
        string torBoxType,
        PluginConfiguration configuration,
        CancellationToken cancellationToken)
    {
        var files = new List<TorBoxFileCandidate>();
        var offset = 0;
        const int limit = 1000;

        while (true)
        {
            var url = $"{ApiBase}/{torBoxType}/mylist?limit={limit}&offset={offset}&bypass_cache=true";
            using var response = await SendAsync(HttpMethod.Get, url, configuration.TorBoxApiKey, null, cancellationToken).ConfigureAwait(false);
            await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
            using var document = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken).ConfigureAwait(false);

            if (!document.RootElement.TryGetProperty("data", out var dataElement) || dataElement.ValueKind != JsonValueKind.Array)
            {
                break;
            }

            var pageCount = 0;
            foreach (var itemElement in dataElement.EnumerateArray())
            {
                pageCount++;
                if (itemElement.TryGetProperty("cached", out var cachedElement)
                    && cachedElement.ValueKind == JsonValueKind.False)
                {
                    continue;
                }

                var itemId = GetString(itemElement, "id");
                var itemName = GetString(itemElement, "name");
                if (string.IsNullOrWhiteSpace(itemId))
                {
                    continue;
                }

                if (!itemElement.TryGetProperty("files", out var fileElements) || fileElements.ValueKind != JsonValueKind.Array)
                {
                    continue;
                }

                foreach (var fileElement in fileElements.EnumerateArray())
                {
                    var fileName = GetString(fileElement, "short_name");
                    var filePath = GetString(fileElement, "name");
                    var fileId = GetString(fileElement, "id");
                    var mimeType = GetString(fileElement, "mimetype");
                    if (string.IsNullOrWhiteSpace(fileName) || string.IsNullOrWhiteSpace(fileId))
                    {
                        continue;
                    }

                    if (!IsAllowedVideo(fileName, mimeType, configuration.AllowedVideoExtensions))
                    {
                        continue;
                    }

                    files.Add(new TorBoxFileCandidate
                    {
                        TorBoxType = torBoxType,
                        ItemId = itemId,
                        FileId = fileId,
                        ItemName = itemName,
                        FileName = fileName,
                        Path = filePath,
                        MimeType = mimeType,
                        DownloadLink = BuildDownloadLink(torBoxType, itemId, fileId, configuration.TorBoxApiKey)
                    });
                }
            }

            if (pageCount < limit)
            {
                break;
            }

            offset += limit;
        }

        _logger.LogInformation("Fetched {Count} managed video files from TorBox {Type}", files.Count, torBoxType);
        return files;
    }

    public async Task DeleteDownloadAsync(
        string torBoxType,
        string torBoxItemId,
        PluginConfiguration configuration,
        CancellationToken cancellationToken)
    {
        var (endpoint, idField, operation) = torBoxType switch
        {
            "torrents" => ("torrents/controltorrent", "torrent_id", "Delete"),
            "usenet" => ("usenet/controlusenetdownload", "usenet_id", "delete"),
            "webdl" => ("webdl/controlwebdownload", "download_id", "delete"),
            _ => throw new InvalidOperationException($"Unsupported TorBox type '{torBoxType}'.")
        };

        var body = JsonSerializer.Serialize(new Dictionary<string, object?>
        {
            [idField] = torBoxItemId,
            ["operation"] = operation,
            ["all"] = false
        }, JsonOptions);

        using var response = await SendAsync(
            HttpMethod.Post,
            $"{ApiBase}/{endpoint}",
            configuration.TorBoxApiKey,
            new StringContent(body, Encoding.UTF8, "application/json"),
            cancellationToken).ConfigureAwait(false);

        var responseBody = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
        if (!string.IsNullOrWhiteSpace(responseBody))
        {
            using var document = JsonDocument.Parse(responseBody);
            if (document.RootElement.TryGetProperty("success", out var successElement)
                && successElement.ValueKind == JsonValueKind.False)
            {
                var detail = document.RootElement.TryGetProperty("detail", out var detailElement)
                    ? detailElement.GetString()
                    : responseBody;
                throw new InvalidOperationException($"TorBox delete failed for {torBoxType}:{torBoxItemId}: {detail}");
            }
        }
    }

    private async Task<HttpResponseMessage> SendAsync(
        HttpMethod method,
        string url,
        string apiKey,
        HttpContent? content,
        CancellationToken cancellationToken)
    {
        var client = _httpClientFactory.CreateClient();
        using var request = new HttpRequestMessage(method, url);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", apiKey);
        request.Headers.UserAgent.ParseAdd("Jellyfin-TorBox-Sync/0.1.0");
        request.Content = content;

        var response = await client.SendAsync(request, cancellationToken).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
        {
            var body = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
            throw new HttpRequestException($"TorBox API returned {(int)response.StatusCode} for {url}: {body}");
        }

        return response;
    }

    private static string BuildDownloadLink(string torBoxType, string itemId, string fileId, string apiKey)
    {
        var idParameter = torBoxType switch
        {
            "torrents" => "torrent_id",
            "usenet" => "usenet_id",
            "webdl" => "web_id",
            _ => throw new InvalidOperationException($"Unsupported TorBox type '{torBoxType}'.")
        };

        return $"{ApiBase}/{torBoxType}/requestdl?token={Uri.EscapeDataString(apiKey)}&{idParameter}={Uri.EscapeDataString(itemId)}&file_id={Uri.EscapeDataString(fileId)}&redirect=true";
    }

    private static bool IsAllowedVideo(string fileName, string mimeType, IReadOnlyCollection<string> allowedExtensions)
    {
        if (mimeType.StartsWith("video/", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        var extension = Path.GetExtension(fileName);
        return allowedExtensions.Contains(extension, StringComparer.OrdinalIgnoreCase);
    }

    private static string GetString(JsonElement element, string propertyName)
    {
        if (!element.TryGetProperty(propertyName, out var property))
        {
            return string.Empty;
        }

        return property.ValueKind switch
        {
            JsonValueKind.String => property.GetString() ?? string.Empty,
            JsonValueKind.Number => property.GetRawText(),
            JsonValueKind.True => "true",
            JsonValueKind.False => "false",
            _ => string.Empty
        };
    }
}
