using Martlet.Core.Audio;
using Martlet.Core.Contracts;

namespace Martlet.Conversation;

internal static class PlaybackFrameMapping
{
    internal static PcmFrame Map(PcmFrame frame, CorrelationIds ids, long turnEpoch, long playbackEpoch)
    {
        if (frame.Epoch != turnEpoch || frame.Ids != ids)
            throw new ConversationException(ConversationFailure.InvalidStream);
        return new(frame.Ids, playbackEpoch, frame.Sequence, frame.SampleOffset, frame.Format, frame.Data.Span);
    }
}
