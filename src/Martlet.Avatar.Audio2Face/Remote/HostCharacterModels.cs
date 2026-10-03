using System.Net;
using System.Security.Cryptography;
using System.Text.Json;
using Martlet.Core.Characters;
using Martlet.Core.Contracts;

namespace Martlet.Avatar.Audio2Face.Remote;

/// <summary>A host's copy of the character models the owner added and the SHA-256 of each piece it holds.</summary>
public sealed record HostCharacterModels(CharacterModelLibrary Library, IReadOnlySet<string> Present);

/// <summary>The host's copy of the shared character models and their pieces (docs/CLUSTER.md, "The shared character
/// models"). Hosts older than it refuse with <c>request.invalid</c>.</summary>
public sealed partial class Audio2FaceHostConnection
{
    private const string CharacterModelsPath = "/martlet/v1/character-models";
    private const string CharacterModelChunkPath = CharacterModelsPath + "/chunks/";
    private const int MaximumCharacterModelsBytes = CharacterModelLibrary.MaximumBytes + 262_144;
    private const int MaximumCharacterModelChunkBytes = (CharacterModelLibrary.ChunkBytes + 2) / 3 * 4 + 4_096;

    public async Task<HostCharacterModels> ReadCharacterModelsAsync(CancellationToken cancellationToken = default)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, pairing.Origin + CharacterModelsPath);
        Sign(request, []);
        return await CharacterModelsAsync(request, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Merges <paramref name="library"/> into the host's copy and returns the merged list, which may include changes
    /// another computer made, and the pieces the host holds.</summary>
    public async Task<HostCharacterModels> MergeCharacterModelsAsync(CharacterModelLibrary library, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(library);
        var body = library.Write();
        using var request = new HttpRequestMessage(HttpMethod.Post, pairing.Origin + CharacterModelsPath)
        {
            Content = Audio2FaceHostClient.JsonContent(body)
        };
        Sign(request, body);
        return await CharacterModelsAsync(request, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Sends one piece of a live model to the host and returns the pieces it now holds. The host keeps it only when
    /// its list has a live model with that piece (merge the list first).</summary>
    public async Task<IReadOnlySet<string>> SendCharacterModelChunkAsync(string sha256, ReadOnlyMemory<byte> data,
        CancellationToken cancellationToken = default)
    {
        if (!CharacterModelLibrary.IsSha256(sha256)) throw new ArgumentException("Invalid piece SHA-256.", nameof(sha256));
        // Base64 needs no escaping; writing it as is keeps the body within the gateway's limit (the default encoder would
        // escape every '+').
        var body = System.Text.Encoding.ASCII.GetBytes("{\"data_base64\":\"" + Convert.ToBase64String(data.Span) + "\"}");
        using var request = new HttpRequestMessage(HttpMethod.Post, pairing.Origin + CharacterModelChunkPath + sha256)
        {
            Content = Audio2FaceHostClient.JsonContent(body)
        };
        Sign(request, body);
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(120));
        using var response = await Audio2FaceHostClient.Send(http, request, timeout.Token).ConfigureAwait(false);
        using var document = await Audio2FaceHostClient.ReadJson(response, MaximumCharacterModelsBytes, timeout.Token).ConfigureAwait(false);
        if (response.StatusCode != HttpStatusCode.OK) throw Audio2FaceHostClient.Remote(document.RootElement);
        return Present(document.RootElement, "present");
    }

    /// <summary>Reads a piece the host holds, checked against <paramref name="sha256"/>; null when it has none.</summary>
    public async Task<byte[]?> ReadCharacterModelChunkAsync(string sha256, CancellationToken cancellationToken = default)
    {
        if (!CharacterModelLibrary.IsSha256(sha256)) throw new ArgumentException("Invalid piece SHA-256.", nameof(sha256));
        using var request = new HttpRequestMessage(HttpMethod.Get, pairing.Origin + CharacterModelChunkPath + sha256);
        Sign(request, []);
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(120));
        using var response = await Audio2FaceHostClient.Send(http, request, timeout.Token).ConfigureAwait(false);
        using var document = await Audio2FaceHostClient.ReadJson(response, MaximumCharacterModelChunkBytes, timeout.Token).ConfigureAwait(false);
        if (response.StatusCode != HttpStatusCode.OK)
        {
            var failure = Audio2FaceHostClient.Remote(document.RootElement);
            if (failure.Code == "chunk.missing") return null;
            throw failure;
        }
        try
        {
            var root = document.RootElement;
            if (root.GetProperty("host_id").GetString() != pairing.HostId)
                throw new Audio2FaceHostException("response.invalid", "The host identity changed; pair again.");
            var data = Convert.FromBase64String(root.GetProperty("data_base64").GetString() ?? "");
            if (root.GetProperty("chunk_sha256").GetString() != sha256 || data.Length > CharacterModelLibrary.ChunkBytes ||
                Convert.ToHexStringLower(SHA256.HashData(data)) != sha256)
                throw new Audio2FaceHostException("response.invalid", "The host's copy of a character piece was invalid.");
            return data;
        }
        catch (Exception error) when (error is KeyNotFoundException or InvalidOperationException or FormatException)
        {
            throw new Audio2FaceHostException("response.invalid", "The host's copy of a character piece was invalid.");
        }
    }

    private async Task<HostCharacterModels> CharacterModelsAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(20));
        using var response = await Audio2FaceHostClient.Send(http, request, timeout.Token).ConfigureAwait(false);
        using var document = await Audio2FaceHostClient.ReadJson(response, MaximumCharacterModelsBytes, timeout.Token).ConfigureAwait(false);
        if (response.StatusCode != HttpStatusCode.OK) throw Audio2FaceHostClient.Remote(document.RootElement);
        try
        {
            var root = document.RootElement;
            if (root.GetProperty("host_id").GetString() != pairing.HostId)
                throw new Audio2FaceHostException("response.invalid", "The host identity changed; pair again.");
            return new(CharacterModelLibrary.Parse(JsonSerializer.SerializeToUtf8Bytes(root.GetProperty("library"))), Present(root, "present"));
        }
        catch (Exception error) when (error is KeyNotFoundException or InvalidOperationException or ContractException)
        {
            throw new Audio2FaceHostException("response.invalid", "The host's copy of the character list was invalid.");
        }
    }

    private static HashSet<string> Present(JsonElement root, string property)
    {
        try
        {
            return root.GetProperty(property).EnumerateArray().Select(item => item.GetString())
                .Where(CharacterModelLibrary.IsSha256).Select(item => item!).ToHashSet(StringComparer.Ordinal);
        }
        catch (Exception error) when (error is KeyNotFoundException or InvalidOperationException)
        {
            throw new Audio2FaceHostException("response.invalid", "The host's list of character pieces was invalid.");
        }
    }
}
