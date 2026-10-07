using System.Text.Json;

namespace Martlet.Avatar.Audio2Face.Remote;

public sealed partial class Audio2FaceHostConnection
{
    public const string OcrRouteId = "martlet.gateway.ocr.v1";
    public const string OcrPath = "/martlet/v1/inference/ocr";
    /// <summary>The largest screenshot the host's Reading role takes (JPEG or PNG bytes).</summary>
    public const int OcrMaximumImageBytes = 4 * 1024 * 1024;

    /// <summary>Asks the host's Reading role (route <c>martlet.gateway.ocr.v1</c>) whether it is ready: one JSON object with
    /// <c>state</c> ("ready" or "loading"). Gateway and transport failures throw <see cref="Audio2FaceHostException"/>.</summary>
    public async Task<JsonElement> OcrStatusAsync(HostRoute route, CancellationToken cancellationToken = default) =>
        OneOcrAnswer(await OcrOperationAsync(route, new Dictionary<string, object> { ["operation"] = "status" }, cancellationToken)
            .ConfigureAwait(false));

    /// <summary>Reads the text in one screenshot (<paramref name="mediaType"/> image/jpeg or image/png) on the host's Reading role:
    /// one JSON object with <c>lines</c> (each <c>text</c>, <c>score</c> and <c>box</c> [x, y, width, height] in the picture's
    /// pixels) and <c>milliseconds</c>. The picture is read in memory there and not kept.</summary>
    public async Task<JsonElement> OcrReadAsync(HostRoute route, byte[] image, string mediaType, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(image);
        if (image.Length is 0 or > OcrMaximumImageBytes) throw new ArgumentException("The screenshot is empty or too large.", nameof(image));
        if (mediaType is not ("image/jpeg" or "image/png")) throw new ArgumentException("Only JPEG and PNG can be read.", nameof(mediaType));
        return OneOcrAnswer(await OcrOperationAsync(route, new Dictionary<string, object>
        {
            ["operation"] = "read", ["media_type"] = mediaType, ["image_base64"] = Convert.ToBase64String(image)
        }, cancellationToken).ConfigureAwait(false));
    }

    private Task<IReadOnlyList<JsonElement>> OcrOperationAsync(HostRoute route, IReadOnlyDictionary<string, object> payload,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(route);
        if (route.RouteId != OcrRouteId || route.Path != OcrPath)
            throw new ArgumentException("The route is not the host's Reading role.", nameof(route));
        return JsonOperationAsync(route, payload, TimeSpan.FromSeconds(15), "reading", cancellationToken);
    }

    private static JsonElement OneOcrAnswer(IReadOnlyList<JsonElement> answer) => answer.Count == 1
        ? answer[0]
        : throw new Audio2FaceHostException("stream.invalid", "The host's reading stream was invalid.");
}
