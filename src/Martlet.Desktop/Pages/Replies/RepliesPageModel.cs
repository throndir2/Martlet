using System.Globalization;
using Martlet.Core.Contracts;
using Martlet.Core.Settings;

namespace Martlet.Desktop;

/// <summary>Companion › Replies' decisions, without WPF: the reply settings and their choices, what each line says for the
/// Thinking route now, and reading what the owner typed into the settings to save. <see cref="RepliesPage"/> draws it, and
/// <see cref="IRepliesPageServices"/> is all it reads in the app.</summary>
internal sealed class RepliesPageModel
{
    internal sealed record ReplySetting(GenerationSetting Setting, string Id, string Label, string Range, string Help, bool Whole)
    {
        /// <summary>The label without its access key, for messages: "Top P".</summary>
        internal string Name => Label.Replace("_", "", StringComparison.Ordinal);

        internal string Format(double value) => value.ToString(Whole ? "0" : "0.###", CultureInfo.CurrentCulture);
        internal double? Read(GenerationSettings? settings) => Setting switch
        {
            GenerationSetting.MaxReplyTokens => settings?.MaxReplyTokens,
            GenerationSetting.Temperature => settings?.Temperature,
            GenerationSetting.TopP => settings?.TopP,
            GenerationSetting.TopK => settings?.TopK,
            GenerationSetting.MinP => settings?.MinP,
            GenerationSetting.RepeatPenalty => settings?.RepeatPenalty,
            GenerationSetting.FrequencyPenalty => settings?.FrequencyPenalty,
            GenerationSetting.PresencePenalty => settings?.PresencePenalty,
            _ => settings?.ContextTokens
        };
    }

    internal static readonly IReadOnlyList<ReplySetting> ReplySettings =
    [
        new(GenerationSetting.MaxReplyTokens, "RepliesMaxReplyTokens", "_Max reply length",
            $"{GenerationSettings.MinimumReplyTokens}-{GenerationSettings.MaximumReplyTokens} tokens, blank = {GenerationSettings.DefaultMaxReplyTokens}",
            "Stops unusually long replies; a reply that hits it may end early.", true),
        new(GenerationSetting.Temperature, "RepliesTemperature", "_Temperature", "0-2",
            "Higher is more varied, lower is more predictable.", false),
        new(GenerationSetting.TopP, "RepliesTopP", "Top _P", "above 0, at most 1",
            "Controls how many likely words the model considers (1 turns it off).", false),
        new(GenerationSetting.TopK, "RepliesTopK", "Top _K", $"1-{GenerationSettings.MaximumTopK}",
            "Limits the model to this many likely words.", true),
        new(GenerationSetting.MinP, "RepliesMinP", "M_in P", "0-1",
            "Drops very unlikely words.", false),
        new(GenerationSetting.RepeatPenalty, "RepliesRepeatPenalty", "_Repeat penalty", "0-2",
            "Above 1 discourages repeated words and phrases.", false),
        new(GenerationSetting.FrequencyPenalty, "RepliesFrequencyPenalty", "_Frequency penalty", "-2 to 2",
            "Positive values reduce words that already appear often.", false),
        new(GenerationSetting.PresencePenalty, "RepliesPresencePenalty", "P_resence penalty", "-2 to 2",
            "Positive values nudge replies toward new topics.", false),
        new(GenerationSetting.ContextTokens, "RepliesContextTokens", "_Context size",
            $"{GenerationSettings.MinimumContextTokens}-{GenerationSettings.MaximumContextTokens} tokens, blank = {GenerationSettings.DefaultContextTokens}",
            "How much the model keeps in mind: persona, lore, memory, the conversation so far and the reply. Older exchanges are " +
            "left out once a conversation outgrows it, and a larger size costs more on paid providers.", true)
    ];

    /// <summary>Companion › Replies › Thinking steps: each choice and what it saves as <see cref="GenerationSettings.Reasoning"/>.
    /// Off is the default: it saves nothing (null), and a saved false from earlier versions also reads as Off.</summary>
    internal static readonly IReadOnlyList<(string Name, bool? Value)> ThinkingChoices = [("Off", null), ("On", true)];
    internal const string ThinkingRange = "Off (default) or On";
    internal const string ThinkingHelp = "Off lets replies start sooner; On lets a reasoning model think step by step first. On can " +
        "help with hard questions but waits seconds before the first word.";

    internal static int ThinkingIndex(bool? reasoning) => GenerationSettings.ThinkingSteps(new() { Reasoning = reasoning }) ? 1 : 0;

    /// <summary>Companion › Replies › Short first sentence: each choice and what it saves as
    /// <see cref="GenerationSettings.ShortFirstSentence"/>. On is the default: it saves nothing (null).</summary>
    internal static readonly IReadOnlyList<(string Name, bool? Value)> ShortFirstChoices = [("On", null), ("Off", false)];
    internal const string ShortFirstRange = "On (default) or Off";
    internal const string ShortFirstHelp = "Martlet begins each spoken reply with a few quick words, such as \"Oh, nice one!\", so " +
        "its voice starts sooner. The Short first sentence prompt on Prompts asks for them.";

    internal static int ShortFirstIndex(bool? shortFirst) => GenerationSettings.StartsShort(new() { ShortFirstSentence = shortFirst }) ? 0 : 1;

    /// <summary>Companion › Replies › Adult content: each choice and what it saves as <see cref="GenerationSettings.AdultContent"/>.
    /// Off is the default: it saves nothing (null).</summary>
    internal static readonly IReadOnlyList<(string Name, bool? Value)> AdultChoices = [("Off", null), ("On (18+)", true)];
    internal const string AdultRange = "Off (default) or On, only if you are 18 or older";
    internal const string AdultHelp = "On lets Martlet flirt and be sexual when its character is an adult, never with a character " +
        "under 18. It never does in a Discord call, and many cloud models refuse such content.";

    internal static int AdultIndex(bool? adult) => GenerationSettings.Adult(new() { AdultContent = adult }) ? 1 : 0;

    /// <summary>Companion › Replies › Personal content: each choice and what it saves as
    /// <see cref="GenerationSettings.PersonalContent"/>. Keep private is the default: it saves nothing (null).</summary>
    internal static readonly IReadOnlyList<(string Name, PersonalContent? Value)> PersonalChoices =
        [("Keep private", null), ("Read my messages", PersonalContent.Messages), ("Everything", PersonalContent.Everything)];
    internal const string PersonalRange = "Keep private (default), Read my messages or Everything";
    internal const string PersonalHelp = "What Martlet may do with personal things it sees on your screen or camera. Keep private " +
        "never reads them out, Read my messages may quote your messages, and Everything also shows password and banking windows.";

    internal static int PersonalIndex(PersonalContent? personal) =>
        GenerationSettings.Personal(new() { PersonalContent = personal }) switch
        {
            PersonalContent.Messages => 1,
            PersonalContent.Everything => 2,
            _ => 0
        };

    internal RepliesPageModel(IRepliesPageServices services)
    {
        Route = services.Settings.ThinkingRoute();
        Saved = services.Settings?.Generation;
        Place = Route is null ? null : services.PlaceName(Route);
    }

    /// <summary>The Thinking route the replies use now, or null before Thinking is set up.</summary>
    internal SetupRoute? Route { get; }
    /// <summary>The saved reply settings, or null.</summary>
    internal GenerationSettings? Saved { get; }
    /// <summary>Where the Thinking route runs, in words, or null without a route.</summary>
    internal string? Place { get; }

    /// <summary>Now's first line.</summary>
    internal string RouteText => Route is null ? "Thinking isn't set up yet." : $"Using {Route.ModelId} from {Place}.";

    /// <summary>How the Thinking route uses <paramref name="setting"/> (every setting counts as used without a route).</summary>
    internal GenerationSettingUse Use(GenerationSetting setting) =>
        Route is null ? GenerationSettingUse.Used : GenerationSupport.Use(Route.RouteType, Route.Origin, setting);

    /// <summary>How the Thinking route takes Thinking steps; see <see cref="GenerationSupport.Reasoning"/>.</summary>
    internal static string ThinkingUseText(SetupRoute route, string place) =>
        GenerationSupport.Use(route.RouteType, route.Origin, GenerationSetting.Reasoning) switch
        {
            GenerationSettingUse.Used when route.RouteType == SetupRouteType.GatewayOllama =>
                $"Used by {place} (if replies fail after a change, update the host to this Martlet version).",
            GenerationSettingUse.Used => $"Used by {place}.",
            GenerationSettingUse.ServerDependent =>
                $"Depends on the model at {place}: a model that can't turn thinking off or on may ignore it or refuse it; Martlet " +
                "then uses the model's own default.",
            _ => $"Not used: {place}'s models here answer without thinking first."
        };

    internal static string UseText(GenerationSettingUse use, GenerationSetting setting, SetupRoute route, string place) => use switch
    {
        GenerationSettingUse.Used => $"Used by {place}.",
        GenerationSettingUse.ServerDependent => $"May not work with {place}, so clear this field if replies fail.",
        _ when MainWindow.IsLocalOllama(route) => $"Not used by {place}.",
        _ => $"Not supported by {place}."
    };

    /// <summary>A setting's range; Ollama on this PC has no reply length limit unless one is set, a Chat Completions route
    /// leaves room for hidden reasoning, and the context size depends on where the model runs.</summary>
    internal static string RangeText(ReplySetting setting, SetupRoute? route) => setting.Setting switch
    {
        GenerationSetting.MaxReplyTokens when MainWindow.IsLocalOllama(route) =>
            $"{GenerationSettings.MinimumReplyTokens}-{GenerationSettings.MaximumReplyTokens} tokens, blank = no limit",
        GenerationSetting.MaxReplyTokens =>
            $"{GenerationSettings.MinimumReplyTokens}-{GenerationSettings.MaximumReplyTokens} tokens, blank = " +
            GenerationSupport.DefaultReplyTokens(route?.RouteType),
        GenerationSetting.ContextTokens when route?.RouteType == SetupRouteType.GatewayOllama =>
            $"{GenerationSettings.MinimumContextTokens}-{GenerationSettings.MaximumHostContextTokens} tokens on a host, blank = {GenerationSettings.DefaultHostContextTokens}",
        GenerationSetting.ContextTokens when MainWindow.IsLocalOllama(route) =>
            $"{GenerationSettings.MinimumContextTokens}-{GenerationSettings.MaximumContextTokens} tokens, blank = Ollama's context length",
        GenerationSetting.ContextTokens =>
            $"{GenerationSettings.MinimumContextTokens}-{GenerationSettings.MaximumContextTokens} tokens, blank = {GenerationSettings.DefaultContextTokens} or the model's limit",
        _ => setting.Range
    };

    internal static string DescribeGeneration(GenerationSettings? settings, SetupRoute? route = null)
    {
        // One sentence (the UI text rule, #992): its facts are joined with semicolons.
        var facts = new List<string> { "Replies are brief" };
        // Ollama on this PC gets no reply token budget unless a max reply length is set.
        facts.Add(settings?.MaxReplyTokens is null && MainWindow.IsLocalOllama(route) ? "no maximum length is set"
            : $"maximum length is {GenerationSupport.ReplyTokens(route?.RouteType, settings)} tokens" +
              (GenerationSupport.BudgetIncludesThinking(route?.RouteType) ? ", including any hidden thinking" : ""));
        // Thinking steps is Off unless On is chosen; routes whose models don't reason don't mention it.
        if (route is null || GenerationSupport.Use(route.RouteType, route.Origin, GenerationSetting.Reasoning) != GenerationSettingUse.Unused)
            facts.Add(GenerationSettings.ThinkingSteps(settings) ? "Thinking steps are on" : "Thinking steps are off");
        facts.Add(GenerationSettings.StartsShort(settings) ? "spoken replies start with a short first sentence"
            : "spoken replies don't start with a short first sentence");
        if (GenerationSettings.Adult(settings)) facts.Add("adult content is on");
        switch (GenerationSettings.Personal(settings))
        {
            case PersonalContent.Messages: facts.Add("Martlet may read your messages"); break;
            case PersonalContent.Everything: facts.Add("Martlet may read your messages and see private windows"); break;
        }
        var parts = new List<string>();
        if (settings is not null)
            foreach (var setting in ReplySettings.Skip(1))
                if (setting.Read(settings) is { } value)
                    parts.Add($"{char.ToLower(setting.Name[0], CultureInfo.CurrentCulture)}{setting.Name[1..]} {setting.Format(value)}");
        facts.Add(parts.Count == 0 ? "other settings use the model default" : string.Join(", ", parts));
        return string.Join("; ", facts) + ".";
    }

    /// <summary>The reply settings the owner typed (<paramref name="texts"/>, by setting) and chose (each list's index), or why
    /// they can't be saved yet: a field that isn't a number in range, or settings that don't pass their checks. Thinking longer
    /// is not one of them (Deep thinking sets it).</summary>
    internal static (GenerationSettings? Generation, string? Problem) Read(IReadOnlyDictionary<GenerationSetting, string> texts,
        int thinking, int shortFirst, int adult, int personal)
    {
        var values = new Dictionary<GenerationSetting, double?>();
        foreach (var setting in ReplySettings)
        {
            var text = texts[setting.Setting].Trim();
            if (text.Length == 0) { values[setting.Setting] = null; continue; }
            // Whole numbers may be typed with thousands separators ("100,000"), as the ranges show them.
            var styles = setting.Whole ? NumberStyles.Float | NumberStyles.AllowThousands : NumberStyles.Float;
            if (!double.TryParse(text, styles, CultureInfo.CurrentCulture, out var value) &&
                !double.TryParse(text, styles, CultureInfo.InvariantCulture, out value) ||
                setting.Whole && (value != Math.Floor(value) || Math.Abs(value) > int.MaxValue))
                return (null, $"Reply settings not saved yet: {setting.Name}: enter {(setting.Whole ? "a whole number" : "a number")} in the shown range, or leave it blank.");
            values[setting.Setting] = value;
        }
        int? Whole(GenerationSetting setting) => values[setting] is { } value ? (int)value : null;
        var generation = new GenerationSettings
        {
            MaxReplyTokens = Whole(GenerationSetting.MaxReplyTokens),
            Temperature = values[GenerationSetting.Temperature],
            TopP = values[GenerationSetting.TopP],
            TopK = Whole(GenerationSetting.TopK),
            MinP = values[GenerationSetting.MinP],
            RepeatPenalty = values[GenerationSetting.RepeatPenalty],
            FrequencyPenalty = values[GenerationSetting.FrequencyPenalty],
            PresencePenalty = values[GenerationSetting.PresencePenalty],
            ContextTokens = Whole(GenerationSetting.ContextTokens),
            Reasoning = ThinkingChoices[Math.Max(0, thinking)].Value,
            ShortFirstSentence = ShortFirstChoices[Math.Max(0, shortFirst)].Value,
            AdultContent = AdultChoices[Math.Max(0, adult)].Value,
            PersonalContent = PersonalChoices[Math.Max(0, personal)].Value
        };
        try { generation.Validate(); }
        catch (ContractException error) { return (null, "Reply settings not saved yet: " + error.Message); }
        return (generation, null);
    }
}
