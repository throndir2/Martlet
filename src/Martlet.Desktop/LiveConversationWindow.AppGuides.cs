using System.Windows;
using System.Windows.Automation;
using Martlet.Conversation;
using Martlet.Conversation.Guides;

namespace Martlet.Desktop;

/// <summary>App guides in the talk window (docs/APP_GUIDES.md): reading up on its own on a game that came to the front, and
/// reading up started from Companion › App guides, while this conversation runs (its task list shows "Reading up on").</summary>
public partial class LiveConversationWindow
{
    /// <summary>Martlet reads up on <paramref name="offer"/> (a game without a guide in front) on its own as this conversation's
    /// job, and tells you as soon as it is free, or with what you say next. Only in a conversation that runs; null otherwise (the
    /// main window reads up by itself then).</summary>
    internal BackgroundJobStart? ReadUpOnItsOwn(AppGuideService guides, AppGuideOfferCandidate offer)
    {
        if (closed || !begun) return null;
        var start = controller.ReadUpOnItsOwn(guides, offer);
        RenderActions();
        return start;
    }

    /// <summary>Reads up on <paramref name="app"/> as this conversation's background job, or null when the conversation doesn't
    /// run (the page reads up by itself then).</summary>
    internal BackgroundJobStart? ReadUp(AppGuideService guides, string app, IReadOnlyList<string> sites)
    {
        if (closed || !begun) return null;
        var start = controller.StartReadingUp(guides, app, sites);
        RenderActions();
        return start;
    }

    /// <summary>Reads more pages of <paramref name="entry"/>'s wiki as this conversation's background job (Read more here, on the
    /// speech bubble or on Companion › App guides), or null when the conversation doesn't run (the caller reads more by itself
    /// then).</summary>
    internal BackgroundJobStart? ReadMore(AppGuideEntry entry)
    {
        if (closed || !begun) return null;
        var start = controller.ReadMore(entry);
        RenderActions();
        return start;
    }

    /// <summary>Raised on the UI thread when a reply used an app's guide and Martlet may read more about it: the main window shows
    /// Read more on the character's speech bubble for a little while. At most once every <see cref="OfferAgain"/> for one app.</summary>
    internal event Action<AppGuideEntry>? ReadMoreOffered;

    /// <summary>How long after offering Read more for an app the speech bubble offers it again, at the soonest.</summary>
    internal static TimeSpan OfferAgain { get; } = TimeSpan.FromMinutes(10);

    private readonly Dictionary<string, long> readMoreOffered = new(StringComparer.Ordinal);

    private void OfferReadMore(string key)
    {
        if (controller.Guides is not { On: true } guides || guides.Building is not null || controller.ReadingGuide ||
            guides.Library.Apps.FirstOrDefault(a => a.Key == key) is not { BuiltAt: not null } entry)
            return;
        var now = clock.GetTimestamp();
        if (readMoreOffered.TryGetValue(key, out var at) && clock.GetElapsedTime(at, now) < OfferAgain) return;
        readMoreOffered[key] = now;
        ReadMoreOffered?.Invoke(entry);
    }

    // The header's Read more: shown while the app in front (or the one the last reply used a guide about) has a guide; off while
    // Martlet reads up on an app.
    private void RenderReadMore()
    {
        var target = begun && !closed ? controller.ReadMoreTarget() : null;
        ReadMoreChip.Visibility = target is null ? Visibility.Collapsed : Visibility.Visible;
        if (target is null) return;
        var reading = controller.Guides?.Building is not null || controller.ReadingGuide;
        ReadMoreChip.IsEnabled = !reading;
        var tip = reading ? "Martlet is reading up on an app now; Read more works again when it's done."
            : $"Read more pages about {target.Name} from its wiki. Its guide has {target.Pages} page{(target.Pages == 1 ? "" : "s")} now.";
        if (Equals(ReadMoreChip.ToolTip, tip)) return;
        ReadMoreChip.ToolTip = tip;
        AutomationProperties.SetName(ReadMoreChip, "Read more about " + target.Name);
        AutomationProperties.SetHelpText(ReadMoreChip, tip);
    }

    private void ReadMore_Click(object sender, RoutedEventArgs e)
    {
        if (controller.ReadMoreTarget() is not { } target) return;
        var start = ReadMore(target);
        notice = start?.Job is not null ? $"Reading more about {target.Name}. Martlet tells you when it's done."
            : $"Couldn't read more about {target.Name}: {start?.Message ?? start?.Refusal ?? "the conversation isn't running"}";
        ErrorLog.Info($"App guides: you pressed Read more in the talk window ({(start?.Job is { } job ? "started " + job.Id : "not started: " + start?.Refusal)}).");
        RenderActions();
    }
}
