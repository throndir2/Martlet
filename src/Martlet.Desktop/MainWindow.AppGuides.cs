using System.IO;
using Martlet.Conversation;
using Martlet.Conversation.Guides;

namespace Martlet.Desktop;

/// <summary>App guides (docs/APP_GUIDES.md), the work behind Companion › App guides (<see cref="AppGuidesPage"/> in
/// Pages\AppGuides): the guide library in &lt;data folder&gt;\guides, the watch of the program in front (it runs only while App guides
/// are on, on this companion PC), reading up on the app in front on its own, Read up now and Read again, and Read more (from the
/// page and from the speech bubble).</summary>
public partial class MainWindow
{
    private AppGuideService? appGuides;
    private AppGuideWatch? appGuideWatch;
    // What the last thing done on the App guides page said (AppGuidesResult); reading up sets it too.
    private string? appGuidesResult;

    private void InitializeAppGuides()
    {
        if (store is null) return;
        var web = new WebAccess();
        appGuides = new AppGuideService(new FileAppGuideStore(Path.Combine(store.DataDirectory, "guides")), new WebGuideBuilder(web, web), web);
        if (conversation is not null)
        {
            conversation.Guides = appGuides;
            conversation.StartGuideResearch(appGuides, web);
        }
        appGuideWatch = new AppGuideWatch(appGuides);
        appGuideWatch.OfferDue += offer => Dispatcher.BeginInvoke(() => OfferAppGuide(offer));
        appGuides.Changed += () => Dispatcher.BeginInvoke(AppGuidesChanged);
        LoadAppGuidesAsync().Forget();
    }

    private async Task LoadAppGuidesAsync()
    {
        if (appGuides is null) return;
        try
        {
            await appGuides.LoadAsync(lifetime.Token);
            var library = appGuides.Library;
            ErrorLog.Info($"App guides: {(library.On ? "on" : "off")}, {library.Apps.Count(a => a.BuiltAt is not null)} guides, " +
                $"{library.Apps.Count} apps on the list." + (appGuides.LoadProblem is { } problem ? $" The library couldn't be read: {problem}." : ""));
        }
        catch (OperationCanceledException) { }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or System.Text.Json.JsonException)
        {
            ErrorLog.Warn("App guides: couldn't read the guide library.", error);
        }
    }

    private void StopAppGuides()
    {
        conversation?.StopGuideResearch();
        appGuideWatch?.Dispose();
        appGuides?.Dispose();
    }

    /// <summary>The library, the app in front or reading up changed (on the UI thread).</summary>
    private void AppGuidesChanged()
    {
        if (closing || appGuides is null) return;
        if (appGuideWatch is not null) appGuideWatch.On = appGuides.On && Role == DeviceRole.Companion;
        if (openTab != CompanionTab.AppGuides) return;
        if (appGuides.Revision != AppGuidesPage.Drawn) RenderTab();
        else AppGuidesPage.RenderStatus();
    }

    /// <summary>The watch saw a game (or an app on the list) without a guide in front: Martlet reads up on it on its own, as the
    /// running conversation's job (which tells you in character), else here by itself. Never left to the model's answer, so the
    /// app is on the list from the start, with its guide when it is done.</summary>
    private void OfferAppGuide(AppGuideOfferCandidate offer)
    {
        if (closing || appGuides is not { } guides || guides.Offer() is not { } still || still.Key != offer.Key) return;
        guides.Offered(offer.Key);
        if (openConversation?.ReadUpOnItsOwn(guides, still) is { } start)
        {
            if (start.Job is null) ErrorLog.Info($"App guides: Martlet didn't read up on the app in front ({start.Refusal}).");
            return;
        }
        ReadUpOnItsOwnAsync(guides, still).Forget();
    }

    // No conversation runs: Martlet reads up on the game in front here; the page shows it, and its guide is ready for later.
    private async Task ReadUpOnItsOwnAsync(AppGuideService guides, AppGuideOfferCandidate offer)
    {
        AppGuideBuild built;
        try { built = await Task.Run(() => guides.BuildAsync(offer.Name, [], null, lifetime.Token, offer.Programs)); }
        catch (OperationCanceledException) { return; }
        catch (Exception error) when (AppGuideService.Refusal(error) is { } why)
        {
            ErrorLog.Warn($"App guides: couldn't keep a guide Martlet read up on by itself ({why}).");
            return;
        }
        LiveConversationController.LogBuild("reading up on the app in front", built);
    }

    /// <summary>Read more on the page: more pages of the app's wiki join its guide.</summary>
    private async Task ReadMoreFromPageAsync(AppGuideEntry entry)
    {
        appGuidesResult = $"Reading more about {entry.Name}...";
        if (openTab == CompanionTab.AppGuides) RenderTab();
        appGuidesResult = await ReadMoreAboutAsync(entry, "the App guides page");
        if (!closing && openTab == CompanionTab.AppGuides) RenderTab();
    }

    /// <summary>The speech bubble's Read more: more pages about the app it was offered for.</summary>
    private void ReadMoreFromBubble()
    {
        var key = captions.TakeOffer() ?? conversation?.ReadMoreTarget()?.Key;
        if (appGuides?.Library.Apps.FirstOrDefault(a => a.Key == key) is not { BuiltAt: not null } entry)
        {
            ErrorLog.Info("App guides: the speech bubble's Read more was pressed, but no app with a guide was offered.");
            return;
        }
        ErrorLog.Info("App guides: you pressed Read more on the speech bubble.");
        ReadMoreFromBubbleAsync(entry).Forget();
    }

    private async Task ReadMoreFromBubbleAsync(AppGuideEntry entry)
    {
        var said = await ReadMoreAboutAsync(entry, "the speech bubble");
        if (closing || said.Length == 0) return;
        ActionText.Text = said;
        if (openTab == CompanionTab.AppGuides) RenderTab();
    }

    /// <summary>Reads more about <paramref name="entry"/>: in the conversation that runs, as its background job (Martlet tells you
    /// in character when it's done), else here on its own. What happened in a few words ("" when Martlet is closing).</summary>
    private async Task<string> ReadMoreAboutAsync(AppGuideEntry entry, string where)
    {
        if (appGuides is not { On: true } guides) return "App guides are off.";
        if (guides.Building is { } busy) return $"Martlet is already reading up on {busy.Name}.";
        if (openConversation?.ReadMore(entry) is { } start)
            return start.Job is { } job ? $"Reading more about {entry.Name} in the conversation ({job.Id})."
                : $"Couldn't read more about {entry.Name}: {start.Message ?? start.Refusal}";
        var research = conversation?.GuideResearch;
        AppGuideBuild read;
        try
        {
            read = await Task.Run(() => research is not null ? research.ReadMoreNowAsync(entry.Key, null, lifetime.Token)
                : guides.ReadMoreAsync(entry.Key, null, null, null, lifetime.Token));
        }
        catch (OperationCanceledException) { return ""; }
        LiveConversationController.LogMore(where, read);
        return read is { Built: true, Pages: > 0 }
            ? $"Read {read.Pages} more page{(read.Pages == 1 ? "" : "s")} about {read.Name} ({read.Chunks} sections)."
            : $"Couldn't read more about {read.Name}: {read.Problem}.";
    }

    /// <summary>Read up now (or Read again): in the conversation that runs, as its background job, else here on its own.</summary>
    private async Task ReadUpFromPageAsync(AppGuideEntry entry)
    {
        if (appGuides is not { On: true } guides) return;
        if (guides.Building is { } busy)
        {
            appGuidesResult = $"Martlet is already reading up on {busy.Name}.";
            if (openTab == CompanionTab.AppGuides) RenderTab();
            return;
        }
        if (openConversation?.ReadUp(guides, entry.Name, entry.Sites) is { } start)
        {
            appGuidesResult = start.Job is { } job ? $"Reading up on {entry.Name} in the conversation ({job.Id})."
                : $"Couldn't start reading up: {start.Message ?? start.Refusal}";
            if (openTab == CompanionTab.AppGuides) RenderTab();
            return;
        }
        appGuidesResult = $"Reading up on {entry.Name}...";
        if (openTab == CompanionTab.AppGuides) RenderTab();
        AppGuideBuild built;
        try { built = await Task.Run(() => guides.BuildAsync(entry.Name, entry.Sites, null, lifetime.Token)); }
        catch (OperationCanceledException) { return; }
        catch (Exception error) when (AppGuideService.Refusal(error) is { } why)
        {
            ErrorLog.Warn($"App guides: couldn't keep a guide read from the App guides page ({why}).");
            appGuidesResult = $"Couldn't keep the guide about {entry.Name}: {why}.";
            if (!closing && openTab == CompanionTab.AppGuides) RenderTab();
            return;
        }
        LiveConversationController.LogBuild("the App guides page", built);
        appGuidesResult = built.Built
            ? $"Read up on {built.Name}: {built.Pages} page{(built.Pages == 1 ? "" : "s")}, {built.Chunks} sections."
            : $"Couldn't read up on {built.Name}: {built.Problem}.";
        if (!closing && openTab == CompanionTab.AppGuides) RenderTab();
    }
}
