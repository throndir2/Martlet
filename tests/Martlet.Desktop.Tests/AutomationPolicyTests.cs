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
    [InlineData("Help-CheckInsAbout", true)]
    [InlineData("Help-CheckInsAbout-Wiki", false)]
    public void ClicksThatChangeNothingAreSafe(string id, bool safe) => Assert.Equal(safe, AutomationPolicy.IsSafeClick(id));

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
