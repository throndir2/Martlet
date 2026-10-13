using System.Text;
using Martlet.Core.Contracts;

namespace Martlet.Core.Settings;

/// <summary>The rules for the user's prompt edits (<see cref="PromptSettings"/>): what a saved edit may contain, and how edits
/// saved by an older Martlet are read.</summary>
internal static class PromptEditRules
{
    /// <summary>Each edit names a known prompt, is short enough, has no control characters and doesn't empty a prompt that is
    /// the message itself; all edits together stay under the settings limit.</summary>
    public static void Validate(PromptSettings settings)
    {
        var overrides = settings.Overrides;
        ContractRules.Require(overrides is not null, "Prompt edits are required.");
        foreach (var (id, text) in overrides!)
        {
            ContractRules.Require(PromptCatalog.Find(id) is not null, $"Unknown prompt '{id}'.");
            ContractRules.Require(text is not null && text.Length <= PromptSettings.MaximumTextCharacters &&
                !text.Any(c => char.IsControl(c) && c is not '\n' and not '\r' and not '\t'),
                $"The {PromptCatalog.Find(id)!.Title} prompt must be at most {PromptSettings.MaximumTextCharacters} characters without control characters.");
            ContractRules.Require(!PromptCatalog.Required(id) || !string.IsNullOrWhiteSpace(text),
                $"The {PromptCatalog.Find(id)!.Title} prompt can't be empty: it is the message itself.");
        }
        try
        {
            ContractRules.Require(overrides.Values.Sum(new UTF8Encoding(false, true).GetByteCount) <= PromptSettings.MaximumAggregateUtf8Bytes,
                "Edited prompts exceed the combined 48 KiB settings limit.");
        }
        catch (EncoderFallbackException)
        {
            throw new ContractException(ErrorCode.InvalidContract, "A prompt contains invalid Unicode.");
        }
    }

    /// <summary>Prompts an older Martlet had (the Thinking model's character palettes, the response styles a reply was
    /// picked from, the glances' Earlier remarks, which What you said lately replaced, the On a call check-in's, which
    /// Talking to someone else replaced, and Remembering and learning names together, now two requests of their own); their saved
    /// edits are dropped.</summary>
    public static bool Retired(string id) => id is "character_theme" or "style" or "style_helpful" or "style_sarcastic" or
        "style_silly" or "style_distracted" or "style_playful_teasing" or "glance_remarks" or "check_in_call" or "after_reply";

    /// <summary><paramref name="edits"/> as this Martlet reads them: without edits of retired prompts and without the {style}
    /// lines of an old Persona edit. Edits that need neither are returned as they are.</summary>
    public static IReadOnlyDictionary<string, string> Upgrade(IReadOnlyDictionary<string, string> edits) =>
        edits is not null && (edits.Keys.Any(Retired) || StyleLines(edits))
            ? edits.Where(e => !Retired(e.Key)).ToDictionary(e => e.Key,
                e => e.Key == PromptCatalog.Persona ? WithoutStyleLines(e.Value) : e.Value, StringComparer.Ordinal)
            : edits!;

    // A Persona prompt edited in an older Martlet can still have the {style} line it had then ("Dominant style for this reply:
    // {style}"). There are no response styles now, so those lines are dropped instead of sent as written.
    private static bool StyleLines(IReadOnlyDictionary<string, string> edits) =>
        edits.TryGetValue(PromptCatalog.Persona, out var persona) && persona?.Contains("{style}", StringComparison.Ordinal) == true;

    private static string WithoutStyleLines(string text) => text?.Contains("{style}", StringComparison.Ordinal) != true ? text! :
        string.Join('\n', text.Split('\n').Where(line => !line.Contains("{style}", StringComparison.Ordinal))).TrimEnd();
}
