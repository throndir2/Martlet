using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using Martlet.Conversation;
using Martlet.Core.Cluster;
using Martlet.Core.Contracts;
using Martlet.Core.Settings;
using Martlet.Credentials.Windows;
using Martlet.Logging;
using Martlet.Providers;
using Martlet.Providers.Ollama;

namespace Martlet.Desktop;

/// <summary>Companion › Vision's and Companion › Hearing's models (docs/SENSE_MODELS.md): which models take pictures and which take
/// recordings. Thinking, the text model, always writes the reply, and with an empty list it also takes both itself (an omni
/// model). Each page has a list of models of their own (MainWindow.SensePools.cs, pools-local.json): Ollama on this PC, one of your
/// computers or one of its graphics cards, a server you run, or a cloud provider. The first one that takes the kind puts what it sees or
/// hears into words for Thinking; the next ones take a job when it is busy. A cloud model's key is in Windows Credential Manager
/// (pool-keys.json says which). Choosing a model asks its server what it takes, and Test vision and Test hearing find out
/// (MainWindow.ModelAbilities.cs). Companion › Thinking says where pictures and recordings go. Each page shows the lines of
/// <see cref="SenseNowLines"/> in its Now card.</summary>
public partial class MainWindow
{
    /// <summary>The model picked under Ollama on this PC on each card.</summary>
    private readonly Dictionary<SenseKind, string> senseLocalPicked = [];
    /// <summary>The last check of whether a model fits beside Thinking's in Ollama on this PC.</summary>
    private (string Thinking, string Model, SideBySideFit Fit)? senseLocalFit;
    /// <summary>The models in Ollama on this PC that Martlet asked about this session (once each).</summary>
    private readonly HashSet<string> senseAsked = new(StringComparer.Ordinal);

    private static string SenseId(SenseKind kind) => kind == SenseKind.Image ? "ImageModel" : "AudioModel";
    internal static string SenseWord(SenseKind kind) => kind == SenseKind.Image ? "image" : "audio";
    private static string SenseInputs(SenseKind kind) => kind == SenseKind.Image ? "pictures" : "recordings";

    /// <summary>The Now card's lines about the model that takes <paramref name="kind"/>: what takes it now, where it goes and
    /// why, what the model is known to do, what is sent where, and Test vision or Test hearing.</summary>
    private List<UIElement> SenseNowLines(SenseKind kind)
    {
        var id = SenseId(kind);
        var setup = SavedSenseSetup();
        var senses = setup.Senses;
        var thinking = homeSettings.ThinkingRoute();
        var abilities = SavedModelAbilities();
        var route = SenseRouting.For(kind, senses, thinking, abilities);
        var place = senses.Place(kind);
        var own = place is not null && !SenseRouting.IsThinking(place, thinking) ? place : null;
        var lines = new List<UIElement>();
        void Line(string lineId, string text, bool problem = false)
        {
            var line = problem ? Warning(text) : Note(text, new Thickness(0, 0, 0, 6));
            AutomationProperties.SetAutomationId(line, lineId);
            lines.Add(line);
        }
        Line(id + "Now", SenseNow(kind, senses, setup.For(kind).Count, thinking));
        Line(id + "Route", route.Why, problem: route.Path == SensePath.None && thinking is not null);
        if (SenseKnown(kind, senses, thinking, abilities) is { } known) Line(id + "Known", known);
        // Only a model that takes this kind gets anything sent to it.
        if (own is not null && route.Described) Line(id + "Sent", SenseSent(kind, own));
        if (setup.State == "unreadable")
            lines.Add(Warning($"Martlet can't read {Martlet.Core.Cluster.PoolSettings.LocalFileName} (a newer Martlet's, or a damaged file), so " +
                "the text model takes pictures and recordings; changing the list below replaces the file."));
        lines.AddRange(SenseTestControls(kind, own, thinking, route));
        return lines;
    }

    // ---------- the status lines (MCP SafeValues) ----------

    /// <summary>The card's Now line, the list in words ("ImageModelNow"): the first model of its own and how many more take a job
    /// when it is busy, or the text model with an empty list. <paramref name="members"/>: the models in the list this PC can use.</summary>
    internal static string SenseNow(SenseKind kind, SenseModels senses, int members, SetupRoute? thinking) => senses.Place(kind) switch
    {
        { } own => $"{own.Describe()} is first in the list" +
            (members > 1 ? $"; {members - 1} more {(members == 2 ? "takes" : "take")} a job when it is busy." : "."),
        _ => thinking is null ? $"Nothing in the list: the text model takes the {SenseInputs(kind)} (Thinking isn't set up yet)."
            : $"Nothing in the list: the text model takes the {SenseInputs(kind)} itself (Thinking: {thinking.ModelId})."
    };

    /// <summary>What the model that takes <paramref name="kind"/> is known to do ("ImageModelKnown"): what Martlet found out about
    /// it (where and when), else what its name says; null before Thinking is set up.</summary>
    internal static string? SenseKnown(SenseKind kind, SenseModels senses, SetupRoute? thinking, ModelAbilities abilities)
    {
        var image = kind == SenseKind.Image;
        if (senses.Place(kind) is { ModelId: { } model } own && !SenseRouting.IsThinking(own, thinking))
        {
            var origin = own.Place == DeepThinkingPlace.Host ? own.HostOrigin : own.Origin;
            return ModelKnown(kind, model, abilities.Find(origin, model), ModelSenses.For(own, abilities).Known(kind));
        }
        if (thinking is null) return null;
        if (!image && !HearingModelCatalog.CarriesAudio(thinking.RouteType))
            return $"{thinking.ModelId} gets no recordings on this route: only an OpenAI-compatible endpoint or one of your computers takes them.";
        return ModelKnown(kind, thinking.ModelId, abilities.Find(thinking.Origin, thinking.ModelId), ModelSenses.For(thinking, abilities).Known(kind));
    }

    /// <summary>What <paramref name="model"/> is known to do for <paramref name="kind"/>: what Martlet found out
    /// (<paramref name="found"/>, with its last check: model-abilities.json keeps one source and date for each model), else
    /// <paramref name="byName"/> (its name), else that Martlet doesn't know yet.</summary>
    internal static string ModelKnown(SenseKind kind, string model, ModelAbility? found, bool? byName)
    {
        var image = kind == SenseKind.Image;
        var test = image ? "Test vision" : "Test hearing";
        string Does(bool yes) => image ? yes ? "sees pictures" : "doesn't see pictures" : yes ? "hears recordings" : "doesn't hear recordings";
        if ((image ? found?.Sees : found?.Hears) is { } known)
            return $"{model} {Does(known)}, as Martlet found out (last check on {found!.CheckedAt.LocalDateTime:d}: {found.Source}).";
        return byName is { } yes ? $"By its name, {model} {Does(yes)}; {test} makes sure."
            : $"Martlet doesn't know yet whether {model} {(image ? "sees pictures" : "hears recordings")}; it tries and remembers a refusal, " +
                $"and {test} finds out.";
    }

    /// <summary>What goes to a model of its own, and where ("ImageModelSent").</summary>
    internal static string SenseSent(SenseKind kind, DeepThinkingSettings own)
    {
        var image = kind == SenseKind.Image;
        var what = image ? "Pictures of your screen or camera and a few lines of the conversation"
            : "Recordings of your voice and of what this PC plays, with a few lines of the conversation,";
        if (own.Place == DeepThinkingPlace.Host) return $"{what} go to {own.HostId} through its paired, pinned connection.";
        if (ContextBudget.IsLocalOllama(SetupRouteType.ChatCompletions, own.Origin))
            return $"{(image ? "Pictures" : "Recordings")} stay on this PC: Ollama describes them here.";
        if (own.Origin is { } origin && OnThisPc(origin)) return $"{what} go to {LocalModelServers.Name(origin)} on this PC, an app that may pass them on.";
        return $"{what} go to {(Uri.TryCreate(own.Origin, UriKind.Absolute, out var uri) ? uri.IdnHost : own.Origin)}, and requests may cost money.";
    }

    /// <summary>Companion › Thinking's line on the image and audio models ("ThinkingSenses"): where pictures and recordings go now,
    /// and the Thinking pool models that get them too for background work (<paramref name="pool"/>; null: not said).</summary>
    internal static string TextModelText(SenseModels senses, SetupRoute? thinking, ModelAbilities? abilities, ThinkingPoolSettings? pool = null)
    {
        string Goes(SenseKind kind) => SenseRouting.For(kind, senses, thinking, abilities) switch
        {
            { Described: true, Model: { } model } => $"the {SenseWord(kind)} model, {model.Describe()}",
            { Path: SensePath.Thinking } => "Thinking itself",
            _ => kind == SenseKind.Image ? "no model, so Martlet can't see" : "no model, so Thinking gets the transcript only"
        };
        string? Pool(SenseKind kind) => pool is not null && SensePoolUsers.For(kind, pool, abilities).Where(u => u.Gets).ToArray() is { Length: > 0 } users
            ? $"{SenseInputs(kind)} to {SensePoolUsers.Names(users)}"
            : null;
        var background = new[] { Pool(SenseKind.Image), Pool(SenseKind.Audio) }.OfType<string>().ToArray();
        return $"Thinking is the text model and writes every reply; pictures go to {Goes(SenseKind.Image)} (Companion › Vision), and " +
            $"recordings go to {Goes(SenseKind.Audio)} (Companion › Hearing)" +
            (background.Length > 0 ? $"; background work also sends {string.Join(" and ", background)} in the Thinking pool." : ".");
    }

    /// <summary>The consent a cloud provider, a model app on this PC or another server needs: what is sent there.</summary>
    internal static string SenseConsent(SenseKind kind, string provider, bool onThisPc) => kind == SenseKind.Image
        ? $"I choose {provider} as the image model, so pictures of your screen or camera and a few lines of the conversation are sent there" +
            (onThisPc ? ", an app that may pass them on." : ", and requests may cost money.")
        : $"I choose {provider} as the audio model, so recordings of your voice and of what this PC plays, with a few lines of the conversation, " +
            "are sent there" + (onThisPc ? ", an app that may pass them on." : ", and requests may cost money.");
}
