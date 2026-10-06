using NetCord;
using NetCord.Rest;

namespace Martlet.Discord;

/// <summary>Every Martlet slash command in one list. Features (text, voice, companion) add their commands here; on each Ready the
/// bot registers the whole list with one bulk overwrite, so no feature replaces another's commands, and each interaction goes to
/// the handler of the command with its name.</summary>
public sealed class DiscordCommands
{
    private readonly Lock gate = new();
    private readonly Dictionary<string, (SlashCommandProperties Command, Func<SlashCommandInteraction, ValueTask> Handler)> commands =
        new(StringComparer.Ordinal);

    /// <summary>A slash command for both installs (a server and a person's own account), usable in servers, the bot's DMs and
    /// other DMs and group DMs.</summary>
    public static SlashCommandProperties Slash(string name, string description, params ApplicationCommandOptionProperties[] options) =>
        new(name, description)
        {
            Options = options,
            IntegrationTypes = [ApplicationIntegrationType.GuildInstall, ApplicationIntegrationType.UserInstall],
            Contexts = [InteractionContextType.Guild, InteractionContextType.BotDMChannel, InteractionContextType.DMChannel]
        };

    /// <summary>Adds a command, replacing any earlier one with the same name. Takes effect at the next registration (Ready).</summary>
    public void Add(SlashCommandProperties command, Func<SlashCommandInteraction, ValueTask> handler)
    {
        ArgumentNullException.ThrowIfNull(command);
        ArgumentNullException.ThrowIfNull(handler);
        lock (gate) commands[command.Name] = (command, handler);
    }

    public IReadOnlyList<string> Names { get { lock (gate) return [.. commands.Keys.Order(StringComparer.Ordinal)]; } }

    internal IReadOnlyList<SlashCommandProperties> Properties
    {
        get { lock (gate) return [.. commands.Values.Select(entry => entry.Command).OrderBy(command => command.Name, StringComparer.Ordinal)]; }
    }

    /// <summary>Replaces the application's global commands with exactly this list.</summary>
    public async Task RegisterAsync(RestClient rest, ulong applicationId, CancellationToken token = default)
    {
        var all = Properties;
        await rest.BulkOverwriteGlobalApplicationCommandsAsync(applicationId, all, cancellationToken: token).ConfigureAwait(false);
    }

    /// <summary>Runs the handler for a slash command; false when no feature registered it.</summary>
    public async ValueTask<bool> HandleAsync(Interaction interaction)
    {
        if (interaction is not SlashCommandInteraction slash) return false;
        Func<SlashCommandInteraction, ValueTask>? handler;
        lock (gate) handler = commands.TryGetValue(slash.Data.Name, out var entry) ? entry.Handler : null;
        if (handler is null) return false;
        await handler(slash).ConfigureAwait(false);
        return true;
    }

    /// <summary>The string value of a top-level (or subcommand) option, or null.</summary>
    public static string? Option(SlashCommandInteraction interaction, string name)
    {
        foreach (var option in interaction.Data.Options)
        {
            if (option.Name == name) return option.Value;
            if (option.Options is { } nested && nested.FirstOrDefault(inner => inner.Name == name) is { } found) return found.Value;
        }
        return null;
    }

    /// <summary>The name of the subcommand used, or null when the command has none.</summary>
    public static string? Subcommand(SlashCommandInteraction interaction) =>
        interaction.Data.Options.FirstOrDefault(option => option.Type == ApplicationCommandOptionType.SubCommand)?.Name;
}
