using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using Martlet.Core.Settings;
using Martlet.Presentation;
using static Martlet.Desktop.SkillsPageModel;

namespace Martlet.Desktop;

/// <summary>Companion › Skills, the UI only (docs/CONVERSATION.md#skills): every skill, built in or the user's own, with its name,
/// when it helps, its text and whether it is pinned (every reply gets it). Edits save as you type. What each line says is in
/// <see cref="SkillsPageModel"/>; what the page needs from the app is <see cref="ISkillsPageServices"/>.</summary>
internal sealed class SkillsPage(ISkillsPageServices services)
{
    /// <summary>Draws the page's cards into <paramref name="page"/>.</summary>
    internal void Render(Panel page)
    {
        var saved = services.Saved;
        var now = Note(DescribeSkills(saved), new Thickness(0, 2, 0, 0));
        AutomationProperties.SetAutomationId(now, "SkillsNow");
        page.Children.Add(Card(Heading("Now"), now));
        page.Children.Add(Card(Heading("How skills work"),
            HelpTip.Explain("A skill is a short set of instructions Martlet uses only when it needs it, so every reply stays short and " +
                "quick. A pinned skill goes with every reply. A skill that explains tools goes with a reply only while that reply is " +
                "offered its tools. Your own skills and any skill can be edited; edits save as you type and are the same on all your " +
                "computers. Empty a skill's text to send nothing for it.", new Thickness(0, 0, 0, 0), "Skills", "skills",
                wiki: "Personality-and-Characters#skills")));

        var editors = new List<(Skill Original, TextBox Name, TextBox UseWhen, TextBox Body, CheckBox Pinned)>();
        var states = new List<Action>();
        IReadOnlyList<Skill> Typed() =>
        [
            .. editors.Select(e => e.Original with
            {
                Name = e.Name.Text.Trim(), UseWhen = e.UseWhen.Text.Replace("\r\n", "\n", StringComparison.Ordinal).Trim(),
                Body = e.Body.Text.Replace("\r\n", "\n", StringComparison.Ordinal), Pinned = e.Pinned.IsChecked == true
            })
        ];
        // There is no Save button: edits save a moment after typing stops, into the newest saved settings.
        var autoSave = new AutoSave(() => services.SaveAsync(Typed(), skills =>
        {
            saved = skills;
            now.Text = DescribeSkills(skills);
            foreach (var show in states) show();
        }));
        services.UseAutoSave(autoSave);
        void Changed()
        {
            services.MarkEdited();
            foreach (var show in states) show();
            autoSave.Changed();
        }
        // Adding or deleting a skill saves at once and draws the page again.
        async Task SaveAndRefreshAsync(IReadOnlyList<Skill> skills)
        {
            while (!await services.SaveAsync(skills, s => saved = s)) await Task.Delay(200);
            services.Refresh();
        }

        foreach (var skill in SkillSettings.All(saved))
        {
            var key = skill.Key;
            TextBox Box(string text, string id, string name, int max, bool lines)
            {
                var box = new TextBox
                {
                    Text = text, AcceptsReturn = lines, TextWrapping = TextWrapping.Wrap, MaxLength = max, Margin = new Thickness(0, 6, 0, 0),
                    MinHeight = lines ? 56 : 0, MaxHeight = lines ? 260 : double.PositiveInfinity,
                    VerticalScrollBarVisibility = lines ? ScrollBarVisibility.Auto : ScrollBarVisibility.Disabled
                };
                AutomationProperties.SetAutomationId(box, id);
                AutomationProperties.SetName(box, name);
                box.TextChanged += (_, _) => Changed();
                return box;
            }
            var name = Box(skill.Name, "SkillName-" + key, "Skill name", SkillSettings.MaximumNameCharacters, false);
            InputHint.SetText(name, "The skill's name");
            var useWhen = Box(skill.UseWhen, "SkillUseWhen-" + key, "When it helps", SkillSettings.MaximumUseWhenCharacters, false);
            InputHint.SetText(useWhen, "When it helps, in one line, such as: When the user asks for a recipe.");
            var body = Box(skill.Body, "Skill-" + key, "Skill text", PromptSettings.MaximumTextCharacters, true);
            InputHint.SetText(body, "The instructions, written to the character");
            var pinned = new CheckBox { Content = "Pinned: every reply gets it", IsChecked = skill.Pinned, Margin = new Thickness(0, 8, 0, 0) };
            AutomationProperties.SetAutomationId(pinned, "SkillPinned-" + key);
            pinned.Checked += (_, _) => Changed();
            pinned.Unchecked += (_, _) => Changed();
            var state = new TextBlock { TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 2, 0, 0) };
            AutomationProperties.SetAutomationId(state, "SkillState-" + key);
            var editor = (skill, name, useWhen, body, pinned);
            editors.Add(editor);
            void ShowState() => state.Text = SkillState(Typed().First(s => s.Key == key), saved);
            ShowState();
            states.Add(ShowState);
            var title = new TextBlock { Text = skill.Name, FontWeight = FontWeights.SemiBold, TextWrapping = TextWrapping.Wrap };
            var button = SkillCatalog.Find(key) is { } built
                ? PageButton("Use built-in skill", () =>
                {
                    name.Text = built.Name;
                    useWhen.Text = built.UseWhen;
                    body.Text = built.Body;
                    pinned.IsChecked = built.Pinned;
                }, link: true, id: "SkillReset-" + key)
                : PageButton("Delete this skill", () =>
                {
                    editors.Remove(editor);
                    SaveAndRefreshAsync(Typed()).Forget();
                }, link: true, id: "SkillDelete-" + key);
            page.Children.Add(Card(title, state, name, useWhen, body, pinned, Row(button)));
        }

        var add = PageButton("Add a skill", () =>
        {
            var typed = Typed();
            SaveAndRefreshAsync([.. typed, new Skill { Key = NewKey(typed), Name = "New skill", UseWhen = "", Body = "" }]).Forget();
        }, id: "SkillsAdd");
        page.Children.Add(Card(Heading("Your own skills"),
            HelpTip.Explain($"Add up to {SkillSettings.MaximumSkills} skills in all. A new skill starts empty, so it sends nothing until " +
                "you write it.", new Thickness(0, 0, 0, 4), "SkillsOwn", "your own skills", wiki: "Personality-and-Characters#skills"),
            Row(add)));
    }
}
