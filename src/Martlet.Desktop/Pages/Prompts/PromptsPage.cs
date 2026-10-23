using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using Martlet.Core.Settings;
using Martlet.Presentation;
using static Martlet.Desktop.PromptsPageModel;

namespace Martlet.Desktop;

/// <summary>Companion › Prompts, the UI only: every internal prompt Martlet sends to the Thinking model (persona wrapper, styles,
/// reply length, always listening, tools, voices, lore, memory, screen and camera glances, remembering, learning names and the
/// smart home notes), each editable. Saved edits replace the built-in text everywhere it is used. What each line says is in
/// <see cref="PromptsPageModel"/>; what the page needs from the app is <see cref="IPromptsPageServices"/>.</summary>
internal sealed class PromptsPage(IPromptsPageServices services)
{
    /// <summary>Draws the page's cards into <paramref name="page"/>.</summary>
    internal void Render(Panel page)
    {
        var saved = services.Saved;
        // What one reply sends: the few parts every reply carries, and the skills the last reply carried because a feature is on.
        var (name, persona) = services.Persona;
        var always = Note(DescribeEveryReply(saved, services.Skills, name, persona), new Thickness(0, 2, 0, 0));
        AutomationProperties.SetAutomationId(always, "PromptsReplyAlways");
        var attached = Note(DescribeAttached(services.LastReply, services.Skills), new Thickness(0, 6, 0, 0));
        AutomationProperties.SetAutomationId(attached, "PromptsReplyAttached");
        page.Children.Add(Card(Heading("What one reply sends"), always, attached,
            HelpTip.Explain("Each rule a reply follows is a skill: a reply gets it only while its feature is on, the director loads it " +
                "when it needs it, and a pinned skill goes with every reply. The prompts on this page are the requests themselves and " +
                "the notes around what Martlet knows; each group below says when Martlet sends it.", new Thickness(0, 6, 0, 0),
                "PromptsReply", "what one reply sends", wiki: "Personality-and-Characters#skills"),
            Row(PageButton("Open Skills", services.OpenSkills, link: true, id: "PromptsOpenSkills"))));

        var now = Note(DescribePrompts(saved), new Thickness(0, 2, 0, 0));
        AutomationProperties.SetAutomationId(now, "PromptsNow");
        var total = Note(DescribePromptTokens(PromptCatalog.All.Select(p => PromptSettings.Text(saved, p.Id))), new Thickness(0, 4, 0, 0));
        AutomationProperties.SetAutomationId(total, "PromptsTokens");
        page.Children.Add(Card(Heading("Now"), now, total));

        page.Children.Add(Card(Heading("How prompts work"),
            HelpTip.Explain("Martlet builds each request from these prompts plus your persona, matching lore, remembered facts and the recent " +
                "conversation. Words in braces, such as {name}, are filled in by Martlet when the prompt is sent; keep them where you " +
                "want that text. Empty a prompt to send nothing for it. Martlet reads the answers to Remembering and Learning names, so " +
                "keep their line formats. Edits save as you type; reload an open conversation to use them.", new Thickness(0, 0, 0, 0), "Prompts", "prompts",
                wiki: "Personality-and-Characters#prompts"),
            HelpTip.Explain("Token counts are Martlet's own estimate. It counts about one token for every three bytes of text: the same rule it " +
                "uses to keep requests within the model's limit, so the model itself may count somewhat fewer. They count each prompt as " +
                "written; the text filled in for words in braces adds to it when sent.", new Thickness(0, 6, 0, 0), "PromptTokens", "token counts",
                wiki: "Personality-and-Characters#token-counts")));

        var boxes = new Dictionary<string, TextBox>(StringComparer.Ordinal);
        var states = new List<Action>();
        // There is no Save button: edits save a moment after typing stops, into the newest saved settings.
        var autoSave = new AutoSave(() => services.SaveAsync(boxes.ToDictionary(b => b.Key, b => b.Value.Text, StringComparer.Ordinal), prompts =>
        {
            saved = prompts;
            now.Text = DescribePrompts(prompts);
            foreach (var show in states) show();
        }));
        services.UseAutoSave(autoSave);
        var groupNumber = 0;
        foreach (var group in PromptCatalog.All.GroupBy(p => p.Group))
        {
            var where = Note(PromptCatalog.GroupWhere(group.Key), new Thickness(0, 2, 0, 0));
            AutomationProperties.SetAutomationId(where, $"PromptGroupWhere-{++groupNumber}");
            var children = new List<UIElement> { Heading(group.Key), where };
            foreach (var prompt in group)
            {
                var title = new TextBlock { Text = prompt.Title, FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 12, 0, 0), TextWrapping = TextWrapping.Wrap };
                var state = new TextBlock { TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 2, 0, 0) };
                AutomationProperties.SetAutomationId(state, "PromptState-" + prompt.Id);
                var help = prompt.Help + (prompt.Placeholders.Count == 0 ? ""
                    : " Fills in: " + string.Join(", ", prompt.Placeholders.Select(p => "{" + p + "}")) + ".");
                var box = new TextBox
                {
                    Text = PromptSettings.Text(saved, prompt.Id), AcceptsReturn = true, AcceptsTab = false, TextWrapping = TextWrapping.Wrap,
                    MaxLength = PromptSettings.MaximumTextCharacters, MinHeight = 56, MaxHeight = 260,
                    VerticalScrollBarVisibility = ScrollBarVisibility.Auto, Margin = new Thickness(0, 6, 0, 0)
                };
                AutomationProperties.SetAutomationId(box, "Prompt-" + prompt.Id);
                AutomationProperties.SetName(box, prompt.Title + " prompt");
                AutomationProperties.SetHelpText(box, help);
                InputHint.SetText(box, "Type this prompt's text, or choose Use built-in text");
                void ShowState() => state.Text = PromptState(prompt, box.Text, saved);
                ShowState();
                states.Add(ShowState);
                box.TextChanged += (_, _) =>
                {
                    services.MarkEdited();
                    ShowState();
                    total.Text = DescribePromptTokens(boxes.Values.Select(b => b.Text));
                    autoSave.Changed();
                };
                boxes[prompt.Id] = box;
                var reset = PageButton("Use built-in text", () => box.Text = prompt.Default, link: true, id: "PromptReset-" + prompt.Id);
                // What the prompt is for and its words in braces sit behind a "?" after its title (issue #995).
                children.Add(HelpTip.After(title, prompt.Title, help, "Prompt-" + prompt.Id, wiki: "Personality-and-Characters#prompts"));
                children.Add(state);
                children.Add(box);
                children.Add(Row(reset));
            }
            page.Children.Add(Card([.. children]));
        }

        var defaults = PageButton("Use all built-in prompts", () =>
        {
            foreach (var (id, box) in boxes) box.Text = PromptCatalog.Default(id);
            autoSave.SaveNowAsync().Forget();
        }, id: "PromptsDefaults");
        page.Children.Add(Card(Heading("Built-in prompts"),
            HelpTip.Explain("Your edits save on their own and apply to every new reply, glance and memory check. Reload an open conversation " +
                "to use them.", new Thickness(0, 0, 0, 4), "PromptsBuiltIn", "built-in prompts", wiki: "Personality-and-Characters#prompts"),
            Row(defaults)));
    }
}
