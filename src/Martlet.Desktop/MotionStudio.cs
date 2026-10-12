using Martlet.Conversation;
using Martlet.Core.Motion;

namespace Martlet.Desktop;

/// <summary>The motion computer for one piece of work, as <see cref="CharacterMotions"/> uses it: Martlet's <c>motion</c> host
/// role on a paired computer, or the FIXTURE - NOT AI maker (<see cref="MotionClient"/>, docs/MOTION_HOST.md).</summary>
internal sealed class MotionStudio(IMotionMaker maker, string? hostId) : IMotionStudio
{
    private int handedOff;

    /// <summary>The motion computer the shared motion choice names, or null while motions aren't set up.</summary>
    internal static IMotionStudio? For(string dataDirectory) =>
        MotionClient.For(dataDirectory) is { } maker ? new MotionStudio(maker, (maker as HostMotionMaker)?.HostId) : null;

    public string Where => maker.Where;
    public string? HostId => hostId;

    public async Task<string?> ProblemAsync(CancellationToken token)
    {
        var availability = await maker.GetAvailabilityAsync(token).ConfigureAwait(false);
        return availability.Available ? null : availability.Reason ?? "the motion computer isn't ready";
    }

    public async Task<MotionMade> MakeAsync(MotionArguments arguments, long seed, IProgress<string>? progress, CancellationToken token)
    {
        ArgumentNullException.ThrowIfNull(arguments);
        try
        {
            var made = await maker.GenerateAsync(new MotionRequest { Prompt = arguments.Motion ?? arguments.Name, Seconds = arguments.Seconds, Seed = seed }, token)
                .ConfigureAwait(false);
            return new(made.Vrma, made.Seconds > 0 ? made.Seconds : arguments.Seconds, made.Seed ?? seed, made.Model is { Length: > 0 } model ? model : made.Engine,
                made.Where is { Length: > 0 } where ? where : maker.Where, made.Fixture, made.Took);
        }
        catch (MotionException error) { throw new MotionStudioException(MotionClient.Problem(error), error); }
    }

    /// <summary>Hands the maker to <see cref="MotionClient.FreeLater"/>, which frees the motion computer's graphics card once no
    /// motion follows for a while, and disposes it.</summary>
    public void FreeLater()
    {
        if (Interlocked.Exchange(ref handedOff, 1) == 0) MotionClient.FreeLater(maker);
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref handedOff, 1) == 0) (maker as IDisposable)?.Dispose();
    }
}
