using System.ComponentModel;
using System.Diagnostics;

namespace Martlet.Desktop;

/// <summary>The "Add a free API key" prompt, in one wording for every place that shows it (the Recommended setup review, Home's
/// recommended-setup notice and Companion › Thinking): no hosted provider has a saved key, so Martlet points to NVIDIA Build's
/// free keys. A saved key keeps Martlet able to reply when the computers are offline or have no room for thinking: the
/// recommended setup can then plan Thinking online where no computer has room for it. A local model still answers first
/// sooner, so where Martlet can already reply the key goes to If Thinking fails, not to Thinking itself.</summary>
internal static class FreeKeyPrompt
{
    internal const string Url = MainWindow.NvidiaKeyPage;
    internal const string Title = "Add a free API key";
    internal const string GetLabel = "Get a free key";
    internal const string AddLabel = "Add your key";
    internal const string OpenThinkingLabel = "Open Thinking";
    internal const string Tip = "Get a free key from NVIDIA Build and add it here. Then Martlet can still reply when your computers " +
        "are offline or have no room for thinking. The key costs nothing. When Martlet uses it, NVIDIA gets and logs what you say.";
    internal const string ProblemTitle = "No computer can do thinking, so Martlet can't reply";
    internal const string OpenedText = "NVIDIA Build opened in your browser. Sign in, create a key and copy it. Then choose Add your key.";
    /// <summary>The line Home adds where Martlet can't reply (Set up thinking, Thinking isn't working) while no key is saved.</summary>
    internal const string HealthHint = "A free API key from NVIDIA Build keeps Martlet able to reply when your computers can't.";

    /// <summary>Why Martlet can't reply, and how to fix it: a free key when none is saved, else Companion › Thinking.</summary>
    internal static string Problem(bool offerKey) => "None of the computers that answer has room for a Thinking model." + (offerKey
        ? " Add a free API key from NVIDIA Build, and Martlet thinks online instead. NVIDIA gets and logs what you say to Martlet."
        : " Choose where Thinking runs in Companion › Thinking.");

    /// <summary>The prompt shows only when no hosted provider has a saved key.</summary>
    internal static bool Shows(IReadOnlyCollection<string> configuredProviders) => configuredProviders.Count == 0;

    /// <summary>NVIDIA Build, whose key is free: its pages don't warn that requests may cost money.</summary>
    internal static bool IsFree(string? baseUrl) =>
        string.Equals(baseUrl, Martlet.Core.Settings.ChatCompletionsEndpointCatalog.NvidiaBuildBaseUrl, StringComparison.Ordinal);

    /// <summary>Where Add your key puts the key: Thinking itself when Martlet can't reply (<paramref name="cannotReply"/>),
    /// else If Thinking fails; with no key to offer, Companion › Thinking only.</summary>
    internal static FreeKeyUse Use(bool offerKey, bool cannotReply) =>
        !offerKey ? FreeKeyUse.None : cannotReply ? FreeKeyUse.Thinking : FreeKeyUse.Fallback;

    /// <summary>Opens NVIDIA Build's key page in the browser; why it couldn't, or null.</summary>
    internal static string? OpenKeyPage()
    {
        try
        {
            Process.Start(new ProcessStartInfo(Url) { UseShellExecute = true })?.Dispose();
            return null;
        }
        catch (Exception error) when (error is Win32Exception or InvalidOperationException)
        {
            return $"Couldn't open the browser ({error.Message}). Go to {Url} yourself.";
        }
    }
}

/// <summary>Where Add your key (FreeKeyPrompt) puts the key: nowhere (Companion › Thinking opens), Thinking's own route
/// (A cloud provider with NVIDIA Build) or If Thinking fails with NVIDIA Build.</summary>
internal enum FreeKeyUse { None, Thinking, Fallback }
