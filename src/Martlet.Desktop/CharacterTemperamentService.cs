using System.IO;
using Martlet.Avatar.Hosting;
using Martlet.Core.Contracts;
using Martlet.Core.Settings;

namespace Martlet.Desktop;

/// <summary>Each persona's touch temperament (Companion › Character › Touch temperament): how the character acts when a zone is
/// touched. The Thinking model decides it from the personality in the background when the personality is saved with a
/// meaningful change (never during a reply), and on Re-decide from personality; the owner's own edits stay until they re-decide.
/// Saved per persona in character-temperaments.json, which the owner's computers share.</summary>
internal sealed class CharacterTemperamentService(string? dataDirectory)
{
    internal const string FixtureVariable = "MARTLET_TOUCH_TEMPERAMENT_FIXTURE";
    internal static readonly TimeSpan Settle = TimeSpan.FromSeconds(3);

    private readonly object gate = new();
    private readonly Dictionary<Guid, CharacterTouchTemperament?> cache = [];
    private readonly Dictionary<Guid, CancellationTokenSource> pending = [];
    private string? status;
    private bool busy;

    internal event Action? Changed;

    /// <summary>How deciding went (or is going), or null before it was asked.</summary>
    internal string? Status => Volatile.Read(ref status);
    internal bool Busy => Volatile.Read(ref busy);

    internal static bool Fixture => Environment.GetEnvironmentVariable(FixtureVariable) is { Length: > 0 };

    /// <summary>The persona's saved temperament, or null (its zones play their built-in reactions).</summary>
    internal CharacterTouchTemperament? For(Guid? personaId)
    {
        if (personaId is not { } id || dataDirectory is null) return null;
        lock (gate)
        {
            if (cache.TryGetValue(id, out var known)) return known;
            var loaded = CharacterTouchTemperaments.Load(dataDirectory, id);
            cache[id] = loaded;
            return loaded;
        }
    }

    /// <summary>Forgets what was read, so the next read sees the file (after another computer's temperaments arrived).</summary>
    internal void Reload()
    {
        lock (gate) cache.Clear();
        Changed?.Invoke();
    }

    internal async Task<string?> SaveAsync(CharacterTouchTemperament temperament, CancellationToken token)
    {
        if (dataDirectory is null) return "Martlet's data folder isn't available.";
        try
        {
            var saved = await CharacterTouchTemperaments.SaveAsync(dataDirectory, temperament, DateTimeOffset.Now, token);
            lock (gate) cache[saved.PersonaId] = saved;
        }
        catch (Exception error) when (error is ContractException or IOException or UnauthorizedAccessException) { return error.Message; }
        // The owner's own change makes the news of the last decision old.
        if (temperament.Source == CharacterTouchTemperament.ByOwner) Volatile.Write(ref status, null);
        Changed?.Invoke();
        return null;
    }

    /// <summary>Goes back to the built-in reactions for the persona.</summary>
    internal async Task<string?> ResetAsync(Guid personaId, CancellationToken token)
    {
        if (dataDirectory is null) return "Martlet's data folder isn't available.";
        try { await CharacterTouchTemperaments.RemoveAsync(dataDirectory, personaId, token); }
        catch (Exception error) when (error is ContractException or IOException or UnauthorizedAccessException) { return error.Message; }
        lock (gate) cache[personaId] = null;
        Report("Back to the built-in reactions for every zone.");
        return null;
    }

    /// <summary>A persona's personality was saved: unless the change is only spacing, case or punctuation, or the owner's own
    /// choices hold the temperament, decide it again after <see cref="Settle"/> once Martlet isn't replying
    /// (<paramref name="replying"/>). A newer save of the same persona replaces a decision still waiting.</summary>
    internal void PersonalitySaved(PersonaProfile persona, Func<bool> replying,
        Func<string, string, string, CancellationToken, Task<(string? Answer, string? Failure)>> ask, CancellationToken lifetime)
    {
        if (string.IsNullOrWhiteSpace(persona.Text)) return;
        var digest = CharacterTouchTemperaments.Digest(persona.Text);
        var saved = For(persona.Id);
        if (saved?.PersonalityDigest == digest) return;
        if (saved?.Source == CharacterTouchTemperament.ByOwner)
        {
            Report($"{persona.Name}'s personality changed. Your own touch choices stay; Re-decide from personality uses the new personality.");
            return;
        }
        CancellationTokenSource next;
        lock (gate)
        {
            if (pending.Remove(persona.Id, out var old)) old.Cancel();
            next = CancellationTokenSource.CreateLinkedTokenSource(lifetime);
            pending[persona.Id] = next;
        }
        Report($"{persona.Name}'s personality changed; Martlet decides how it reacts to touch in a moment.");
        _ = Task.Run(async () =>
        {
            try
            {
                await Task.Delay(Settle, next.Token);
                while (replying()) await Task.Delay(TimeSpan.FromSeconds(2), next.Token);
                await DecideAsync(persona, ask, next.Token);
            }
            catch (OperationCanceledException) { }
            finally { lock (gate) if (pending.TryGetValue(persona.Id, out var current) && current == next) pending.Remove(persona.Id); }
        });
    }

    /// <summary>Asks the Thinking model (or reads MARTLET_TOUCH_TEMPERAMENT_FIXTURE, FIXTURE - NOT AI) how the persona reacts to
    /// touch, and saves the answer. On failure the previous temperament stays. Returns what happened.</summary>
    internal async Task<string> DecideAsync(PersonaProfile persona,
        Func<string, string, string, CancellationToken, Task<(string? Answer, string? Failure)>> ask, CancellationToken token)
    {
        if (string.IsNullOrWhiteSpace(persona.Text)) return Report($"{persona.Name} has no personality text to decide from.");
        Volatile.Write(ref busy, true);
        var fixture = Environment.GetEnvironmentVariable(FixtureVariable);
        var label = fixture is { Length: > 0 } ? "FIXTURE - NOT AI: " : "";
        Report(label + (fixture is { Length: > 0 }
            ? $"reading {persona.Name}'s touch temperament from {FixtureVariable} instead of asking the Thinking model..."
            : $"Asking the Thinking model how {persona.Name} reacts to touch..."));
        try
        {
            var (answer, failure) = fixture is { Length: > 0 }
                ? (File.Exists(fixture) ? await File.ReadAllTextAsync(fixture, token) : null, File.Exists(fixture) ? null : "the fixture file is missing")
                : await ask("Deciding touch reactions", CharacterTouchTemperaments.DecisionInstructions, CharacterTouchTemperaments.DecisionRequest(persona.Text), token);
            var decided = CharacterTouchTemperaments.Parse(answer, persona.Id, CharacterTouchTemperaments.Digest(persona.Text),
                fixture is { Length: > 0 } ? CharacterTouchTemperament.ByFixture : CharacterTouchTemperament.ByThinking, DateTimeOffset.Now);
            if (decided is null)
                return Report(label + (failure is null
                    ? $"The answer about {persona.Name}'s touch reactions couldn't be read, so the previous ones stay."
                    : $"Couldn't ask the Thinking model ({failure}), so {persona.Name}'s previous touch reactions stay."));
            if (await SaveAsync(decided, token) is { } why) return Report(label + "The touch reactions were decided but couldn't be saved: " + why);
            ErrorLog.Info($"Touch temperament decided for a persona ({decided.Source}): {CharacterTouchTemperaments.Summary(decided)}.");
            // The whole temperament is in the card's table and in its status line's tooltip, so this says only that it worked.
            return Report(label + $"Decided how {persona.Name} reacts to touch at {DateTime.Now:t}.");
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { return Report("Deciding touch reactions was stopped."); }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            return Report(label + $"Couldn't read the answer ({error.Message}), so {persona.Name}'s previous touch reactions stay.");
        }
        finally { Volatile.Write(ref busy, false); Changed?.Invoke(); }
    }

    private string Report(string text)
    {
        Volatile.Write(ref status, text);
        Changed?.Invoke();
        return text;
    }
}
