using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using Martlet.Core.Settings;

namespace Martlet.Desktop;

/// <summary>The Now card of a job page (<c>SetupJobNow-&lt;page&gt;</c>).</summary>
internal sealed partial class JobPage
{
    /// <summary>What the job uses now, first on every setup page, with any problem that stops it.</summary>
    private Border NowCard(CompanionTab section, HostJob job, SetupRoute? route)
    {
        var problem = services.Problem(job.Job);
        var status = JobPageModel.NowStatus(section, route, route is null ? null : services.RouteName(route),
            services.NetworkHost(job.Job) is not null);
        var now = new StackPanel();
        now.Children.Add(Heading("Now"));
        var nowText = new TextBlock { Text = status, FontSize = 15, TextWrapping = TextWrapping.Wrap };
        AutomationProperties.SetAutomationId(nowText, "SetupJobNow-" + section);
        now.Children.Add(nowText);
        if (section == CompanionTab.Thinking)
        {
            now.Children.Add(services.TextModelLine(route));
            // What Martlet knows the model takes on this route, and Test tools for an OpenAI-compatible endpoint.
            if (route is not null)
            {
                var known = Note(MainWindow.ThinkingAbilitiesText(route, services.SavedModelAbilities()), new Thickness(0, 6, 0, 0));
                AutomationProperties.SetAutomationId(known, "ThinkingAbilities");
                now.Children.Add(known);
            }
            foreach (var element in services.ToolsTestControls(route)) now.Children.Add(element);
            // Where live Thinking goes in the situation now (Normal, While gaming, Host away).
            now.Children.Add(services.SituationNowLine());
        }
        if (services.NetworkJobNote(job.Job) is { } network)
        {
            var line = new TextBlock { Text = network, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 6, 0, 0) };
            line.SetResourceReference(FrameworkElement.StyleProperty, "Muted");
            AutomationProperties.SetAutomationId(line, "SetupJobNetwork-" + section);
            now.Children.Add(line);
        }
        if (section == CompanionTab.Listening && services.StandInLine(route) is { } standIn) now.Children.Add(standIn);
        if (problem is not null)
        {
            // The problem shows; the "?" also has what it means for you.
            var warning = HelpTip.Explain($"Needs attention: {problem.Problem} {problem.Effect}".Trim(), new Thickness(0, 6, 0, 0),
                "SetupJobProblem-" + section, job.Job, wiki: "Multiple-Computers#when-no-machine-can-do-a-job");
            warning.SetResourceReference(TextBlock.ForegroundProperty, "WarningBrush");
            now.Children.Add(warning);
        }
        if (section == CompanionTab.Listening && services.AudioMissing(output: false))
        {
            var mic = new TextBlock { Text = services.Settings?.Audio?.Input.EndpointId is null
                    ? "No microphone found: plug one in or type instead."
                    : "Your chosen microphone isn't connected: plug it in, pick another or type instead.",
                TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 6, 0, 0) };
            mic.SetResourceReference(TextBlock.ForegroundProperty, "WarningBrush");
            AutomationProperties.SetAutomationId(mic, "ListeningMicMissing");
            now.Children.Add(mic);
        }
        return Card(now);
    }
}
