using System.IO;
using Martlet.Core.Contracts;
using Martlet.Core.Speakers;

namespace Martlet.Desktop;

/// <summary>Companion › People's decisions, without WPF: the voices in the order the page lists them, what the lines say, and the
/// changes the owner makes (recognition on or off, keeping clips, names, This voice is mine, merging and forgetting). Each
/// change syncs to your other computers shortly after. <see cref="PeoplePage"/> draws it, and <see cref="IPeoplePageServices"/>
/// is all it reads in the app.</summary>
internal sealed class PeoplePageModel
{
    private readonly IPeoplePageServices services;
    private readonly LocalVoices voices;

    internal PeoplePageModel(IPeoplePageServices services)
    {
        this.services = services;
        voices = services.Voices;
        Known = [.. voices.Roster.Live.OrderByDescending(voices.IsYours).ThenByDescending(v => v.Account is not null)
            .ThenByDescending(v => v.Named).ThenByDescending(v => v.LastHeardAt)];
    }

    /// <summary>The voices Martlet knows: yours first, then those linked to accounts, the named ones, then the latest heard.</summary>
    internal IReadOnlyList<KnownVoice> Known { get; }

    /// <summary>The Voices Martlet knows heading (PeopleVoiceCount).</summary>
    internal string CountText => Known.Count == 0 ? "Voices Martlet knows" : $"Voices Martlet knows ({Known.Count})";

    /// <summary>The link line (PeopleLinkStatus).</summary>
    internal string LinkText => LinkStatus(Known.Count, Known.Count(v => v.Account is not null), Known.Count(voices.IsYours),
        voices.Account is not null);

    /// <summary>People's link line: how many voices link to people's accounts and how many are yours. Counts only, no names.
    /// Before the desktop knows its account, only whether a voice is marked as yours.</summary>
    internal static string LinkStatus(int voices, int linked, int yours, bool accounts) =>
        !accounts ? yours == 0 ? "No voice is marked as yours yet: tick This is me on yours." : "Martlet knows which voice is yours."
        : $"{linked} of {voices} voice{(voices == 1 ? "" : "s")} {(linked == 1 ? "is" : "are")} linked to people's accounts; " +
          (yours == 0 ? "none is yours yet, so tick This voice is ... on yours." : $"your account has {yours} voice{(yours == 1 ? "" : "s")}.");

    /// <summary>A voice's title note: you, whose voice it is, or that it has no name yet (null for a named voice of nobody's account).</summary>
    internal string? Whose(KnownVoice voice) =>
        voices.IsYours(voice) ? "you" : voice.Account is { } other ? $"{services.AccountName(other) ?? "someone else"}'s voice"
            : voice.Named ? null : "no name yet";

    /// <summary>A voice's line under its name: its number, how often and when it was heard, and voices merged into it.</summary>
    internal static string Heard(KnownVoice voice) =>
        $"{(voice.Named ? $"Voice {voice.Number} · " : "")}heard {voice.Heard} time{(voice.Heard == 1 ? "" : "s")}, last {Ago(voice.LastHeardAt)}" +
        (voice.MergedVoices > 0 ? $" · {voice.MergedVoices + 1} voices merged" : "");

    /// <summary>The warning when other named voices go by the same name as <paramref name="voice"/>, or null.</summary>
    internal string? Twins(KnownVoice voice)
    {
        var twins = Known.Where(other => other.Id != voice.Id && other.Named && voice.Named &&
            string.Equals(other.DisplayName, voice.DisplayName, StringComparison.OrdinalIgnoreCase)).ToArray();
        return twins.Length == 0 ? null : $"{string.Join(", ", twins.Select(t => $"Voice {t.Number}"))} also goes by {voice.DisplayName}, " +
            "so merge them below if they're the same person.";
    }

    /// <summary>"just now", "12 min ago", "3 h ago", or the date.</summary>
    internal static string Ago(DateTimeOffset when)
    {
        var age = DateTimeOffset.UtcNow - when;
        return age < TimeSpan.FromMinutes(1) ? "just now"
            : age < TimeSpan.FromHours(1) ? $"{(int)age.TotalMinutes} min ago"
            : age < TimeSpan.FromDays(1) ? $"{(int)age.TotalHours} h ago"
            : when.LocalDateTime.ToString("d");
    }

    // ---------- the changes ----------

    internal void SetRecognition(bool on)
    {
        try
        {
            voices.SetEnabled(on);
            services.QueueSettingsSync();
            services.Say(on ? "Voice recognition is on, and open conversations use it from your next message."
                : "Voice recognition is off, and saved voices are kept.");
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            services.Say($"Couldn't save your choice: {error.Message}");
        }
        services.Redraw();
    }

    internal void SetKeepClips(bool on)
    {
        try
        {
            voices.Clips.SetEnabled(on);
            services.QueueSettingsSync();
            services.Say(on ? "Martlet keeps a few clips of voices you haven't named." : "Martlet keeps no clips, and deleted the ones it had.");
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            services.Say($"Couldn't save your choice: {error.Message}");
        }
        services.Redraw();
    }

    /// <summary>Changes a voice's names as they are now (the page may be older than the list): <paramref name="name"/> turns
    /// the name the owner chose (null when none) into the one to show (null leaves it to the names learned), and
    /// <paramref name="names"/> turns every name it goes by into the ones it should.</summary>
    internal void SaveNames(KnownVoice voice, Func<string?, string?> name, Func<IEnumerable<string>, IEnumerable<string>> names)
    {
        if (services.Closing) return;
        try
        {
            if (voices.Roster.Resolve(voice.Id) is not { } current) return;
            voices.SetNames(current.Id, name(current.Name), names(current.Names.Select(n => n.Text)).ToArray());
            var now = voices.Roster.Resolve(voice.Id);
            services.Say($"Saved the names of {now?.DisplayName ?? voice.DisplayName}.");
        }
        catch (ContractException error) { services.Say(error.Message); }
        // After the click returns: the page is rebuilt, the button with it.
        services.RedrawLater();
    }

    /// <summary>Adds the name <paramref name="typed"/> to <paramref name="voice"/>; on a voice the owner hasn't named, it becomes
    /// the name shown.</summary>
    internal void AddName(KnownVoice voice, string typed)
    {
        typed = typed.Trim();
        if (typed.Length == 0) return;
        if (VoiceRoster.CleanName(typed) is null)
        {
            services.Say($"Not added: names start with a letter and use at most {VoiceRoster.MaximumNameLength} characters.");
            return;
        }
        SaveNames(voice, shownName => shownName ?? typed, all => all.Append(typed));
    }

    /// <summary>Links the voice to the signed-in account, or unlinks it; a voice linked to someone else's account moves to yours
    /// only after you confirm.</summary>
    internal void Link(KnownVoice voice, bool yours)
    {
        if (services.Closing) return;
        var current = voices.Roster.Resolve(voice.Id);
        if (current is null) return;
        if (yours && current.Account is { } other && other != voices.Account &&
            !services.Confirm($"{current.DisplayName} is {services.AccountName(other) ?? "someone else"}'s voice: " +
                "make it yours instead, on all your computers?", "Make it mine"))
        {
            services.RedrawLater();
            return;
        }
        voices.Link(current.Id, yours);
        services.Say(yours ? $"{current.DisplayName} is now your voice." : $"{current.DisplayName} is no longer linked to you.");
        // After the click returns: the page is rebuilt, the box with it.
        services.RedrawLater();
    }

    internal void Merge(KnownVoice from, KnownVoice into)
    {
        if (!services.Confirm($"Merge {from.DisplayName} into {into.DisplayName} as one person on all your computers (you can't undo this)?",
                "Merge"))
            return;
        voices.Join(from.Id, into.Id);
        services.Say($"Merged {from.DisplayName} into {into.DisplayName}.");
        services.Redraw();
    }

    internal void Forget(KnownVoice voice)
    {
        if (!services.Confirm($"Forget {voice.DisplayName} and delete its saved voice and names from all your computers?", "Forget"))
            return;
        voices.Forget(voice.Id);
        services.Say($"Forgot {voice.DisplayName}.");
        services.Redraw();
    }

    internal void ForgetAll()
    {
        if (!services.Confirm("Forget every voice and delete all saved voices and names from all your computers?", "Forget all"))
            return;
        voices.ForgetAll();
        services.Say("Martlet forgot every voice.");
        services.Redraw();
    }
}
