using System.Text;

namespace Martlet.Conversation;

/// <summary>Stops a reply where the model starts to write prompt text instead of its own words: Martlet's notes label
/// ([MARTLET_NOTES]), a chat template token (&lt;|im_start|&gt;, [INST], &lt;start_of_turn&gt;) or, at the start of a line, a
/// speaker label or instruction heading ("User:", "Owner:", "### Response"). Models (small local ones most) sometimes go on
/// past the end of their turn and write the next user message or copy the notes and instructions they were sent; that text
/// must never be shown or spoken. Text that could still become a marker is held back until the next piece tells (a token or
/// two); the voice waits for a whole sentence anyway, so this adds nothing to when the first word is heard. Once
/// <see cref="Cut"/>, nothing more passes.</summary>
public sealed class PromptTextGuard
{
    // Anywhere in a reply: Martlet's notes label and chat template tokens a model writes only when it leaks its prompt.
    private static readonly string[] Anywhere =
    [
        "[MARTLET_NOTES]", "[/MARTLET_NOTES]", "MARTLET_NOTES", "<|im_start|>", "<|im_end|>", "<|eot_id|>", "<|start_header_id|>",
        "<|end_header_id|>", "<|endoftext|>", "<|end|>", "<|user|>", "<|assistant|>", "<|system|>", "<start_of_turn>",
        "<end_of_turn>", "</s>", "[INST]", "[/INST]", "<<SYS>>"
    ];

    // At the start of a line (after spaces or Markdown emphasis): a speaker label or an instruction heading.
    private static readonly string[] LineStart =
    [
        "User:", "Owner:", "Human:", "Assistant:", "System:", "### Instruction", "### Response", "### Input", "### User",
        "### Assistant", "### System"
    ];

    private readonly StringBuilder pending = new();
    private bool atLineStart = true;

    /// <summary>The marker the reply was cut at, or null while it wasn't.</summary>
    public string? Cut { get; private set; }

    /// <summary>The part of <paramref name="piece"/> (and of what was held back) that is safe to show and speak now.</summary>
    public string Push(string piece)
    {
        if (Cut is not null || string.IsNullOrEmpty(piece)) return "";
        pending.Append(piece);
        return Drain(final: false);
    }

    /// <summary>What was held back at the end of the reply (it never became a marker).</summary>
    public string Finish() => Cut is null ? Drain(final: true) : "";

    /// <summary><paramref name="text"/> up to its first marker (all of it when there is none).</summary>
    public static string Before(string text)
    {
        var guard = new PromptTextGuard();
        return guard.Push(text) + guard.Finish();
    }

    private string Drain(bool final)
    {
        var text = pending.ToString();
        for (var i = 0; i < text.Length; i++)
        {
            if (i == 0 ? atLineStart : text[i - 1] == '\n')
            {
                var j = i;
                while (j < text.Length && text[j] is ' ' or '\t' or '*' or '_' or '>') j++;
                if (j == text.Length && !final) return Hold(text, i);
                switch (Find(text, j, LineStart, out var label))
                {
                    case Match.Whole: return Stop(text, i, label!);
                    case Match.Part when !final: return Hold(text, i);
                }
            }
            switch (Find(text, i, Anywhere, out var token))
            {
                case Match.Whole: return Stop(text, i, token!);
                case Match.Part when !final: return Hold(text, i);
            }
        }
        pending.Clear();
        if (text.Length > 0) atLineStart = text[^1] == '\n';
        return text;
    }

    private enum Match { None, Part, Whole }

    // Whether text from start begins with a marker (Whole) or is the start of one that more text may finish (Part).
    private static Match Find(string text, int start, string[] markers, out string? found)
    {
        found = null;
        var rest = text.AsSpan(start);
        if (rest.Length == 0) return Match.None;
        var part = false;
        foreach (var marker in markers)
        {
            if (rest.StartsWith(marker, StringComparison.OrdinalIgnoreCase))
            {
                found = marker;
                return Match.Whole;
            }
            part |= rest.Length < marker.Length && marker.AsSpan().StartsWith(rest, StringComparison.OrdinalIgnoreCase);
        }
        return part ? Match.Part : Match.None;
    }

    private string Hold(string text, int at)
    {
        if (at > 0) atLineStart = text[at - 1] == '\n';
        pending.Clear().Append(text, at, text.Length - at);
        return text[..at];
    }

    private string Stop(string text, int at, string marker)
    {
        Cut = marker;
        pending.Clear();
        return text[..at];
    }
}
