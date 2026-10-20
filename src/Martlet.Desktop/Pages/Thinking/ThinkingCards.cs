using System.Windows;
using System.Windows.Controls;
using Martlet.Core.Settings;
using Martlet.Providers;

namespace Martlet.Desktop;

/// <summary>The shared cards of Companion › Thinking, the UI only: Model app and Your model app on this PC
/// (ThinkingCards.LocalServer.cs), Ollama on this PC (ThinkingCards.LocalThinking.cs), If Thinking fails
/// (ThinkingCards.Fallback.cs) and the FIXTURE situations card. What they need from the app is
/// <see cref="IThinkingCardsServices"/>. MainWindow keeps one instance for the window's life, so what is typed in a box stays
/// while the page draws again.</summary>
internal sealed partial class ThinkingCards(IThinkingCardsServices services)
{
    /// <summary>The Model app picker's choice for an app typed by its address.</summary>
    internal const string AnotherAddress = "Another address on this PC";

    private static string Lowered(string text) => text.Length == 0 ? text : char.ToLowerInvariant(text[0]) + text[1..];

    /// <summary>FIXTURE (MARTLET_SIMULATE_SITUATION): Companion › Thinking's buttons that start or end a simulated game and make the
    /// Thinking host simulated away or back. They change nothing saved.</summary>
    internal Border? SituationFixture()
    {
        if (services.SimulatedSituation is not { } fixture) return null;
        var game = PageButton(fixture.Game is null ? "Simulate a game" : "End the simulated game", () =>
        {
            fixture.Game = fixture.Game is null ? SimulatedSituation.GameName : null;
            services.ObserveSituation();
            services.Redraw();
        }, id: "SituationSimulateGame");
        var away = PageButton(fixture.Away ? "The simulated host answers again" : "Simulate the Thinking host away", () =>
        {
            fixture.Away = !fixture.Away;
            services.ObserveSituation();
            services.Redraw();
        }, id: "SituationSimulateAway");
        return Card(Heading("FIXTURE: situations"),
            Note($"{SimulatedSituation.Variable} is set: {fixture.Describe()}, and nothing is played, contacted or saved.", new Thickness(0, 0, 0, 0)),
            Row(game, away));
    }

    /// <summary>The model app found on this PC that the Model app picker shows for <see cref="MainWindow.LocalApp.Other"/>: the
    /// one picked, else the one Thinking uses, else the first found; null for an app typed by its address.</summary>
    private LocalModelServer? PickedLocalServer(SetupRoute? route)
    {
        var found = services.OtherLocalServers();
        var wanted = services.LocalServerPicked ?? (MainWindow.IsLocalServer(route) ? route!.Origin : null);
        if (wanted == AnotherAddress) return null;
        return found.FirstOrDefault(s => s.ChatCompletionsBaseUrl == wanted) ?? (wanted is null ? found.FirstOrDefault() : null);
    }

    /// <summary>Which app runs the model on this PC, as an option picker (<c>Picker-LocalApp-&lt;key&gt;</c>): Ollama, each other
    /// model app found here, and an app typed by its address. Choosing one shows its card below.</summary>
    internal Border LocalApp(SetupRoute? route, MainWindow.LocalApp chosen)
    {
        var others = services.OtherLocalServers();
        var options = JobOptions.LocalApps(others, services.LookingForLocalServers && services.LocalServers is null,
            !Prerequisites.IsMissing(Prerequisites.Ollama), MainWindow.IsLocalOllama(route), MainWindow.IsLocalServer(route) ? route!.Origin : null);
        var shown = chosen == MainWindow.LocalApp.Ollama ? JobOptions.OllamaApp
            : PickedLocalServer(route) is { } server ? JobOptions.AppKey(server) : JobOptions.AddressApp;
        // The chosen app's own card below is its details.
        return services.OptionPicker("LocalApp", "Model app", null, options, shown, key =>
        {
            services.LocalApp = key == JobOptions.OllamaApp ? MainWindow.LocalApp.Ollama : MainWindow.LocalApp.Other;
            if (key == JobOptions.AddressApp) services.LocalServerPicked = AnotherAddress;
            else if (others.FirstOrDefault(s => JobOptions.AppKey(s) == key) is { } picked) services.LocalServerPicked = picked.ChatCompletionsBaseUrl;
        }, details: false);
    }
}
