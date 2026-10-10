using System.IO;
using System.Net.Http;
using System.Text.Json;
using System.Text.Json.Nodes;
using Martlet.Avatar.Audio2Face.Remote;
using Martlet.Avatar.Hosting;
using Martlet.Core.Cluster;
using Martlet.Core.Contracts;
using Martlet.Core.Pictures;
using Martlet.Core.Settings;
using Martlet.Credentials.Windows;
using Martlet.Providers.Pictures;

namespace Martlet.Desktop;

/// <summary>
/// The picture maker Martlet's conversation uses (docs/PICTURES.md), from this PC's Pictures list (<see cref="PoolAreas.Pictures"/>,
/// pools-local.json, made once from Companion › Pictures' earlier choice, <see cref="PicturesSettings"/>): each member is ComfyUI
/// on Martlet's <c>pictures</c> role through a paired computer's gateway (<see cref="GatewayComfyApi"/>), a ComfyUI the owner
/// runs at an address, OpenRouter or NVIDIA Build (the key from Windows Credential Manager for each picture: the member's own,
/// or Thinking's for the same provider). With more than one, a picture goes to the first that is free (<see cref="PicturePool"/>).
/// Setting <c>MARTLET_PICTURES_FIXTURE=1</c> before Martlet starts makes it use the FIXTURE - NOT AI <see cref="FixturePictureMaker"/>.
/// </summary>
internal static class PictureClient
{
    internal const string FixtureVariable = "MARTLET_PICTURES_FIXTURE";
    /// <summary>How long Martlet's pictures role keeps its model loaded after the last picture before it frees the graphics card.</summary>
    internal static readonly TimeSpan FreeAfter = TimeSpan.FromMinutes(3);
    private static readonly object Gate = new();
    private static (string Directory, DateTime Written, PicturesSettings Settings)? cached;
    // The computers waiting to free their graphics card, by host ID (empty: the first paired computer that offers the route).
    private static readonly Dictionary<string, CancellationTokenSource> freeing = new(StringComparer.Ordinal);

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

    /// <summary>This PC's Pictures list (<see cref="PoolAreas.Pictures"/>, pools-local.json), from the pool lists' cache (read
    /// again only when the file changed, cheap enough for every reply). Without one yet, it is made from pictures.json
    /// (<see cref="Migrate"/>) and saved once it has a place; an empty one isn't saved, so a choice saved there later still
    /// counts.</summary>
    internal static PoolList List(string dataDirectory)
    {
        if (WorkSharingRoster.Pool(dataDirectory, PoolAreas.Pictures) is { } list) return list;
        lock (Gate)
        {
            if (PoolSettings.LoadFor(dataDirectory, PoolAreas.Pictures) is { } saved) return saved;
            list = Migrate(dataDirectory, Settings(dataDirectory));
            if (list.Members.Count > 0 && SaveList(dataDirectory, list))
                ErrorLog.Info($"Pictures: made the Pictures list from the earlier choice ({list.Members.Count} place{(list.Members.Count == 1 ? "" : "s")}).");
            return list;
        }
    }

    /// <summary>Saves this PC's Pictures list (never shared).</summary>
    internal static bool SaveList(string dataDirectory, PoolList list)
    {
        var saved = PoolSettings.SaveFor(dataDirectory, PoolAreas.Pictures, list);
        WorkSharingRoster.Forget();
        return saved;
    }

    /// <summary>The Pictures list for a one-place choice (<see cref="PicturePoolMembers.Migrate"/>): that place; for Martlet's
    /// pictures role, then the other paired computers the shared plan (cluster.json) says run it, this PC's own host service
    /// first and then the rest, fewest jobs first, never a host a friend shares (it keeps pictures for its owner) or one kept for
    /// another companion PC (Devices › Sharing work).</summary>
    internal static PoolList Migrate(string dataDirectory, PicturesSettings settings)
    {
        IReadOnlyList<string> others = [];
        if (settings.Place == PicturePlace.Host)
        {
            var hosts = Paired(dataDirectory);
            others = PicturePoolMembers.Others(ClusterSync.LoadPlan(dataDirectory), [.. hosts.Where(h => !h.Shared).Select(h => h.HostId)],
                OwnHost(hosts), settings.HostId, WorkSharingRoster.Settings(dataDirectory), WorkSharingRoster.Device);
        }
        return PicturePoolMembers.Migrate(settings, others, DateTimeOffset.UtcNow);
    }

    /// <summary>The members a picture from this PC tries now, first to last (<see cref="PoolRouting.Order"/>): on, kept for this
    /// PC or every one, agreed to (a cloud provider) and, for a paired computer, still paired and not a host a friend shares.</summary>
    internal static PoolOrder Order(string dataDirectory, bool paired = true)
    {
        var hosts = paired ? Paired(dataDirectory) : null;
        return PoolRouting.Order(PoolAreas.Pictures, List(dataDirectory), WorkSharingRoster.Device,
            hosts is null ? null : member => member.Kind != PoolMemberKind.Computer || hosts.Any(h => h.HostId == member.HostId && !h.Shared));
    }

    /// <summary>Whether draw_picture is offered: the fixture is on, or the Pictures list has a place that is on.</summary>
    internal static bool IsSetUp(string dataDirectory) => Fixture || Order(dataDirectory, paired: false).Members.Count > 0;

    /// <summary>The first cloud provider a picture may go to, in words ("OpenRouter (google/...)"), so a picture the owner asks for
    /// on a page can say first that it may cost money; null when no cloud provider in the list is on.</summary>
    internal static string? FirstPaid(string dataDirectory) =>
        Order(dataDirectory, paired: false).Members.FirstOrDefault(m => m.Kind == PoolMemberKind.Cloud) is { } paid ? PicturePoolMembers.Describe(paid) : null;

    /// <summary>A cloud member's own key on this PC as the place's settings (its credential ID in pool-keys.json); null without one.</summary>
    internal static PicturesSettings? CloudPlace(string dataDirectory, PoolMember member) =>
        member.Kind == PoolMemberKind.Cloud
            ? PicturePoolMembers.Place(member, null, PoolKeys.Load(dataDirectory).For(PoolAreas.Pictures.Id, member.Key))
            : null;

    /// <summary>The paired computer that draws first (the first member, when it is Martlet's pictures role), or null.</summary>
    internal static string? Painter(string dataDirectory) => Order(dataDirectory, paired: false).Members.FirstOrDefault() is { } first
        ? first.Kind == PoolMemberKind.ThisPc ? OwnHost(Paired(dataDirectory)) : first.Kind == PoolMemberKind.Computer ? first.HostId : null
        : null;

    /// <summary>The picture maker for this PC's Pictures list, or null while it is off. One place: that place's maker, as before.
    /// More: a <see cref="PicturePool"/> that draws on the first that is free. <paramref name="thinking"/> is Thinking's route,
    /// whose key a cloud provider borrows when it has none of its own.</summary>
    internal static IPictureMaker? For(string dataDirectory, Guid profile, SetupRoute? thinking)
    {
        if (Fixture) return new FixturePictureMaker(TimeSpan.FromMilliseconds(300));
        var order = Order(dataDirectory);
        if (order.Members.Count == 0) return null;
        var own = OwnHost(Paired(dataDirectory));
        var keys = PoolKeys.Load(dataDirectory);
        List<PicturePoolMember> made = [];
        foreach (var member in order.Members)
            if (PicturePoolMembers.Place(member, own, keys.For(PoolAreas.Pictures.Id, member.Key)) is { } place &&
                Maker(dataDirectory, member, place, profile, thinking) is { } maker)
                made.Add(new(member.Key, maker, place.Place == PicturePlace.Host ? place.HostId : null));
        return made.Count switch
        {
            0 => null,
            1 => made[0].Maker,
            _ => new PicturePool(made)
        };
    }

    // One member's picture maker, with its own workflow, checkpoint, model and key; null when its address isn't one.
    private static IPictureMaker? Maker(string dataDirectory, PoolMember member, PicturesSettings place, Guid profile, SetupRoute? thinking)
    {
        var custom = place.Workflow == PictureWorkflow.Custom
            ? PicturesSettings.LoadWorkflow(dataDirectory, PicturePoolMembers.WorkflowFile(member)) : null;
        try
        {
            return place.Place switch
            {
                PicturePlace.ComfyUi => new ComfyPictureMaker(new ComfyHttpApi(place.Address!), place.Workflow, place.Checkpoint, custom),
                PicturePlace.Host => new ComfyPictureMaker(new GatewayComfyApi(dataDirectory, place.HostId), place.Workflow, place.Checkpoint, custom),
                PicturePlace.OpenRouter => new OpenRouterPictureMaker(place.ModelId, token => KeyAsync(place, profile, thinking, token)),
                PicturePlace.NvidiaBuild => new NvidiaPictureMaker(place.ModelId, token => KeyAsync(place, profile, thinking, token)),
                _ => null
            };
        }
        catch (ContractException error)
        {
            ErrorLog.Info($"Pictures: skipped {member.Name} in the Pictures list ({error.Message}).");
            return null;
        }
    }

    private static IReadOnlyList<PairedHost> Paired(string dataDirectory)
    {
        try { return HostRegistry.Load(dataDirectory); }
        catch (Exception error) when (error is InvalidDataException or IOException or UnauthorizedAccessException) { return []; }
    }

    // This PC's own host service: the one Martlet runs here, else a pairing saved as this PC's own; null: the first paired
    // computer that offers the pictures role.
    private static string? OwnHost(IReadOnlyList<PairedHost> hosts) =>
        WorkSharingRoster.OwnHostId ?? hosts.FirstOrDefault(h => h.Method == HostSetupMethod.ThisPcDocker)?.HostId;

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
    /// <summary>After a picture on Martlet's pictures role: frees the graphics card of the computer that drew it once
    /// <see cref="FreeAfter"/> passes without another picture there, so the voice, listening and a local Thinking model get it
    /// back. Each computer has its own wait, so a picture on one never keeps another's card loaded.</summary>
    internal static void FreeLater(IPictureMaker maker)
    {
        if (maker is PicturePool { Route: { } route, Chosen: { } chosen })
        {
            if (route.QueuedBehind is { } ahead)
                ErrorLog.Info($"Pictures: every place in the Pictures list was busy, so the picture waited behind {(ahead == int.MaxValue ? "others" : ahead)} on {chosen.Maker.Where}.");
            else if (route.Position > 0 || route.Busy > 0 || route.Unavailable > 0)
                ErrorLog.Info($"Pictures: drawn on {chosen.Maker.Where}, place {route.Position + 1} in the Pictures list ({route.Busy} busy, " +
                    $"{route.Unavailable} couldn't draw it first).");
            maker = chosen.Maker;
        }
        if (maker is not ComfyPictureMaker { Api: GatewayComfyApi api }) return;
        var host = api.HostId ?? "";
        CancellationTokenSource next = new();
        lock (Gate)
        {
            if (freeing.Remove(host, out var earlier)) earlier.Cancel();
            freeing[host] = next;
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
            finally
            {
                lock (Gate)
                    if (freeing.TryGetValue(host, out var current) && current == next) freeing.Remove(host);
                api.Dispose();
            }
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

    /// <summary>The computer this draws on: the one it opened, else the one it was made for (null: the first that offers it).</summary>
    public string? HostId => open?.Host.HostId ?? hostId;

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
