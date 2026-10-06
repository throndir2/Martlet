using System.Diagnostics;
using System.Security.Cryptography;
using System.Text.Json.Nodes;
using Martlet.Core.Pictures;

namespace Martlet.Providers.Pictures;

/// <summary>Draws pictures with ComfyUI (<see cref="IComfyApi"/>): checks the workflow's model files are there, queues the
/// workflow, follows it through the queue to its finished picture and fetches it. Cancelling takes it off ComfyUI's queue or
/// interrupts it.</summary>
public sealed class ComfyPictureMaker(IComfyApi api, PictureWorkflow workflow, string? checkpoint = null, JsonObject? custom = null,
    TimeSpan? poll = null, TimeSpan? limit = null) : IPictureMaker, IDisposable
{
    public const string Engine = "comfyui";
    private readonly TimeSpan interval = poll ?? TimeSpan.FromMilliseconds(500);
    private readonly TimeSpan timeLimit = limit ?? TimeSpan.FromMinutes(10);

    public IComfyApi Api => api;
    public string Where => api.Where;

    public void Dispose() => (api as IDisposable)?.Dispose();

    public string Model => workflow switch
    {
        PictureWorkflow.ZImageTurbo => "Z-Image Turbo",
        PictureWorkflow.Checkpoint => checkpoint ?? "a checkpoint",
        _ => "your workflow"
    };

    public async Task<PictureMakerAvailability> GetAvailabilityAsync(CancellationToken cancellationToken)
    {
        JsonObject status;
        try { status = await api.StatusAsync(cancellationToken).ConfigureAwait(false); }
        catch (PictureException error) { return PictureMakerAvailability.Unavailable(error.Message); }
        var state = status["state"]?.ToString();
        if (state is "not_provisioned") return PictureMakerAvailability.Unavailable($"{Where} hasn't finished downloading its picture model.");
        if (state is not "ready") return PictureMakerAvailability.Unavailable($"{Where} is still starting.");
        if (workflow == PictureWorkflow.Custom && custom is null) return PictureMakerAvailability.Unavailable("Load your ComfyUI workflow in Companion › Pictures.");
        if (workflow == PictureWorkflow.Checkpoint && checkpoint is null) return PictureMakerAvailability.Unavailable("Choose a checkpoint in Companion › Pictures.");
        if (Missing(status) is { } missing) return PictureMakerAvailability.Unavailable(missing);
        return new(true, null, Where);
    }

    /// <summary>What the workflow needs that ComfyUI doesn't list, in words, or null (a ComfyUI that lists no folders passes).</summary>
    public string? Missing(JsonObject status)
    {
        var lacking = new List<string>();
        foreach (var (folder, file) in ComfyWorkflows.Needs(workflow, checkpoint))
            if (status["models"]?[folder] is JsonArray files && !files.Any(f => string.Equals(f?.ToString(), file, StringComparison.Ordinal)))
                lacking.Add($"{folder}/{file}");
        return lacking.Count == 0 ? null : $"{Where} doesn't have {string.Join(", ", lacking)}" +
            (workflow == PictureWorkflow.ZImageTurbo
                ? " (Z-Image Turbo's files from huggingface.co/Comfy-Org/z_image_turbo). Add them there, or choose a checkpoint it has."
                : ". Choose one it has.");
    }

    public async Task<PictureResult> GenerateAsync(PictureRequest request, IProgress<PictureProgress>? progress, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        request.Validate();
        var clock = Stopwatch.StartNew();
        var seed = request.Seed ?? RandomNumberGenerator.GetInt32(int.MaxValue);
        var graph = ComfyWorkflows.Build(workflow, request, seed, checkpoint, custom);
        progress?.Report(new(PictureProgress.Queued));
        PictureProgress? last = null;
        var id = await api.QueueAsync(graph, cancellationToken).ConfigureAwait(false);
        JsonObject? done = null;
        try
        {
            while (true)
            {
                cancellationToken.ThrowIfCancellationRequested();
                done = await api.HistoryAsync(id, cancellationToken).ConfigureAwait(false);
                if (done is not null && (done["outputs"] is JsonObject { Count: > 0 } || ComfyErrors.Failure(done) is not null ||
                    done["status"]?["completed"]?.ToString() == "true"))
                    break;
                if (clock.Elapsed > timeLimit)
                    throw new PictureException(PictureErrorCodes.TimedOut, $"{Where} took longer than {timeLimit.TotalMinutes:0} minutes.");
                var queue = await api.QueueStateAsync(cancellationToken).ConfigureAwait(false);
                var pending = ComfyErrors.Pending(queue).ToList();
                var position = pending.IndexOf(id);
                var update = position < 0
                    ? new PictureProgress(PictureProgress.Drawing)
                    : new PictureProgress(PictureProgress.Queued, 0, position + ComfyErrors.Running(queue).Count);
                if (update != last) progress?.Report(last = update);
                await Task.Delay(interval, cancellationToken).ConfigureAwait(false);
            }
        }
        catch (Exception) when (cancellationToken.IsCancellationRequested || done is null)
        {
            using var cancel = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            try { await api.CancelAsync(id, cancel.Token).ConfigureAwait(false); }
            catch (Exception error) when (error is PictureException or OperationCanceledException or HttpRequestException) { }
            throw;
        }
        if (ComfyErrors.Failure(done) is { } failure)
            throw new PictureException(PictureErrorCodes.Failed, $"{Where} couldn't draw it ({failure}).");
        if (ComfyErrors.Picture(done) is not { } file)
            throw new PictureException(PictureErrorCodes.Failed, $"{Where} finished without saving a picture (the workflow needs a Save Image node).");
        progress?.Report(new(PictureProgress.Fetching, 0.95));
        var bytes = await api.ViewAsync(file.Filename, file.Subfolder, file.Type, cancellationToken).ConfigureAwait(false);
        var (mediaType, width, height) = PictureImages.Require(bytes, Where);
        return new()
        {
            Image = bytes, MediaType = mediaType, Width = width, Height = height, Seed = seed, Engine = Engine, Model = Model,
            Where = Where, Took = clock.Elapsed
        };
    }
}
