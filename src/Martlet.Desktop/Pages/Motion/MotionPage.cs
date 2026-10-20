using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using Martlet.Core.Cluster;
using Martlet.HostClient;

namespace Martlet.Desktop;

/// <summary>
/// Companion › Motion (an optional extra, docs/MOTION_HOST.md#the-motion-pool), the UI only: where Martlet makes new body motions
/// for VRM characters, on the <c>motion</c> host role (Tencent's HY-Motion 1.0). In the standard order: Now (whether and where
/// Martlet makes motions, and whether the character shown is a VRM, the only kind they play on); the motion pool (the shared list
/// control, which MainWindow builds: <paramref name="poolList"/>; the computers that make motions, in order, with no separate on/off
/// switch: with none on, motion is off); each member's Settings say where Motion stands there and set the role up with the same
/// martlet-host flow as every role (its own dialog shows the model and Tencent's license terms); then what Motion is. Kept motions
/// and Make a motion stay on Companion › Emotes and motions. What each line says is in <see cref="MotionPageModel"/>; what the page
/// needs from the app is <see cref="IMotionPageServices"/>. Automation IDs: <c>MotionNow</c>, <c>MotionCharacter</c>,
/// <c>MotionOpenEmotes</c>, the list's <c>Pool-motion-...</c> (and per member <c>Pool-motion-State-&lt;key&gt;</c> and
/// <c>Pool-motion-SetUp-&lt;key&gt;</c>) and <c>MotionAbout</c>.
/// </summary>
internal sealed class MotionPage(IMotionPageServices services, Func<PoolListOptions, UIElement> poolList)
{
    /// <summary>Draws the page's cards into <paramref name="page"/>.</summary>
    internal void Render(Panel page)
    {
        services.EnsurePool();
        var model = new MotionPageModel(services);

        // ---------- Now ----------
        var nowText = new TextBlock
        {
            Text = model.NowText,
            FontSize = 15, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 6)
        };
        AutomationProperties.SetAutomationId(nowText, "MotionNow");
        var character = Note(model.CharacterText, new Thickness(0, 0, 0, 4));
        if (model.CharacterWarning) character.SetResourceReference(TextBlock.ForegroundProperty, "WarningBrush");
        AutomationProperties.SetAutomationId(character, "MotionCharacter");
        var emotes = PageButton("Kept motions and Make a motion", services.OpenEmotes, link: true, id: "MotionOpenEmotes");
        page.Children.Add(Card(Heading("Now"), nowText,
            HelpTip.Explain("While you talk, Martlet can move a VRM character's whole body: cheer, clap, bow deeply or any movement it " +
                "thinks of. It makes a new motion in the background and plays it when it's ready, and your words never wait for it. It " +
                "keeps each motion, so your VRM characters do it again at once later, on all your computers.", new Thickness(0, 0, 0, 4),
                "MotionWhat", "body motions", wiki: "The-Character#body-motions"),
            character, Row(emotes)));

        // ---------- the motion pool: the computers that make motions, in order ----------
        page.Children.Add(poolList(new PoolListOptions
        {
            Area = PoolAreas.Motion,
            Heading = "Where Martlet makes motions",
            Intro = "The computers that make body motions, in order. A motion goes to the first one that isn't making another motion; " +
                "when all are busy it waits for the first to finish. With none on, Martlet makes no new motions; the motions it kept " +
                "still play. Open a computer's Settings to set Motion up there. Your other computers follow this list.",
            Wiki = "The-Character#body-motions",
            Status = model.MemberStatus,
            Settings = member => MemberSettings(model, member),
            CanAdd = services.CanAdd,
            Saved = (before, after) =>
            {
                // A computer added that doesn't run Motion yet shows its Settings, where Set up is.
                foreach (var added in after.Members.Where(m => before.Find(m.Key) is null))
                    if (model.MemberHost(added) is not { } host || !services.Offers(host, HostRoles.Motion))
                        services.ShowSettings(added.Key);
                return Task.CompletedTask;
            }
        }));

        // ---------- what Motion is ----------
        var about = HelpTip.Explain(MotionPageModel.About, new Thickness(0, 0, 0, 0), "MotionAbout", "the Motion role", wiki: "The-Character#body-motions");
        AutomationProperties.SetAutomationId(about, "MotionAbout");
        page.Children.Add(Card(Heading("What Motion is"), about));
    }

    /// <summary>A motion pool member's Settings (automation IDs <c>Pool-motion-&lt;part&gt;-&lt;member key&gt;</c>): where Motion
    /// stands there (State) and Set up Motion here (SetUp) where it doesn't run yet.</summary>
    private UIElement? MemberSettings(MotionPageModel model, PoolMember member)
    {
        var id = $"Pool-{PoolAreas.Motion.Id}-";
        var settings = model.Settings(member);
        var panel = new StackPanel();
        var line = Note(settings.Text, new Thickness(0, 0, 0, 4));
        if (settings.Warning) line.SetResourceReference(TextBlock.ForegroundProperty, "WarningBrush");
        AutomationProperties.SetAutomationId(line, id + "State-" + member.Key);
        panel.Children.Add(line);
        if (settings.Ready) return panel;
        var setUp = PageButton(settings.Pending ? "Setting up..." : "Set up Motion here", () => services.SetUp(settings.SetUpOn, settings.Key),
            primary: true, id: id + "SetUp-" + member.Key);
        setUp.IsEnabled = settings.CanSetUp;
        if (settings.Cannot is { } cannot)
        {
            setUp.ToolTip = cannot;
            ToolTipService.SetShowOnDisabled(setUp, true);
            AutomationProperties.SetHelpText(setUp, cannot);
        }
        panel.Children.Add(Row(setUp));
        return panel;
    }
}
