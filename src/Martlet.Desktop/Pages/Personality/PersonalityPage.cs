using System.Windows;
using System.Windows.Controls;

namespace Martlet.Desktop;

/// <summary>Companion › Personality, the UI only: the active persona, Edit personality, the Profiles card, character card import and
/// the link to Prompts. It has no decisions of its own, so it has no page model; what it needs from the app is
/// <see cref="IPersonalityPageServices"/>.</summary>
internal sealed class PersonalityPage(IPersonalityPageServices services)
{
    /// <summary>Draws the page's cards into <paramref name="page"/>.</summary>
    internal void Render(Panel page)
    {
        var companion = services.Settings?.Companion;
        var persona = companion?.ActivePersona;
        var count = companion?.Personas.Count ?? 1;
        page.Children.Add(PageNowCard(persona is null
            ? "Default persona."
            : $"{persona.Name}{(count > 1 ? $", one of {count} personas" : "")}.", null));

        page.Children.Add(Card(Heading("Personas"),
            HelpTip.Explain("Create, edit or switch personas. Changes save on their own, and the next message uses the chosen persona.",
                new Thickness(0, 0, 0, 8), "PersonasAbout", "personas", wiki: "Personality-and-Characters#personas"),
            Row(PageButton("Edit personality", () => services.OpenPersonality(importCard: false), primary: true, id: "OpenCompanion"))));

        page.Children.Add(services.ProfilesLinkCard());

        page.Children.Add(Card(Heading("Character cards"),
            Note("Import a PNG, JSON or CHARX character card to create a persona.", new Thickness(0, 0, 0, 8)),
            Row(PageButton("Import a character card", () => services.OpenPersonality(importCard: true), id: "ImportCharacterCard"))));

        page.Children.Add(Card(Heading("Prompts"),
            HelpTip.Explain("See and edit Martlet's own instructions around the persona. The persona is wrapped in them, along with lore, " +
                "memory, reply length and more.", new Thickness(0, 0, 0, 8), "PromptsAround", "prompts",
                wiki: "Personality-and-Characters#prompts"),
            Row(PageButton("Edit prompts", services.OpenPrompts, id: "OpenPrompts"))));
    }
}
