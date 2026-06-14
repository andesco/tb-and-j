using System.Net;
using System.Text;
using Jellyfin.Plugin.TorBoxSync.Models;
using Jellyfin.Plugin.TorBoxSync.Services;
using Microsoft.Extensions.Logging.Abstractions;

namespace Jellyfin.Plugin.TorBoxSync.Tests;

public sealed class TorBoxClientTests
{
    [Fact]
    public async Task GetManagedVideoFilesAsync_RejectsResponseWithoutDataArray()
    {
        var client = CreateClient("""{"success":true}""");

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            client.GetManagedVideoFilesAsync("torrents", new PluginConfiguration(), CancellationToken.None));
    }

    [Fact]
    public async Task GetManagedVideoFilesAsync_PreservesUnavailableItemIdentity()
    {
        var client = CreateClient(
            """
            {
              "data": [
                {
                  "id": "123",
                  "name": "Temporarily unavailable",
                  "cached": false
                }
              ]
            }
            """);

        var snapshot = await client.GetManagedVideoFilesAsync(
            "torrents",
            new PluginConfiguration(),
            CancellationToken.None);

        var itemKey = TorBoxDeletionRecord.BuildKey("torrents", "123");
        Assert.Empty(snapshot.Candidates);
        Assert.Contains(itemKey, snapshot.ObservedItemKeys);
        Assert.Contains(itemKey, snapshot.UnavailableItemKeys);
    }

    private static TorBoxClient CreateClient(string responseBody)
    {
        var handler = new StubHttpMessageHandler(responseBody);
        return new TorBoxClient(new StubHttpClientFactory(handler), NullLogger<TorBoxClient>.Instance);
    }

    private sealed class StubHttpClientFactory(HttpMessageHandler handler) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => new(handler, disposeHandler: false);
    }

    private sealed class StubHttpMessageHandler(string responseBody) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
            => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(responseBody, Encoding.UTF8, "application/json")
            });
    }
}
