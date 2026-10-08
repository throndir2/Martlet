using System.IO;
using System.Net.Http;
using System.Text.Json;
using System.Text.Json.Nodes;
using Martlet.Avatar.Audio2Face.Remote;
using Martlet.Avatar.Hosting;
using Martlet.Core.Pictures;
using Martlet.Core.Settings;
using Martlet.Credentials.Windows;
using Martlet.Providers.Pictures;

namespace Martlet.Desktop;

/// <summary>
/// The picture maker Martlet's conversation uses (docs/PICTURES.md), from Companion › Pictures on this PC
/// (<see cref="PicturesSettings"/>): ComfyUI on a paired computer's <c>pictures</c> role through its gateway
/// (<see cref="GatewayComfyApi"/>), a ComfyUI the owner runs at an address, OpenRouter or NVIDIA Build (the key from Windows
/// Credential Manager for each picture: Pictures' own, or Thinking's for the same provider). Setting
/// <c>MARTLET_PICTURES_FIXTURE=1</c> before Martlet starts makes it use the FIXTURE - NOT AI <see cref="FixturePictureMaker"/>.
/// </summary>
internal static class PictureClient
{
    internal const string FixtureVariable = "MARTLET_PICTURES_FIXTURE";
    /// <summary>How long Martlet's pictures role keeps its model loaded after the last picture before it frees the graphics card.</summary>
    internal static readonly TimeSpan FreeAfter = TimeSpan.FromMinutes(3);
    private static readonly object Gate = new();
    private static (string Directory, DateTime Written, PicturesSettings Settings)? cached;
    private static CancellationTokenSource? freeing;

    internal static bool Fixture => Environment.GetEnvironmentVariable(FixtureVariable) == "1";

    /// <summary>This PC's choice, read again only when pictures.json changed (cheap enough for every reply).</summary>
    internal static PicturesSettings Settings(string dataDirectory)
    {
        var path = Path.Combine(dataDirectory, PicturesSettings.FileName);
        DateTime written;
        try { written = File.Exists(path) ? File.GetLastWriteTimeUtc(path) : DateTime.MinValue; }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException) { written = DateTime.MinValue; }
        lock (Gate)
        {
            if (cached is { } known && known.Directory == dataDirectory && known.Written == written) return known.Settings;
            var settings = PicturesSettings.Load(dataDirectory);
            cached = (dataDirectory, written, settings);
            return settings;
        }
    }

    /// <summary>Whether draw_picture is offered: the fixture is on, or pictures are set up somewhere.</summary>
    internal static bool IsSetUp(string dataDirectory) => Fixture || Settings(dataDirectory).On;

    /// <summary>The picture maker for this PC's choice, or null while pictures are off. <paramref name="thinking"/> is Thinking's
    /// route, whose key a cloud provider borrows when it has none of its own.</summary>
    internal static IPictureMaker? For(string dataDirectory, Guid profile, SetupRoute? thinking)
    {
        if (Fixture) return new FixturePictureMaker(TimeSpan.FromMilliseconds(300));
        var settings = Settings(dataDirectory);
        var custom = settings.Workflow == PictureWorkflow.Custom ? PicturesSettings.LoadWorkflow(dataDirectory) : null;
        return settings.Place switch
        {
            PicturePlace.ComfyUi => new ComfyPictureMaker(new ComfyHttpApi(settings.Address!), settings.Workflow, settings.Checkpoint, custom),
            PicturePlace.Host => new ComfyPictureMaker(new GatewayComfyApi(dataDirectory, settings.HostId), settings.Workflow, settings.Checkpoint, custom),
            PicturePlace.OpenRouter => new OpenRouterPictureMaker(settings.ModelId, token => KeyAsync(settings, profile, thinking, token)),
            PicturePlace.NvidiaBuild => new NvidiaPictureMaker(settings.ModelId, token => KeyAsync(settings, profile, thinking, token)),
            _ => null
        };
    }

    /// <summary>The cloud provider's key: Pictures' own, or Thinking's for the same provider; null when there's none.</summary>
    internal static Task<string?> KeyAsync(PicturesSettings settings, Guid profile, SetupRoute? thinking, CancellationToken token)
    {
        if (profile == Guid.Empty) return Task.FromResult<string?>(null);
        var scope = settings.CredentialId is { } own ? settings.Binding(profile, own)
            : settings.UsesThinkingKey(thinking) ? CredentialBinding.For(profile, thinking!, thinking!.CredentialId!.Value)
            : null;
        if (scope is null) return Task.FromResult<string?>(null);
        return Task.Run(() =>
        {
            using var result = new WindowsCredentialStore().Read(scope);
            if (result.Error != CredentialError.None || result.Secret is null) return null;
            string? key = null;
            result.Secret.Use(secret => key = new string(secret));
            return key;
        }, token);
    }

    /// <summary>After a picture on Martlet's pictures role: frees its graphics card once <see cref="FreeAfter"/> passes without
    /// another picture, so the voice, listening and a local Thinking model get it back.</summary>
    internal static void FreeLater(IPictureMaker maker)
    {
        if (maker is not ComfyPictureMaker { Api: GatewayComfyApi api }) return;
        CancellationTokenSource next = new();
        lock (Gate)
        {
            freeing?.Cancel();
            freeing = next;
        }
        Task.Run(async () =>
        {
            try
            {
                await Task.Delay(FreeAfter, next.Token).ConfigureAwait(false);
                await api.FreeAsync(next.Token).ConfigureAwait(false);
                ErrorLog.Info($"Pictures: freed the graphics card on {api.Where} after {FreeAfter.TotalMinutes:0} idle minutes.");
            }
            catch (OperationCanceledException) { }
            catch (Exception error) when (error is PictureException or HttpRequestException or IOException)
            {
                ErrorLog.Info($"Pictures: couldn't free the graphics card on {api.Where} ({error.Message}).");
            }
            finally { api.Dispose(); }
        }).Forget();
    }

    /// <summary>Why a picture failed, in a few plain words for the conversation.</summary>
    internal static string Problem(PictureException error) => error.Code switch
    {
        PictureErrorCodes.Unavailable => error.Message.TrimEnd('.'),
        PictureErrorCodes.Busy => "the picture computer is busy",
        PictureErrorCodes.NotAuthorized => "the picture provider refused the API key",
        PictureErrorCodes.Refused => "the picture provider's content filter refused it",
        PictureErrorCodes.TimedOut => "drawing it took too long",
        PictureErrorCodes.RequestInvalid => error.Message.TrimEnd('.'),
        _ => error.Message.TrimEnd('.')
    };
}

/// <summary>
/// ComfyUI on a paired computer's <c>pictures</c> host role, through its gateway (route <c>martlet.gateway.picture.v1</c>,
/// whose operations are <see cref="IComfyApi"/>'s). The computer named in Companion › Pictures draws; without one, the first
/// paired computer that offers the route. Pages of the picture come back as base64 events.
/// </summary>
internal sealed class GatewayComfyApi(string dataDirectory, string? hostId) : IComfyApi, IDisposable
{
    private const int PageBytes = 2 * 1024 * 1024;
    private (PairedHost Host, HostRoute Route, Audio2FaceHostConnection Connection)? open;

    public string Where => open is { } found ? $"{found.Host.HostId}'s Pictures role" : hostId is null ? "Martlet's Pictures role" : $"{hostId}'s Pictures role";

    public async Task<JsonObject> StatusAsync(CancellationToken cancellationToken) =>
        Single(await CallAsync(new() { ["operation"] = "status" }, cancellationToken).ConfigureAwait(false));

    public async Task<string> QueueAsync(JsonObject workflow, CancellationToken cancellationToken)
    {
        var answer = Single(await CallAsync(new() { ["operation"] = "prompt", ["prompt"] = workflow }, cancellationToken).ConfigureAwait(false));
        if (answer["prompt_id"] is JsonValue id && id.ToString() is { Length: > 0 and <= 128 } text) return text;
        throw new PictureException(PictureErrorCodes.RequestInvalid, $"{Where} rejected the workflow: {ComfyErrors.Describe(answer)}");
    }

    public async Task<JsonObject?> HistoryAsync(string promptId, CancellationToken cancellationToken)
    {
        var answer = Single(await CallAsync(new() { ["operation"] = "history", ["prompt_id"] = promptId }, cancellationToken).ConfigureAwait(false));
        return answer[promptId] as JsonObject ?? (answer.ContainsKey("outputs") ? answer : null);
    }

    public async Task<JsonObject> QueueStateAsync(CancellationToken cancellationToken) =>
        Single(await CallAsync(new() { ["operation"] = "queue" }, cancellationToken).ConfigureAwait(false));

    public async Task<byte[]> ViewAsync(string filename, string subfolder, string type, CancellationToken cancellationToken)
    {
        using var buffer = new MemoryStream();
        long total = -1;
        while (total < 0 || buffer.Length < total)
        {
            var pages = await CallAsync(new()
            {
                ["operation"] = "view", ["filename"] = filename, ["subfolder"] = subfolder, ["type"] = type,
                ["offset"] = buffer.Length, ["maximum"] = PageBytes
            }, cancellationToken).ConfigureAwait(false);
            var before = buffer.Length;
            foreach (var page in pages)
            {
                Fail(page);
                total = page["total_bytes"]?.GetValue<long>() ?? throw Invalid();
                if (page["offset"]?.GetValue<long>() != buffer.Length || total is < 0 or > PictureImages.MaximumBytes) throw Invalid();
                var data = Convert.FromBase64String(page["data_base64"]?.ToString() ?? "");
                if (buffer.Length + data.Length > total) throw Invalid();
                buffer.Write(data);
            }
            if (buffer.Length == before && buffer.Length < total) throw Invalid();
        }
        return buffer.ToArray();
    }

    public async Task CancelAsync(string promptId, CancellationToken cancellationToken) =>
        await CallAsync(new() { ["operation"] = "cancel", ["prompt_id"] = promptId }, cancellationToken).ConfigureAwait(false);

    public async Task FreeAsync(CancellationToken cancellationToken) =>
        await CallAsync(new() { ["operation"] = "free" }, cancellationToken).ConfigureAwait(false);

    private async Task<IReadOnlyList<JsonObject>> CallAsync(Dictionary<string, object> payload, CancellationToken token)
    {
        for (var attempt = 0; ; attempt++)
        {
            var (host, route, connection) = await OpenAsync(token).ConfigureAwait(false);
            try
            {
                var answer = await connection.PictureOperationAsync(route, payload, token).ConfigureAwait(false);
                return [.. answer.Select(element => JsonNode.Parse(element.GetRawText()) as JsonObject ?? throw Invalid())];
            }
            // The gateway runs one request per route at a time: another computer's poll can hold it for a moment.
            catch (Audio2FaceHostException error) when (error.Code == "job.busy" && attempt < 40)
            {
                await Task.Delay(TimeSpan.FromMilliseconds(250), token).ConfigureAwait(false);
            }
            catch (Audio2FaceHostException error)
            {
                throw new PictureException(error.Code switch
                {
                    "request.invalid" or "request.too_large" => PictureErrorCodes.RequestInvalid,
                    "job.busy" => PictureErrorCodes.Busy,
                    _ => PictureErrorCodes.Unavailable
                }, $"{host.HostId}'s Pictures role: {error.Message}", error);
            }
            catch (Exception error) when (error is HttpRequestException or IOException)
            {
                Dispose();
                throw new PictureException(PictureErrorCodes.Unavailable, $"{host.HostId} stopped answering ({error.Message}).", error);
            }
        }
    }

    private async Task<(PairedHost, HostRoute, Audio2FaceHostConnection)> OpenAsync(CancellationToken token)
    {
        if (open is { } known) return known;
        IReadOnlyList<PairedHost> hosts;
        try { hosts = HostRegistry.Load(dataDirectory); }
        catch (Exception error) when (error is InvalidDataException or IOException or UnauthorizedAccessException)
        {
            throw new PictureException(PictureErrorCodes.Unavailable, error.Message, error);
        }
        // Pictures stay the owner's on a host a friend shares (it refuses them), so those hosts are skipped.
        foreach (var host in hosts.Where(h => !h.Shared && (hostId is null || h.HostId == hostId)))
        {
            Audio2FaceHostConnection? connection = null;
            try
            {
                connection = ClusterSync.Connect(host.Pairing);
                var routes = await connection.ReadRoutesAsync(token).ConfigureAwait(false);
                if (routes.FirstOrDefault(r => r.RouteId == Audio2FaceHostConnection.PictureRouteId) is { } route)
                {
                    open = (host, route, connection);
                    connection = null;
                    return open.Value;
                }
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested) { throw; }
            catch (Exception error) when (ClusterSync.IsHostFailure(error))
            {
                if (hostId is not null) throw new PictureException(PictureErrorCodes.Unavailable, $"{hostId} isn't reachable ({error.Message}).", error);
            }
            finally { connection?.Dispose(); }
        }
        throw new PictureException(PictureErrorCodes.Unavailable, hostId is null
            ? "No paired computer runs the Pictures role. Set it up in Companion › Pictures."
            : $"{hostId} doesn't run the Pictures role. Set it up there in Companion › Pictures.");
    }

    private JsonObject Single(IReadOnlyList<JsonObject> answer)
    {
        if (answer.Count != 1) throw Invalid();
        Fail(answer[0], allowPrompt: true);
        return answer[0];
    }

    private void Fail(JsonObject answer, bool allowPrompt = false)
    {
        if (answer["error"] is not JsonObject error) return;
        var code = error["code"]?.ToString();
        if (allowPrompt && code == "prompt.invalid") return;
        throw new PictureException(code switch
        {
            "pictures.busy" => PictureErrorCodes.Busy,
            "request.invalid" => PictureErrorCodes.RequestInvalid,
            "pictures.unavailable" or "worker.unavailable" => PictureErrorCodes.Unavailable,
            _ => PictureErrorCodes.Failed
        }, $"{Where}: {error["summary"]?.ToString() ?? code ?? "it failed"}");
    }

    private PictureException Invalid() => new(PictureErrorCodes.Failed, $"{Where} returned an invalid answer.");

    public void Dispose()
    {
        open?.Connection.Dispose();
        open = null;
    }
}
