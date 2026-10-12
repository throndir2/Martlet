using System.Security.Cryptography;
using System.Text.Json.Nodes;

namespace Martlet.Core.Motion;

/// <summary>Reads the Motion role's answers (gateway route <c>martlet.gateway.motion.v1</c>): a failure object
/// (<c>{"error": {"code", "summary"}}</c>), and a generate's VRMA pieces (<c>media_type</c>, <c>total_bytes</c>, <c>offset</c>,
/// <c>bytes</c>, <c>data_base64</c>) followed by its summary (<c>state</c> "done", <c>model</c>, <c>frames</c>, <c>fps</c>,
/// <c>seed</c>, <c>milliseconds</c>, <c>load_milliseconds</c>, <c>total_bytes</c>, <c>sha256</c>).</summary>
public static class MotionAnswers
{
    /// <summary>Throws the <see cref="MotionException"/> an answer reports, if it reports one.</summary>
    public static void ThrowIfError(JsonObject answer, string where)
    {
        ArgumentNullException.ThrowIfNull(answer);
        if (answer["error"] is not JsonObject error) return;
        var code = error["code"]?.ToString();
        throw new MotionException(code switch
        {
            "motion.busy" => MotionErrorCodes.Busy,
            "motion.out_of_memory" => MotionErrorCodes.OutOfMemory,
            "request.invalid" => MotionErrorCodes.RequestInvalid,
            "motion.unavailable" => MotionErrorCodes.Unavailable,
            _ => MotionErrorCodes.Failed
        }, $"{where}: {error["summary"]?.ToString() ?? code ?? "it failed"}");
    }

    /// <summary>The motion a generate's answers carry: the pieces joined in order, checked against the summary's size and
    /// SHA-256 and as VRMA. Throws <see cref="MotionException"/> for a failure or an invalid answer.</summary>
    public static MotionResult Generated(IReadOnlyList<JsonObject> answers, string where, TimeSpan took)
    {
        ArgumentNullException.ThrowIfNull(answers);
        var invalid = new MotionException(MotionErrorCodes.Failed, $"{where} returned an invalid answer.");
        using var clip = new MemoryStream();
        JsonObject? summary = null;
        try
        {
            foreach (var item in answers)
            {
                ThrowIfError(item, where);
                if (item["data_base64"] is not null)
                {
                    var total = item["total_bytes"]?.GetValue<long>() ?? 0;
                    if (summary is not null || item["offset"]?.GetValue<long>() != clip.Length || total is <= 0 or > MotionFiles.MaximumBytes)
                        throw invalid;
                    var data = Convert.FromBase64String(item["data_base64"]!.ToString());
                    if (clip.Length + data.Length > total) throw invalid;
                    clip.Write(data);
                }
                else if (item["state"]?.ToString() == "done") summary = item;
            }
            if (summary is null || summary["total_bytes"]?.GetValue<long>() != clip.Length) throw invalid;
            var vrma = clip.ToArray();
            if (summary["sha256"]?.ToString() != Convert.ToHexStringLower(SHA256.HashData(vrma))) throw invalid;
            MotionFiles.Require(vrma, where);
            var frames = (int)(summary["frames"]?.GetValue<long>() ?? 0);
            var fps = (int)(summary["fps"]?.GetValue<long>() ?? 30);
            return new()
            {
                Vrma = vrma, Frames = frames, Fps = fps, Seconds = fps > 0 ? frames / (double)fps : 0,
                Seed = summary["seed"]?.GetValue<long>(), Engine = "hy-motion", Model = summary["model"]?.ToString() ?? "",
                Where = where, Took = took
            };
        }
        catch (Exception error) when (error is FormatException or InvalidOperationException or OverflowException)
        {
            throw new MotionException(MotionErrorCodes.Failed, $"{where} returned an invalid answer.", error);
        }
    }
}
