using System.IO;
using Martlet.Avatar.Hosting;
using Martlet.Core.Contracts;

namespace Martlet.Desktop;

/// <summary>Touch zones' Reset (Companion › Touch, <see cref="CharacterTouchReset"/>) for the loaded model only.</summary>
internal sealed partial class CharacterTouchZoneService
{
    /// <summary>Every zone of the loaded model reacts again as a fresh zone does (<paramref name="fresh"/>): its reaction list,
    /// rest, Martlet notices and own words. The zones and their boxes stay. Returns why it couldn't be saved, or null.</summary>
    internal async Task<string?> ResetReactionsAsync(Func<CharacterTouchZone, CharacterTouchReaction> fresh, CancellationToken token)
    {
        if (Busy) return "Martlet is placing or finding the zones now. Wait until it is done, or press Stop.";
        if (Current is not { } settings) return null;
        ClearStreaks();
        return await SaveAsync(CharacterTouchZones.WithFreshReactions(settings, fresh), token);
    }

    /// <summary>Forgets the loaded model's zones (found and added), their picture and what the last detection sent, so the model
    /// is like a fresh character's: the Touch zones page places a first guess again. Other models stay. Returns why it couldn't,
    /// or null.</summary>
    internal async Task<string?> ForgetAsync(CancellationToken token)
    {
        if (Busy) return "Martlet is placing or finding the zones now. Wait until it is done, or press Stop.";
        if (dataDirectory is null) return "Martlet's data folder isn't available.";
        if (ModelId is not { } id) return "No model is shown.";
        try { await CharacterTouchZones.RemoveAsync(dataDirectory, id, token); }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or ContractException) { return error.Message; }
        if (ModelId == id)
        {
            Volatile.Write(ref current, null);
            Volatile.Write(ref sent, null);
            Volatile.Write(ref detection, null);
            Volatile.Write(ref lastMatch, null);
        }
        ClearStreaks();
        Changed?.Invoke();
        return null;
    }

    private void ClearStreaks()
    {
        lock (rested) rested.Clear();
        lock (streaks) streaks.Clear();
    }
}
