using System.Globalization;
using System.Text;

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

    private static readonly string[] SensitiveStems =
    [
        "lock", "unlock", "deadbolt", "padlock", "door", "garage", "gate", "alarm", "disarm", "security", "valve",
        "schloss", "schließ", "schliess", "verriegel", "entriegel", "tür", "tuer", "tor", "garag", "ventil",
        "serrure", "verrou", "déverrouill", "deverrouill", "porte", "portail", "vanne",
        "cerradura", "cerrojo", "puerta", "portón", "porton", "válvula", "valvula",
        "serratura", "porta", "cancell", "allarm", "valvol",
        "slot", "deur", "poort"
    ];

    private static readonly HashSet<string> QuestionWords = new(StringComparer.Ordinal)
    {
        "is", "are", "was", "were", "did", "does", "do", "has", "have", "had", "what", "whats", "which", "where", "wheres",
        "who", "whos", "when", "how", "hows", "why",
        "ist", "sind", "war", "waren", "hat", "haben", "welche", "welcher", "welches", "wo", "wie", "wer", "wann", "warum",
        "est", "quel", "quelle", "quels", "quelles", "où", "comment", "qui", "quand", "pourquoi", "combien",
        "está", "están", "esta", "estan", "qué", "cuál", "cuáles", "dónde", "donde", "cómo", "quién", "cuándo",
        "è", "sono", "quale", "quali", "dove", "come", "chi", "quando", "perché", "cosa",
        "zijn", "wat", "welke", "waar", "hoe", "wie", "wanneer", "staat", "staan"
    };

    public static HomeCommandRisk Assess(string text)
    {
        var words = Words(text);
        if (!words.Any(word => SensitiveStems.Any(stem => word.StartsWith(stem, StringComparison.Ordinal))))
            return HomeCommandRisk.Normal;
        if (words.Count > MaximumCommandWords) return HomeCommandRisk.NotACommand;
        return words.Count > 0 && QuestionWords.Contains(words[0]) ? HomeCommandRisk.Normal : HomeCommandRisk.Sensitive;
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

    internal static List<string> Words(string text)
    {
        var words = new List<string>();
        var current = new StringBuilder();
        foreach (var c in text.Normalize(NormalizationForm.FormC))
        {
            if (char.IsLetterOrDigit(c)) current.Append(char.ToLower(c, CultureInfo.InvariantCulture));
            else if (c is '\'' or '’') continue;
            else if (current.Length > 0)
            {
                words.Add(current.ToString());
                current.Clear();
            }
        }
        if (current.Length > 0) words.Add(current.ToString());
        // A leading greeting or the companion's name is not the first word of the request ("hey Martlet, is the door locked?").
        while (words.Count > 1 && words[0] is "hey" or "hi" or "ok" or "okay" or "please" or "martlet" or "so" or "and" or "um" or "uh")
            words.RemoveAt(0);
        return words;
    }
}
