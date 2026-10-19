using System.Globalization;
using System.Text;
using System.Text.Json.Nodes;

namespace Martlet.Home;

public enum HomeCommandRisk
{
    /// <summary>Ordinary words: sent to Home Assistant's Assist, which acts only if it recognizes a command.</summary>
    Normal,
    /// <summary>A short request that names a lock, door, garage, gate, alarm or valve and isn't a status question.</summary>
    Sensitive,
    /// <summary>Mentions such a device in a longer sentence (chat, not a command): not sent to Home Assistant at all.</summary>
    NotACommand
}

/// <summary>Martlet's own check on top of Home Assistant's exposed-entities list. Anything that could open the home (locks,
/// doors, garage doors, gates, alarms, valves) is blocked or needs a click to confirm, except status questions such as
/// "is the garage door open?". It looks for common words in English, German, French, Spanish, Italian and Dutch; it can
/// miss other phrasings, so such devices are best left unexposed to voice assistants in Home Assistant.</summary>
public static class HomeCommandGuard
{
    /// <summary>Home Assistant commands are short; longer sentences that mention a door or lock are conversation.</summary>
    public const int MaximumCommandWords = 12;

    // Whole words, word beginnings and compound endings that name something able to open the home. Short words are matched
    // whole so "portable heater", "torch lamp" or "ventilator" stay ordinary.
    private static readonly HashSet<string> SensitiveWords = new(StringComparer.Ordinal)
    {
        "lock", "locks", "locked", "locking", "door", "doors", "gate", "gates", "alarm", "alarms", "valve", "valves",
        "tor", "tore", "tür", "türe", "türen", "tuer", "tueren", "schloss", "ventil", "ventile",
        "porte", "portes", "vanne", "vannes", "alarme", "alarmes",
        "puerta", "puertas", "alarma", "alarmas", "porta", "allarme", "cancello", "cancelli",
        "deur", "deuren", "slot", "sloten", "poort", "poorten"
    };

    private static readonly string[] SensitivePrefixes =
    [
        "unlock", "deadbolt", "padlock", "garag", "disarm",
        "verriegel", "entriegel", "abschlie", "aufschlie", "zuschlie", "alarmanlage", "haustür", "haustuer",
        "serrure", "verrou", "déverrouill", "deverrouill", "portail", "désarm", "desarm",
        "portón", "porton", "cerradura", "cerrojo", "válvul", "valvul",
        "serratura", "portone", "allarm", "valvol",
        "vergrendel", "ontgrendel"
    ];

    private static readonly string[] SensitiveSuffixes = ["tür", "türe", "türen", "tuer", "schloss", "deur", "deuren"];

    /// <summary>Whether one lower-case word names a lock, door, garage, gate, alarm or valve.</summary>
    public static bool IsSensitiveWord(string word) =>
        SensitiveWords.Contains(word) ||
        SensitivePrefixes.Any(prefix => word.StartsWith(prefix, StringComparison.Ordinal)) ||
        word.Length > 5 && SensitiveSuffixes.Any(suffix => word.EndsWith(suffix, StringComparison.Ordinal));

    /// <summary>Whether any word of <paramref name="text"/> names a lock, door, garage, gate, alarm or valve.</summary>
    public static bool MentionsSensitive(string text) => Words(text, keepGreeting: true).Any(IsSensitiveWord);
    // Words that can open a status question. "do" and "have" are not here: they also open requests ("do unlock the door",
    // "have the alarm disarmed"). A question word alone is not enough; the text must also have no action word.
    private static readonly HashSet<string> QuestionWords = new(StringComparer.Ordinal)
    {
        "is", "are", "was", "were", "did", "does", "has", "had", "what", "whats", "which", "where", "wheres",
        "who", "whos", "when", "how", "hows", "why",
        "ist", "sind", "war", "waren", "hat", "haben", "welche", "welcher", "welches", "wo", "wie", "wer", "wann", "warum",
        "est", "quel", "quelle", "quels", "quelles", "où", "comment", "qui", "quand", "pourquoi", "combien",
        "está", "están", "esta", "estan", "qué", "cuál", "cuáles", "dónde", "donde", "cómo", "quién", "cuándo",
        "è", "sono", "quale", "quali", "dove", "come", "chi", "quando", "perché", "cosa",
        "zijn", "wat", "welke", "waar", "hoe", "wie", "wanneer", "staat", "staan"
    };

    // Verb forms that ask for an action ("why don't you unlock the door", "how about opening the garage"). State words such
    // as "unlocked", "geöffnet", "ouvert" or "abierta" are not here, so "is the door unlocked?" stays a status question.
    private static readonly HashSet<string> ActionWords = new(StringComparer.Ordinal)
    {
        "unlock", "unlocking", "disarm", "disarming", "arm", "arming", "opening", "closing", "locking", "shutting", "raise",
        "raising", "lift", "lifting", "release", "releasing", "trigger", "toggle", "deactivate", "deactivating", "let",
        "öffne", "öffnen", "öffnest", "öffnet", "oeffne", "oeffnen", "aufmachen", "aufmache", "entriegle", "entriegele",
        "entriegeln", "entsperre", "entsperren", "aufschließen", "aufschliessen", "aufsperren", "deaktiviere", "deaktivieren",
        "schließe", "schliesse", "schließen", "schliessen", "mach", "mache",
        "ouvre", "ouvres", "ouvrez", "ouvrir", "ouvrons", "déverrouille", "déverrouiller", "déverrouillez", "deverrouille",
        "deverrouiller", "deverrouillez", "désarme", "désarmer", "désarmez", "desarme", "desarmer", "désactive", "désactiver",
        "desactive", "desactiver", "ferme", "fermer", "fermez",
        "abre", "abres", "abra", "abran", "abrir", "desbloquea", "desbloquear", "desbloquee", "desactiva", "desactivar",
        "desarma", "desarmar", "cierra", "cerrar",
        "apri", "aprire", "apra", "sblocca", "sbloccare", "disattiva", "disattivare", "disarma", "disarmare", "chiudi", "chiudere",
        "openen", "opent", "ontgrendel", "ontgrendelen", "ontgrendelt", "deactiveer", "deactiveren", "doe", "sluit", "sluiten"
    };

    // Words that are a request as a verb ("why don't you open the gate") but a state or a name after the device or "the"
    // ("is the garage door open?", "is the lock on?").
    private static readonly HashSet<string> ActionOrStateWords = new(StringComparer.Ordinal)
    {
        "open", "close", "shut", "lock"
    };

    private static readonly HashSet<string> StateContext = new(StringComparer.Ordinal)
    {
        "the", "a", "an", "my", "our", "your", "this", "that", "these", "those", "its", "any", "still", "left", "wide", "all",
        "fully", "now", "currently", "already", "properly", "been", "being"
    };

    public static HomeCommandRisk Assess(string text)
    {
        var words = Words(text);
        if (!words.Any(IsSensitiveWord))
            return HomeCommandRisk.Normal;
        if (words.Count > MaximumCommandWords) return HomeCommandRisk.NotACommand;
        return IsStatusQuestion(words) ? HomeCommandRisk.Normal : HomeCommandRisk.Sensitive;
    }

    // A status question starts with a question word and asks for no action anywhere in it.
    private static bool IsStatusQuestion(List<string> words)
    {
        if (words.Count == 0 || !QuestionWords.Contains(words[0])) return false;
        // "did I lock the door?" and "does the gate close?" ask about the past or a habit, not for an action.
        var pastOrHabit = words[0] is "did" or "does";
        for (var i = 0; i < words.Count; i++)
        {
            var word = words[i];
            if (ActionWords.Contains(word)) return false;
            if (!pastOrHabit && ActionOrStateWords.Contains(word) &&
                !(i > 0 && (IsSensitiveWord(words[i - 1]) || StateContext.Contains(words[i - 1]))))
                return false;
        }
        return true;
    }

    /// <summary>Whether Home Assistant reports operating something that can open the home.</summary>
    public static bool IsSensitive(HomeTarget target)
    {
        var id = target.Id ?? "";
        return target.Domain is "lock" or "alarm_control_panel" or "valve" ||
            target.Domain == "cover" && (id.Contains("garage", StringComparison.Ordinal) || id.Contains("gate", StringComparison.Ordinal) ||
                id.Contains("door", StringComparison.Ordinal)) ||
            target.Type is "device_class" && id is "garage" or "garage_door" or "gate" or "door" ||
            target.Type is "domain" && id is "lock" or "alarm_control_panel" or "valve";
    }

    internal static List<string> Words(string text, bool keepGreeting = false)
    {
        var words = new List<string>();
        var current = new StringBuilder();
        var previous = '\0';
        foreach (var c in text.Normalize(NormalizationForm.FormC))
        {
            // Tool and script names split at camel case too ("HassUnlockDoor", "open_garage").
            if (char.IsUpper(c) && char.IsLower(previous) && current.Length > 0)
            {
                words.Add(current.ToString());
                current.Clear();
            }
            previous = c;
            if (char.IsLetterOrDigit(c)) current.Append(char.ToLower(c, CultureInfo.InvariantCulture));
            else if (c is '\'' or '’')
            {
                // French and Italian elision ("l'alarme", "d'entrée", "qu'il") ends a word; English ("don't") does not.
                if (current.ToString() is "l" or "d" or "j" or "m" or "n" or "s" or "c" or "qu" or "dell" or "all" or "nell" or "dall")
                {
                    words.Add(current.ToString());
                    current.Clear();
                }
            }
            else if (current.Length > 0)
            {
                words.Add(current.ToString());
                current.Clear();
            }
        }
        if (current.Length > 0) words.Add(current.ToString());
        // A leading greeting or the companion's name is not the first word of the request ("hey Martlet, is the door locked?").
        while (!keepGreeting && words.Count > 1 && words[0] is "hey" or "hi" or "ok" or "okay" or "please" or "martlet" or "so" or "and" or "um" or "uh")
            words.RemoveAt(0);
        return words;
    }

    private static readonly HashSet<string> ReadOnlyTools = new(StringComparer.Ordinal)
    {
        "GetLiveContext", "GetDateTime", "HassGetState", "HassGetWeather", "HassGetCurrentDate", "HassGetCurrentTime",
        "HassTimerStatus", "todo_get_items", "calendar_get_events"
    };

    private static readonly HashSet<string> SensitiveDomains = new(StringComparer.Ordinal) { "lock", "alarm_control_panel", "valve" };
    private static readonly HashSet<string> SensitiveDeviceClasses = new(StringComparer.Ordinal) { "garage", "garage_door", "gate", "door" };

    /// <summary>How a Home Assistant MCP tool call should be treated. Status tools are read-only; Home Assistant's built-in
    /// intents (Hass*) on ordinary devices are comfort actions; anything naming a lock, door, garage, gate, alarm or valve
    /// (in the tool name, its arguments, a domain or device class, or a name in <paramref name="sensitiveNames"/>) is
    /// sensitive; other tools, such as exposed scripts, are unknown. Without <paramref name="sensitiveNames"/> (Home
    /// Assistant's entity list could not be read) a device addressed only by name is unknown too.</summary>
    public static HomeToolRisk AssessTool(string tool, JsonObject arguments, IReadOnlyCollection<string>? sensitiveNames)
    {
        ArgumentNullException.ThrowIfNull(tool);
        ArgumentNullException.ThrowIfNull(arguments);
        if (ReadOnlyTools.Contains(tool)) return HomeToolRisk.ReadOnly;
        if (Words(tool, keepGreeting: true).Any(IsSensitiveWord)) return HomeToolRisk.Sensitive;
        var values = new List<(string Key, string Value)>();
        Collect(null, arguments, values, 0);
        foreach (var (key, value) in values)
        {
            var normalized = value.Trim().ToLowerInvariant();
            if (key == "domain" && SensitiveDomains.Contains(normalized) ||
                key == "device_class" && SensitiveDeviceClasses.Contains(normalized) ||
                MentionsSensitive(value) ||
                sensitiveNames is not null && sensitiveNames.Contains(value.Trim()))
                return HomeToolRisk.Sensitive;
        }
        if (!tool.StartsWith("Hass", StringComparison.Ordinal)) return HomeToolRisk.Unknown;
        var targetsByName = values.Any(v => v.Key is "name" or "area" or "floor") && !values.Any(v => v.Key == "domain");
        return sensitiveNames is null && targetsByName ? HomeToolRisk.Unknown : HomeToolRisk.Comfort;
    }

    private static void Collect(string? key, JsonNode? node, List<(string, string)> values, int depth)
    {
        if (node is null || depth > 6 || values.Count >= 64) return;
        switch (node)
        {
            case JsonObject obj:
                foreach (var (name, child) in obj) Collect(name, child, values, depth + 1);
                break;
            case JsonArray array:
                foreach (var child in array) Collect(key, child, values, depth + 1);
                break;
            case JsonValue value when value.TryGetValue<string>(out var text):
                values.Add((key ?? "", text.Length > 256 ? text[..256] : text));
                break;
        }
    }
}

public enum HomeToolRisk { ReadOnly, Comfort, Sensitive, Unknown }
