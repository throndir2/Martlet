using Martlet.Core.Settings;
using Martlet.Core.Text;

namespace Martlet.Desktop.Tests;

/// <summary>Companion › Prompts shows one sentence for each state line (issue #995): what each prompt is for sits behind the "?"
/// after its title.</summary>
public sealed class PromptsTabTextTests
{
    [Fact]
    public void PromptStatesAreOneSentence()
    {
        var prompt = PromptCatalog.All[0];
        var builtIn = PromptsPageModel.PromptState(prompt, prompt.Default, null);
        Assert.StartsWith("Built-in text, about ", builtIn, StringComparison.Ordinal);
        Assert.Equal(1, Sentences.Count(builtIn));

        var saving = PromptsPageModel.PromptState(prompt, "My own text.", null);
        Assert.StartsWith("Edited, about ", saving, StringComparison.Ordinal);
        Assert.EndsWith(", saving.", saving, StringComparison.Ordinal);
        Assert.Equal(1, Sentences.Count(saving));

        Assert.Equal("Empty: nothing is sent for this prompt, saving.", PromptsPageModel.PromptState(prompt, "  ", null));
    }

    [Fact]
    public void PromptSummariesAreOneSentence()
    {
        Assert.Equal(1, Sentences.Count(PromptsPageModel.DescribePromptTokens(PromptCatalog.All.Select(p => p.Default))));
        var edited = PromptSettings.Normalize(new Dictionary<string, string>(StringComparer.Ordinal)
        {
            [PromptCatalog.All[0].Id] = "My own text.",
            [PromptCatalog.All[1].Id] = ""
        });
        var summary = PromptsPageModel.DescribePrompts(edited);
        Assert.Contains("edited", summary, StringComparison.Ordinal);
        Assert.Equal(1, Sentences.Count(summary));
    }
}
