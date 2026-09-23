using Martlet.Avatars;
using Martlet.Core.Contracts;
using NvidiaAce.Controller.V1;

namespace Martlet.Avatar.Audio2Face;

internal sealed class AnimationDecoder(GeneratedSpeechClip clip, Audio2FaceOptions options)
{
    private static readonly IReadOnlyDictionary<string, string> Names = AvatarChannels.BlendshapeNames
        .ToDictionary(name => char.ToUpperInvariant(name[0]) + name[1..], StringComparer.Ordinal);
    private static readonly HashSet<string> ExcludedNames = new(StringComparer.Ordinal)
    {
        "HeadRoll", "HeadPitch", "HeadYaw",
        "TongueTipUp", "TongueTipDown", "TongueTipLeft", "TongueTipRight",
        "TongueRollUp", "TongueRollDown", "TongueRollLeft", "TongueRollRight",
        "TongueUp", "TongueDown", "TongueLeft", "TongueRight",
        "TongueIn", "TongueStretch", "TongueWide", "TongueNarrow"
    };
    private string?[]? names;
    private double lastTime = -1;
    private long lastOffset = -1;
    private int frameCount;
    private int messageCount;
    private long responseBytes;
    private bool lastWasSuccess;

    public IReadOnlyList<AvatarFrame> Decode(AnimationDataStream message)
    {
        if (++messageCount > 20_000 ||
            (responseBytes += message.CalculateSize()) > options.MaxResponseBytes)
            throw new Audio2FaceException(Audio2FaceFailure.LimitExceeded);
        lastWasSuccess = false;
        switch (message.StreamPartCase)
        {
            case AnimationDataStream.StreamPartOneofCase.AnimationDataStreamHeader:
                if (names is not null || message.AnimationDataStreamHeader.SkelAnimationHeader is not { } header ||
                    header.BlendShapes.Count is < 1 or > 128)
                    throw Invalid();
                var seen = new HashSet<string>(StringComparer.Ordinal);
                names = header.BlendShapes.Select(name =>
                {
                    if (!seen.Add(name)) throw Invalid();
                    if (Names.TryGetValue(name, out var canonical)) return canonical;
                    if (ExcludedNames.Contains(name)) return null;
                    throw Invalid();
                }).ToArray();
                if (names.All(name => name is null)) throw Invalid();
                return [];
            case AnimationDataStream.StreamPartOneofCase.Status:
                if (message.Status.Code is NvidiaAce.Status.V1.Status.Types.Code.Error or
                    NvidiaAce.Status.V1.Status.Types.Code.Warning)
                    throw new Audio2FaceException(Audio2FaceFailure.UpstreamFailure);
                if (names is null) throw Invalid();
                if (message.Status.Code == NvidiaAce.Status.V1.Status.Types.Code.Success)
                    lastWasSuccess = true;
                else if (message.Status.Code != NvidiaAce.Status.V1.Status.Types.Code.Info)
                    throw Invalid();
                return [];
            case AnimationDataStream.StreamPartOneofCase.Event:
                if (names is null ||
                    message.Event.EventType != EventType.EndOfA2FAudioProcessing) throw Invalid();
                return [];
            case AnimationDataStream.StreamPartOneofCase.AnimationData:
                if (names is null) throw Invalid();
                // Echoed audio, emotion metadata, camera and joint tracks are not speaker/renderer inputs.
                if (message.AnimationData.SkelAnimation is not { } animation) return [];
                if (animation.BlendShapeWeights.Count > options.MaxOutputFrames - frameCount)
                    throw new Audio2FaceException(Audio2FaceFailure.LimitExceeded);
                var result = new List<AvatarFrame>(animation.BlendShapeWeights.Count);
                foreach (var weights in animation.BlendShapeWeights)
                {
                    if (!double.IsFinite(weights.TimeCode) || weights.TimeCode < 0 ||
                        weights.TimeCode <= lastTime || weights.Values.Count != names.Length ||
                        weights.TimeCode > (double)clip.SampleCount / clip.SampleRate)
                        throw Invalid();
                    var relative = (long)Math.Floor(weights.TimeCode * clip.SampleRate);
                    var offset = checked(clip.SampleOffset + relative);
                    if (relative > clip.SampleCount || offset <= lastOffset) throw Invalid();
                    var blendshapes = new Dictionary<string, double>(StringComparer.Ordinal);
                    for (var i = 0; i < names.Length; i++)
                    {
                        var value = weights.Values[i];
                        if (!float.IsFinite(value)) throw Invalid();
                        if (names[i] is { } name)
                        {
                            if (value is < 0 or > 1) throw Invalid();
                            blendshapes.Add(name, value);
                        }
                    }
                    var frame = new AvatarFrame
                    {
                        Version = ContractVersion.Current, Ids = clip.Ids, SourceId = Audio2FaceAdapter.SourceId,
                        Epoch = clip.Epoch, Sequence = frameCount++, SampleRate = clip.SampleRate,
                        SampleOffset = offset, Blendshapes = blendshapes, Semantics = []
                    };
                    frame.Validate();
                    result.Add(frame);
                    lastTime = weights.TimeCode;
                    lastOffset = offset;
                }
                return result;
            default:
                throw Invalid();
        }
    }

    public void Complete()
    {
        if (names is null || frameCount == 0 || !lastWasSuccess) throw Invalid();
    }

    private static Audio2FaceException Invalid() => new(Audio2FaceFailure.InvalidProtocol);
}
