using System.IO;
using System.Net;
using System.Net.Http;
using Martlet.Core.Nodes;

namespace Martlet.Desktop;

/// <summary>Where this PC keeps the files it sends hosts without internet access (<see cref="HostSupplier"/>), and how it
/// downloads them. Also compiled into Martlet's MCP server for host_supply_check.</summary>
internal static class HostSupplies
{
    private static readonly HttpClient Http = CreateClient();

    internal static string Cache(string dataDirectory) => Path.Combine(dataDirectory, "host-supply");

    /// <summary><see cref="HostSupplyOpen"/> over HTTPS: null for 404, an exception for any other failure.</summary>
    internal static async Task<HostSupplyDownload?> OpenAsync(string url, CancellationToken token)
    {
        var response = await Http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, token);
        try
        {
            if (response.StatusCode == HttpStatusCode.NotFound)
            {
                response.Dispose();
                return null;
            }
            response.EnsureSuccessStatusCode();
            return new(await response.Content.ReadAsStreamAsync(token), response.Content.Headers.ContentLength, response);
        }
        catch
        {
            response.Dispose();
            throw;
        }
    }

    private static HttpClient CreateClient()
    {
        // The timeout covers the response headers; a long download continues until it ends or is canceled.
        var client = new HttpClient { Timeout = TimeSpan.FromMinutes(2) };
        client.DefaultRequestHeaders.UserAgent.ParseAdd("Martlet/" +
            (typeof(HostSupplies).Assembly.GetName().Version?.ToString(3) ?? "0.0.0"));
        return client;
    }
}
