using System.Net;
using System.Text.Json;
using Martlet.Core.Contracts;
using Martlet.Core.Speakers;

namespace Martlet.Avatar.Audio2Face.Remote;

/// <summary>The host's copy of the shared voice list (the people the owner's computers recognize by voice).</summary>
public sealed partial class Audio2FaceHostConnection
{
    private const string VoicesPath = "/martlet/v1/voices";

    /// <summary>Reads the host's copy of the voice list. Hosts older than voice sharing refuse with code
    /// <c>request.invalid</c> (or answer not found).</summary>
    public async Task<VoiceRoster> ReadVoicesAsync(CancellationToken cancellationToken = default)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, pairing.Origin + VoicesPath);
        Sign(request, []);
        return await VoicesAsync(request, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Merges <paramref name="roster"/> into the host's copy and returns the merged list, which may include changes
    /// another computer made.</summary>
    public async Task<VoiceRoster> MergeVoicesAsync(VoiceRoster roster, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(roster);
        var body = roster.Write();
        using var request = new HttpRequestMessage(HttpMethod.Post, pairing.Origin + VoicesPath)
        {
            Content = Audio2FaceHostClient.JsonContent(body)
        };
        Sign(request, body);
        return await VoicesAsync(request, cancellationToken).ConfigureAwait(false);
    }

    private async Task<VoiceRoster> VoicesAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(10));
        using var response = await Audio2FaceHostClient.Send(http, request, timeout.Token).ConfigureAwait(false);
        using var document = await Audio2FaceHostClient.ReadJson(response, VoiceRoster.MaximumBytes + 4096, timeout.Token).ConfigureAwait(false);
        if (response.StatusCode != HttpStatusCode.OK) throw Audio2FaceHostClient.Remote(document.RootElement);
        try
        {
            var root = document.RootElement;
            if (root.GetProperty("host_id").GetString() != pairing.HostId)
                throw new Audio2FaceHostException("response.invalid", "The host identity changed; pair again.");
            return VoiceRoster.Parse(JsonSerializer.SerializeToUtf8Bytes(root.GetProperty("roster")));
        }
        catch (Exception error) when (error is KeyNotFoundException or InvalidOperationException or ContractException)
        {
            throw new Audio2FaceHostException("response.invalid", "The host's copy of the voice list was invalid.");
        }
    }
}
