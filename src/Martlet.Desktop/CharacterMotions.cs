using System.IO;
using System.Security.Cryptography;
using System.Text.Json;
using Martlet.Avatar.Hosting;
using Martlet.Conversation;
using Martlet.Core.Creations;

namespace Martlet.Desktop;

/// <summary>Makes body motions on the motion computer for one piece of work (docs/AVATARS.md#motions-in-conversation): a
/// paired computer's motion role (HY-Motion), or the FIXTURE test maker. <see cref="MotionStudio.For"/> makes one from the
/// shared motion choice; tests use their own.</summary>
internal interface IMotionStudio : IDisposable
{
    /// <summary>Where motions are made, in a few words for the model and the log ("the motion PC").</summary>
    string Where { get; }

    /// <summary>The paired computer that makes them (its host ID), so a motion holds it away from background thinks; null when
    /// none does (the FIXTURE maker).</summary>
    string? HostId { get; }

    /// <summary>Null when a motion can be made now, else why not, in a few plain words.</summary>
    Task<string?> ProblemAsync(CancellationToken token);

    /// <summary>Makes one motion (a VRMA clip). Throws <see cref="MotionStudioException"/> with the problem in plain words.</summary>
    Task<MotionMade> MakeAsync(MotionArguments arguments, long seed, IProgress<string>? progress, CancellationToken token);

    /// <summary>Frees the motion computer's GPU a while after the last motion (the motion relay's free), as pictures do.</summary>
    void FreeLater();
}

/// <summary>A motion couldn't be made or kept; the message says why in a few plain words.</summary>
internal sealed class MotionStudioException(string problem, Exception? inner = null) : Exception(problem, inner);

/// <summary>Plays motions on the character this PC shows.</summary>
internal interface ICharacterMotionPlayer
{
    /// <summary>Plays the shown character's emote or motion whose tag is <paramref name="tag"/>; returns null when the
    /// character has no such tag (or none is shown), else what happened in words for the model.</summary>
    Task<string?> PlayTagAsync(string tag, string reason, CancellationToken token);

    /// <summary>Plays the kept motion <paramref name="key"/> on the shown VRM character (after the character's emotes and
    /// motions take it in); null when it played, else why not, in a few words.</summary>
    Task<string?> PlayKeptAsync(string key, string reason, CancellationToken token);
}

/// <summary>The VRM character's body motions on this PC (docs/AVATARS.md#motions-in-conversation): whether replies get
/// body_motion (motions are set up and the chosen character is a VRM; saved choices only, so the tools don't come and go with
/// the motion computer), making a motion one at a time on the motion computer and keeping it as a <c>motion</c> creation
/// (shared with every Martlet computer), the kept motions every VRM character can do, and Martlet's starter motions, made once.</summary>
internal sealed class CharacterMotions(string? dataDirectory, Func<string?> creationsFolder, Func<string, bool> isSetUp,
    Func<string, IMotionStudio?> studioFor, ICharacterMotionPlayer player, CreationRegistry? registry = null, TimeProvider? clock = null)
{
    /// <summary>The starters file (<see cref="MotionTools.StartersFile"/>).</summary>
    internal const string StartersFile = MotionTools.StartersFile;

    private readonly SemaphoreSlim making = new(1, 1);
    private readonly CreationRegistry kinds = registry ?? CreationRegistry.Shared;
    private readonly TimeProvider time = clock ?? TimeProvider.System;
    private volatile bool chosenVrm;
    private volatile string? characterName;
    private volatile string? starters;
    private int startersRunning;

    /// <summary>Whether the chosen character (Companion › Character) is a VRM: kept from the saved choice by the desktop.</summary>
    internal bool ChosenVrm { get => chosenVrm; set => chosenVrm = value; }

    /// <summary>The active persona's name, kept with a new motion as the character it was made for.</summary>
    internal string? CharacterName { get => characterName; set => characterName = value; }

    internal ICharacterMotionPlayer Player => player;

    /// <summary>Whether motions are set up (the saved motion settings, or the FIXTURE switch), whatever the character.</summary>
    internal bool IsSetUp => dataDirectory is not null && isSetUp(dataDirectory);

    /// <summary>Whether replies get body_motion: motions are set up and the chosen character is a VRM.</summary>
    internal bool Offered => ChosenVrm && IsSetUp;

    /// <summary>A maker for one piece of work, or null while motions aren't set up.</summary>
    internal IMotionStudio? Studio() => dataDirectory is null ? null : studioFor(dataDirectory);

    /// <summary>Whether a motion is being made now.</summary>
    internal bool Busy => making.CurrentCount == 0;

    private readonly object offeredGate = new();
    private (Guid Conversation, HashSet<string> Ids)? offered;

    /// <summary><paramref name="catalog"/> with only the kept motions it had when <paramref name="conversation"/> began (its
    /// first request asked): a motion kept later joins the reply tags at the next conversation, so the reply instructions, and
    /// with them the start of every request, stay the same while a conversation goes on.</summary>
    internal CharacterActionCatalog OfferedIn(Guid conversation, CharacterActionCatalog catalog)
    {
        ArgumentNullException.ThrowIfNull(catalog);
        var kept = catalog.Inventory.Sources.Where(s => CharacterActionInventory.IsKeptMotion(s.Id)).Select(s => s.Id).ToArray();
        HashSet<string> ids;
        lock (offeredGate)
        {
            if (offered is not { } current || current.Conversation != conversation) offered = current = (conversation, [.. kept]);
            ids = current.Ids;
        }
        return kept.All(ids.Contains) ? catalog : catalog with
        {
            Inventory = catalog.Inventory with
            {
                Sources = [.. catalog.Inventory.Sources.Where(s => !CharacterActionInventory.IsKeptMotion(s.Id) || ids.Contains(s.Id))]
            }
        };
    }

    /// <summary>How the starter motions went (or are going), or null before Martlet looked.</summary>
    internal string? Starters => starters;

    /// <summary>The live kept motions whose clip is on this computer, oldest first, as the character's emotes and motions list
    /// them.</summary>
    internal IReadOnlyList<Martlet.Avatar.Hosting.KeptMotion> Kept()
    {
        if (creationsFolder() is not { } folder) return [];
        var library = CreationStore.View(folder);
        return [.. MotionCreations.Kept(library).Where(c => CreationStore.IsComplete(folder, c)).Select(c => new Martlet.Avatar.Hosting.KeptMotion(
            c.Key, MotionCreations.NameOf(c), c.Text ?? c.Summary ?? MotionCreations.NameOf(c), MotionCreations.SecondsOf(c)))];
    }

    /// <summary>The kept motions a VRM gets ahead of their first use: Martlet's starter motions, then the newest, at most
    /// <see cref="AvatarController.PreloadedMotions"/>.</summary>
    internal IReadOnlyList<string> PreloadKeys()
    {
        if (creationsFolder() is not { } folder) return [];
        var kept = MotionCreations.Kept(CreationStore.View(folder)).Where(c => CreationStore.IsComplete(folder, c)).ToArray();
        return [.. kept.Where(c => MotionCreations.Metadata(c)?.Starter == true).Concat(kept.Reverse()).Select(c => c.Key)
            .Distinct(StringComparer.Ordinal).Take(AvatarController.PreloadedMotions)];
    }

    /// <summary>The kept motion's clip by its key, or null when it isn't (yet) on this computer.</summary>
    internal async Task<byte[]?> ClipAsync(string key, CancellationToken token)
    {
        if (creationsFolder() is not { } folder || CreationStore.View(folder).Resolve(key) is not { Kind: MotionCreations.KindName, Removed: false } creation)
            return null;
        return (await MotionCreations.LoadAsync(creation, CreationStore.Assets(folder, creation), token).ConfigureAwait(false)).Clip;
    }

    /// <summary>Makes one motion with <paramref name="studio"/> and keeps it, one at a time on this PC (a conversation's
    /// motions and the starter motions take turns); <paramref name="report"/> hears each step. Throws
    /// <see cref="MotionStudioException"/> with the problem in a few plain words.</summary>
    internal async Task<(Creation Creation, MotionMade Made)> MakeAndKeepAsync(MotionArguments arguments, IMotionStudio studio,
        Action<string> report, CancellationToken token, bool starter = false)
    {
        ArgumentNullException.ThrowIfNull(arguments);
        ArgumentNullException.ThrowIfNull(studio);
        if (arguments.Motion is null) throw new MotionStudioException("no motion was described");
        if (!making.Wait(0))
        {
            report("Waiting for the motion before it");
            await making.WaitAsync(token).ConfigureAwait(false);
        }
        try
        {
            report("Checking the motion computer");
            if (await studio.ProblemAsync(token).ConfigureAwait(false) is { } problem) throw new MotionStudioException(problem.TrimEnd('.'));
            report("Making the motion on " + studio.Where);
            var made = await studio.MakeAsync(arguments, Seed(), new Progress<string>(report), token).ConfigureAwait(false);
            if (made.Clip.Length > Math.Min(MotionCreations.MaximumClipBytes, VrmAnimationClip.MaximumBytes))
                throw new MotionStudioException("the motion was too large to keep");
            try { VrmAnimationClip.Inspect(made.Clip); }
            catch (InvalidDataException error) { throw new MotionStudioException($"the motion computer sent a motion that can't be played ({error.Message.TrimEnd('.')})", error); }
            report("Keeping the motion");
            if (creationsFolder() is not { } folder) throw new MotionStudioException("Martlet's data folder isn't available");
            var author = new CreationAuthor
            {
                Device = HostSetupCommands.SuggestedDeviceId(), Computer = Martlet.Core.Network.ThisComputer.Name,
                Persona = CharacterName is { Length: > 0 } persona ? persona : null
            };
            try
            {
                var creation = await CreationStore.AddAsync(folder, MotionCreations.Draft(arguments, made, author, CharacterName, starter), kinds,
                    time.GetUtcNow(), token).ConfigureAwait(false);
                return (creation, made);
            }
            catch (Exception error) when (CreationStore.IsFailure(error))
            {
                ErrorLog.Warn($"Motions: couldn't keep the motion {arguments.Name} ({error.Message}).");
                throw new MotionStudioException("there was no room to keep the motion on this PC", error);
            }
        }
        finally { making.Release(); }
    }

    /// <summary>Whether the starter motions should be made now: motions are set up, the chosen character is a VRM, this
    /// computer hasn't seen them made, and none is kept yet (another computer may have made them: then this one marks them
    /// seen).</summary>
    internal bool StartersDue()
    {
        if (!Offered || Volatile.Read(ref startersRunning) != 0 || creationsFolder() is not { } folder) return false;
        if (File.Exists(Path.Combine(folder, StartersFile))) return false;
        if (MotionCreations.Kept(CreationStore.View(folder)).Any(c => MotionCreations.Metadata(c)?.Starter == true))
        {
            MarkStarters(folder, "seen");
            starters = "Martlet's starter motions are kept (made on another computer).";
            return false;
        }
        return true;
    }

    /// <summary>Makes Martlet's starter motions (<see cref="MotionTools.Starters"/>) once, one at a time, when the motion
    /// computer says it is ready, then frees its GPU. Returns what happened, in words.</summary>
    internal async Task<string> MakeStartersAsync(CancellationToken token)
    {
        if (Interlocked.Exchange(ref startersRunning, 1) != 0) return starters ?? "The starter motions are being made.";
        try
        {
            if (creationsFolder() is not { } folder) return starters = "Martlet's data folder isn't available, so no starter motions were made.";
            using var studio = Studio();
            if (studio is null) return starters = "Motions aren't set up, so no starter motions were made.";
            if (await studio.ProblemAsync(token).ConfigureAwait(false) is { } problem)
                return starters = $"The starter motions wait until the motion computer is ready ({problem.TrimEnd('.')}).";
            var kept = MotionCreations.Kept(CreationStore.View(folder)).Select(MotionCreations.NameOf).ToHashSet(StringComparer.Ordinal);
            var made = 0;
            var failed = new List<string>();
            try
            {
                foreach (var starter in MotionTools.Starters.Where(s => !kept.Contains(s.Name)))
                {
                    starters = $"Making the starter motion {starter.Name} ({made} of {MotionTools.Starters.Count} made)…";
                    try
                    {
                        var (creation, _) = await MakeAndKeepAsync(starter, studio, _ => { }, token, starter: true).ConfigureAwait(false);
                        made++;
                        ErrorLog.Info($"Motions: kept the starter motion {starter.Name} as {creation.Key}.");
                    }
                    catch (MotionStudioException error)
                    {
                        failed.Add($"{starter.Name} ({error.Message})");
                        ErrorLog.Warn($"Motions: the starter motion {starter.Name} wasn't made ({error.Message}).");
                    }
                }
            }
            finally { studio.FreeLater(); }
            if (failed.Count == 0) MarkStarters(folder, "made");
            return starters = failed.Count == 0
                ? $"Martlet made its starter motions ({made} new) at {DateTime.Now:t}."
                : $"Martlet made {made} starter motion{(made == 1 ? "" : "s")}; {string.Join(", ", failed)} didn't work. It tries again later.";
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { return starters = "Making the starter motions was stopped."; }
        finally { Volatile.Write(ref startersRunning, 0); }
    }

    private void MarkStarters(string folder, string how)
    {
        try
        {
            File.WriteAllText(Path.Combine(folder, StartersFile),
                JsonSerializer.Serialize(new { starters = how, at = time.GetUtcNow(), names = MotionTools.Starters.Select(s => s.Name) }));
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            ErrorLog.Warn($"Motions: couldn't note the starter motions ({error.Message}).");
        }
    }

    private static long Seed() => RandomNumberGenerator.GetInt32(int.MaxValue);
}
