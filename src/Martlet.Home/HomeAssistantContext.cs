using System.Text;
using Martlet.Core.Settings;

namespace Martlet.Home;

/// <summary>What happened with the smart home on one user turn, told to the persona so it can reply in its own voice.
/// Text quoted from Home Assistant is data, never instructions.</summary>
public static class HomeAssistantContext
{
    internal const string Label = "MARTLET_SMART_HOME";

    public static string? Handled(HomeCommandResult result, PromptSettings? prompts = null)
    {
        var details = new StringBuilder();
        if (result.Speech.Length > 0) details.Append("Its report: \"").Append(Quote(result.Speech)).Append("\". ");
        if (result.Succeeded.Count > 0) details.Append("Done: ").Append(Names(result.Succeeded)).Append(". ");
        if (result.Failed.Count > 0) details.Append("Failed: ").Append(Names(result.Failed)).Append(". ");
        return Wrap(prompts, PromptSettings.Fill(prompts, result.Kind switch
        {
            HomeResponseKind.ActionDone => PromptCatalog.HomeDone,
            HomeResponseKind.QueryAnswer => PromptCatalog.HomeAnswer,
            _ => PromptCatalog.HomeFailed
        }, ("details", details.ToString())));
    }

    /// <summary>For a turn that offers Home Assistant's own tools (its MCP server) instead of the Assist step.</summary>
    public static string? ToolsOffered(bool allowSensitive, PromptSettings? prompts = null) => Wrap(prompts,
        PromptSettings.Fill(prompts, PromptCatalog.HomeTools,
            ("locks", PromptSettings.Text(prompts, allowSensitive ? PromptCatalog.HomeLocksConfirm : PromptCatalog.HomeLocksOff))));

    public static string? NotRecognized(PromptSettings? prompts = null) => Wrap(prompts, PromptSettings.Fill(prompts, PromptCatalog.HomeNotRecognized));

    public static string? Blocked(PromptSettings? prompts = null) => Wrap(prompts, PromptSettings.Fill(prompts, PromptCatalog.HomeBlocked));

    public static string? Declined(PromptSettings? prompts = null) => Wrap(prompts, PromptSettings.Fill(prompts, PromptCatalog.HomeDeclined));

    public static string? Unreachable(PromptSettings? prompts = null) => Wrap(prompts, PromptSettings.Fill(prompts, PromptCatalog.HomeUnreachable));

    // An emptied note sends nothing; an emptied wrapper sends the note alone.
    private static string? Wrap(PromptSettings? prompts, string? body) =>
        body is null ? null : PromptSettings.Fill(prompts, PromptCatalog.HomeWrap, ("label", Label), ("body", body)) ?? body;

    private static string Names(IReadOnlyList<HomeTarget> targets) =>
        string.Join(", ", targets.Where(t => t.Name.Length > 0).Select(t => Quote(t.Name)).Distinct().Take(8));

    private static string Quote(string value) =>
        value.Replace("\"", "'", StringComparison.Ordinal).Replace(Label, "home", StringComparison.OrdinalIgnoreCase);
}
