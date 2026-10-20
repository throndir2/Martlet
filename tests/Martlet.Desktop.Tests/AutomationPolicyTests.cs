using Martlet.Mcp;

namespace Martlet.Desktop.Tests;

/// <summary>AutomationPolicy, the safety boundary of the ui_* tools: one line for each automation ID with its page and its effect,
/// and the same decisions as before the lists moved out of DesktopAutomation.</summary>
public sealed class AutomationPolicyTests
{
    [Theory]
    [InlineData("SafeClicks")]
    [InlineData("SafeClickPrefixes")]
    [InlineData("SafeValues")]
    [InlineData("SafeValuePrefixes")]
    public void EveryEntryIsOneIdWithItsPageAndEffect(string list)
    {
        var entries = list switch
        {
            "SafeClicks" => AutomationPolicy.SafeClicks,
            "SafeClickPrefixes" => AutomationPolicy.SafeClickPrefixes,
            "SafeValues" => AutomationPolicy.SafeValues,
            _ => AutomationPolicy.SafeValuePrefixes
        };
        Assert.NotEmpty(entries);
        Assert.All(entries, entry =>
        {
            Assert.Matches(@"^\S+$", entry.Id);
            Assert.False(string.IsNullOrWhiteSpace(entry.Page), entry.Id);
            Assert.False(string.IsNullOrWhiteSpace(entry.Effect), entry.Id);
        });
        var twice = entries.GroupBy(entry => entry.Id, StringComparer.Ordinal).Where(group => group.Count() > 1).Select(group => group.Key);
        Assert.Empty(twice);
    }

    [Theory]
    [InlineData("NavHome", true)]
    [InlineData("CompanionTab-Personality", true)]
    [InlineData("CheckInsOpenPool", true)]
    [InlineData("ThinkingRequestDetailClose", true)]
    [InlineData("LiveMessageContext-3", true)]
    [InlineData("MessageContextSection-history", true)]
    [InlineData("MessageContextClose", true)]
    [InlineData("MessageContextPlay", false)]
    [InlineData("MessageContextCopyAll", false)]
    [InlineData("LiveSend", false)]
    [InlineData("CheckInRun-c1", false)]
    [InlineData("ThinkingRequestsClear", false)]
    [InlineData("LiveStop", true)]
    [InlineData("NearbyFind", true)]
    [InlineData("FriendsCheck", false)]
    [InlineData("OpenAiCheck", false)]
    [InlineData("OpenAiCheckAddress", false)]
    [InlineData("PicturesCheck", false)]
    [InlineData("PicturesComfyConnect-0", false)]
    [InlineData("HostSignInSettings", false)]
    [InlineData("LiveJobCancel-think-1", false)]
    [InlineData("TrayPause", false)]
    [InlineData("TrayStopListening", false)]
    [InlineData("TrayStopWatching", false)]
    [InlineData("Help-CheckInsAbout", true)]
    [InlineData("Help-CheckInsAbout-Wiki", false)]
    public void ClicksThatChangeNothingAreSafe(string id, bool safe) => Assert.Equal(safe, AutomationPolicy.IsSafeClick(id));

    /// <summary>The click lists keep the policy's own rule: no entry says it writes a file, contacts a computer by its address,
    /// sends something or pauses Martlet.</summary>
    [Fact]
    public void NoSafeClickSaysItWritesSendsOrPauses()
    {
        var forbidden = new System.Text.RegularExpressions.Regex(
            @"\bkeeps\b[^.;]*\.json|\bwrites (?!nothing)|\bsends (?!nothing|and saves nothing)|\bGET [</]|\bsigned\b|\bPause Martlet\b|\bStop listening\b|\bStop watching\b|\bCancel button of a background job\b");
        var broken = AutomationPolicy.SafeClicks.Concat(AutomationPolicy.SafeClickPrefixes)
            .Where(entry => forbidden.IsMatch(entry.Effect)).Select(entry => $"{entry.Id}: {entry.Effect}").ToArray();
        Assert.True(broken.Length == 0, "These safe clicks break AutomationPolicy's rule:\n" + string.Join("\n", broken));
    }

    [Theory]
    [InlineData("21:04:11.532 WARN This PC · App: Could not read C:\\Users\\ana\\AppData\\Local\\Martlet\\x.json", "21:04:11.532 WARN This PC · App: Could not read [path]")]
    [InlineData("ana@example.net (authentik). Shares gpu-box", "[email] (authentik). Shares gpu-box")]
    [InlineData("Failed at \\\\nas\\share\\file.txt and /home/ana/.martlet", "Failed at [path] and [path]")]
    [InlineData("http://192.168.1.20:8080/v1 answered at 21:04", "http://192.168.1.20:8080/v1 answered at 21:04")]
    public void SafeValuesHideEmailAddressesAndFilePaths(string shown, string returned) => Assert.Equal(returned, AutomationPolicy.Redact(shown));

    [Theory]
    [InlineData("CheckInsNow", true)]
    [InlineData("CheckInStatus-c1", true)]
    [InlineData("ThinkingRequestInputState", true)]
    [InlineData("ApiKeyRow-AbC", true)]
    [InlineData("ExternalEndpointsStatus", true)]
    [InlineData("ExternalEndpoint-openrouter.ai", true)]
    [InlineData("ThinkingPoolRunsOnLabel-touch-zones", true)]
    [InlineData("MessageContextSummary", true)]
    [InlineData("MessageContextSize-instructions", true)]
    [InlineData("MessageContextPart-notes-1", true)]
    [InlineData("MessageContextText-history-0", false)]
    [InlineData("MessageContextText-message-0", false)]
    [InlineData("MessageContextPicture", false)]
    [InlineData("ThinkingRequestTopic", false)]
    [InlineData("ThinkingRequestInput", false)]
    [InlineData("ThinkingRequestOutput", false)]
    [InlineData("CheckInPrompt-c1", false)]
    [InlineData("Help-CheckInsAbout-Card", true)]
    [InlineData("Help-CheckInsAbout-Wiki", true)]
    public void OnlyStatusTextIsReturned(string id, bool safe) => Assert.Equal(safe, AutomationPolicy.IsSafeValue(id));
}
