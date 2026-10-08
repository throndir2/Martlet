using System.IO;
using Martlet.Avatar.Hosting;
using Martlet.Core.Contracts;
using Martlet.Core.Settings;

namespace Martlet.Desktop;

/// <summary>Each persona's touch temperament (Companion › Touch › Touch temperament): how the character acts when a zone is
/// touched. The Thinking model decides a persona's own temperament from the personality in the background when the personality
/// is saved with a meaningful change (never during a reply), and on Re-decide from personality; the owner's own edits stay until
/// they re-decide. The owner can also make named custom temperaments and choose, per persona, its own, the built-in reactions or
/// a custom one. All of it is saved in character-temperaments.json, which the owner's computers share.</summary>
internal sealed class CharacterTemperamentService(string? dataDirectory)
{
    internal const string FixtureVariable = "MARTLET_TOUCH_TEMPERAMENT_FIXTURE";
    internal static readonly TimeSpan Settle = TimeSpan.FromSeconds(3);

    private readonly object gate = new();
    private readonly Dictionary<Guid, CancellationTokenSource> pending = [];
    private TouchTemperamentSet? saved;
    private string? status;
    private bool busy;

    internal event Action? Changed;

    /// <summary>How deciding went (or is going), or null before it was asked.</summary>
    internal string? Status => Volatile.Read(ref status);
    internal bool Busy => Volatile.Read(ref busy);

    internal static bool Fixture => Environment.GetEnvironmentVariable(FixtureVariable) is { Length: > 0 };

    /// <summary>What character-temperaments.json holds: read once, then kept up to date by each change made here.</summary>
    internal TouchTemperamentSet Saved
    {
        get
        {
            if (dataDirectory is null) return TouchTemperamentSet.Empty;
            lock (gate) return saved ??= CharacterTouchTemperaments.LoadSet(dataDirectory);
        }
    }

    /// <summary>The temperament the persona uses: the custom one the owner chose for it, null for the built-in reactions (its
    /// zones play their built-in reactions), else its own (null until it is decided). Touch reactions, strokes and the gaze all
    /// follow it.</summary>
    internal CharacterTouchTemperament? For(Guid? personaId) => personaId is { } id ? Saved.For(id) : null;

    /// <summary>The persona's own temperament (decided from its personality, or edited by the owner), whatever it uses now.</summary>
    internal CharacterTouchTemperament? Own(Guid personaId) => Saved.Own(personaId);

    /// <summary>Forgets what was read, so the next read sees the file (after another computer's temperaments arrived).</summary>
    internal void Reload()
    {
        lock (gate) saved = null;
        Changed?.Invoke();
    }

    // One change to the file; byOwner: the owner's own change, which makes the news of the last decision old. Returns why it
    // failed, or null.
    private async Task<string?> UpdateAsync(Func<TouchTemperamentSet, TouchTemperamentSet> change, CancellationToken token, bool byOwner = true)
    {
        if (dataDirectory is null) return "Martlet's data folder isn't available.";
        try
        {
            var next = await CharacterTouchTemperaments.UpdateAsync(dataDirectory, change, token);
            lock (gate) saved = next;
        }
        catch (Exception error) when (error is ContractException or IOException or UnauthorizedAccessException) { return error.Message; }
        if (byOwner) Volatile.Write(ref status, null);
        Changed?.Invoke();
        return null;
    }

    /// <summary>Saves the persona's own temperament.</summary>
    internal Task<string?> SaveAsync(CharacterTouchTemperament temperament, CancellationToken token)
    {
        var next = temperament with { UpdatedAt = DateTimeOffset.UtcNow };
        return UpdateAsync(set => set.WithOwn(next), token, byOwner: temperament.Source == CharacterTouchTemperament.ByOwner);
    }

    /// <summary>Saves a custom temperament; every persona that uses it changes with it.</summary>
    internal Task<string?> SaveCustomAsync(CustomTouchTemperament custom, CancellationToken token)
    {
        var next = custom with { UpdatedAt = DateTimeOffset.UtcNow };
        return UpdateAsync(set => set.WithCustom(next), token);
    }

    /// <summary>Chooses what the persona uses: null for its own temperament, <see cref="CharacterTouchTemperaments.BuiltIn"/> for
    /// the built-in reactions, or a custom temperament's ID.</summary>
    internal Task<string?> ChooseAsync(Guid personaId, string? uses, CancellationToken token) => UpdateAsync(set => set.Choose(personaId, uses), token);

    /// <summary>Makes a custom temperament called <paramref name="name"/>, a copy of what the persona uses now, and the persona
    /// then uses it.</summary>
    internal Task<string?> CreateCustomAsync(Guid personaId, string name, CancellationToken token)
    {
        if (CharacterTouchTemperaments.NameProblem(name) is { } problem) return Task.FromResult<string?>(problem);
        return UpdateAsync(set =>
        {
            var made = CustomTouchTemperament.Copy(name, set.For(personaId), DateTimeOffset.UtcNow);
            return set.WithCustom(made).Choose(personaId, made.Id.ToString());
        }, token);
    }

    internal Task<string?> RenameCustomAsync(Guid id, string name, CancellationToken token)
    {
        if (CharacterTouchTemperaments.NameProblem(name) is { } problem) return Task.FromResult<string?>(problem);
        return UpdateAsync(set => set.Custom.FirstOrDefault(c => c.Id == id) is { } custom
            ? set.WithCustom(custom with { Name = name.Trim(), UpdatedAt = DateTimeOffset.UtcNow })
            : throw new ContractException(ErrorCode.InvalidContract, "That custom temperament is gone."), token);
    }

    /// <summary>Deletes a custom temperament; the personas that used it use their own again.</summary>
    internal Task<string?> DeleteCustomAsync(Guid id, CancellationToken token) => UpdateAsync(set => set.WithoutCustom(id), token);

    /// <summary>A persona's personality was saved: unless the change is only spacing, case or punctuation, or the owner's own
    /// choices hold its own temperament, decide that again after <see cref="Settle"/> once Martlet isn't replying
    /// (<paramref name="replying"/>). A newer save of the same persona replaces a decision still waiting. What the persona uses
    /// (its own, the built-in reactions or a custom temperament) doesn't change.</summary>
    internal void PersonalitySaved(PersonaProfile persona, Func<bool> replying,
        Func<string, string, string, CancellationToken, Task<(string? Answer, string? Failure)>> ask, CancellationToken lifetime)
    {
        if (string.IsNullOrWhiteSpace(persona.Text)) return;
        var digest = CharacterTouchTemperaments.Digest(persona.Text);
        var own = Own(persona.Id);
        if (own?.PersonalityDigest == digest) return;
        if (own?.Source == CharacterTouchTemperament.ByOwner)
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
    /// touch, and saves the answer as its own temperament; with <paramref name="use"/> (Re-decide from personality) the persona
    /// then uses it, whatever it used before. Only the personality is sent, never a custom temperament. On failure the previous
    /// temperament stays. Returns what happened.</summary>
    internal async Task<string> DecideAsync(PersonaProfile persona,
        Func<string, string, string, CancellationToken, Task<(string? Answer, string? Failure)>> ask, CancellationToken token, bool use = false)
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
            if (await UpdateAsync(set => (use ? set.Choose(persona.Id, null) : set).WithOwn(decided), token, byOwner: false) is { } why)
                return Report(label + "The touch reactions were decided but couldn't be saved: " + why);
            ErrorLog.Info($"Touch temperament decided for a persona ({decided.Source}): {CharacterTouchTemperaments.Summary(decided)}.");
            // The whole temperament is in the card's table and in its status line's tooltip, so this says only that it worked.
            var other = Saved.UsesBuiltIn(persona.Id) ? CharacterTouchTemperaments.BuiltInLabel : Saved.CustomOf(persona.Id)?.Name;
            return Report(label + $"Decided how {persona.Name} reacts to touch at {DateTime.Now:t}." +
                (other is null ? "" : $" {persona.Name} still uses \"{other}\"; choose \"{CharacterTouchTemperaments.OwnLabel}\" under Uses to use it."));
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