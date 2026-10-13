using Martlet.Core.Contracts;

namespace Martlet.Core.Settings;

public static class OpenAiSetup
{
    public const string Origin = "https://api.openai.com";
    public const string Pricing = "https://openai.com/api/pricing/";
    public const string Retention = "https://platform.openai.com/docs/guides/your-data";
    public const string Disclosure = "Cloud requests may cost money. " +
        "Your text, audio or reply text is sent only when you start an action. Saving settings sends nothing.";

    public static string Alias(SetupRole role) => role switch
    {
        SetupRole.Stt => "openai-stt", SetupRole.Llm => "openai-llm", SetupRole.Tts => "openai-tts",
        _ => throw new ContractException(ErrorCode.InvalidContract, "Choose STT, LLM or TTS.")
    };

    public static string Boundary(SetupRole role) => role switch
    {
        SetupRole.Stt => "Microphone audio can be sent for transcription.",
        SetupRole.Llm => "Your messages and recent conversation can be sent for replies.",
        SetupRole.Tts => "Reply text can be sent to create speech.",
        _ => throw new ContractException(ErrorCode.InvalidContract, "Choose STT, LLM or TTS.")
    };

    internal static void UpstreamId(string value)
    {
        ContractRules.Require(value is { Length: >= 1 and <= 128 } &&
            value.All(c => char.IsAsciiLetterOrDigit(c) || c is '-' or '_' or '.'),
            "Enter a model or voice ID using letters, numbers, dots, underscores or hyphens.");
    }
}
