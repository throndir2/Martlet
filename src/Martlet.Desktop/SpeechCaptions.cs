using System.IO;
using System.Text.Json;
using Martlet.Conversation;

namespace Martlet.Desktop;

// Where Martlet's spoken words are shown: speech bubbles beside the character are on by default (they only appear while the
// character is showing), subtitles are off. Saved locally beside the other desktop preferences.
internal sealed record SpeechDisplayPreferences(bool SpeechBubbles = true, bool Subtitles = false)
{
    private const string FileName = "speech-display.json";

    internal static SpeechDisplayPreferences Load(string? directory)
    {
        if (directory is null) return new();
        try
        {
            var path = Path.Combine(directory, FileName);
            return File.Exists(path) ? JsonSerializer.Deserialize<SpeechDisplayPreferences>(File.ReadAllText(path)) ?? new() : new();
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or JsonException) { return new(); }
    }

    internal bool Save(string? directory)
    {
        if (directory is null) return false;
        try
        {
            Directory.CreateDirectory(directory);
            var temporary = Path.Combine(directory, $"speech-display.{Guid.NewGuid():N}.tmp");
            try
            {
                File.WriteAllText(temporary, JsonSerializer.Serialize(this));
                File.Move(temporary, Path.Combine(directory, FileName), overwrite: true);
            }
            finally
            {
                if (File.Exists(temporary)) File.Delete(temporary);
            }
            return true;
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException) { return false; }
    }
}

// Shows each sentence Martlet speaks as a speech bubble beside the character and/or a subtitle on the active screen.
// Create on the UI thread: the feed is read there, so the overlay and preferences are only touched by the dispatcher.
internal sealed class SpeechCaptions : IDisposable
{
    private static readonly TimeSpan Linger = TimeSpan.FromSeconds(1.5);
    private static readonly TimeSpan PreviewTime = TimeSpan.FromSeconds(4);
    private readonly AvatarController avatar;
    private readonly string? directory;
    private readonly CancellationTokenSource lifetime = new();
    private SubtitleOverlayWindow? overlay;
    private Task<bool> bubbleSends = Task.FromResult(true);
    private bool bubbleShown;
    private long current;

    internal SpeechCaptions(AvatarController avatar, string? directory)
    {
        this.avatar = avatar;
        this.directory = directory;
        Preferences = SpeechDisplayPreferences.Load(directory);
        ReadAsync().Forget();
    }

    internal SpokenTextFeed Feed { get; } = new();
    internal SpeechDisplayPreferences Preferences { get; private set; }

    /// <summary>Raised on the UI thread after the preferences change, so every place showing them can follow.</summary>
    internal event Action? Changed;

    internal bool Update(SpeechDisplayPreferences next)
    {
        Preferences = next;
        if (!next.Subtitles) overlay?.ClearLine();
        if (!next.SpeechBubbles && bubbleShown) Say(null);
        var saved = next.Save(directory);
        Changed?.Invoke();
        return saved;
    }

    /// <summary>Shows a sample bubble beside the showing character for a few seconds. False when the character is hidden or
    /// its overlay did not take the bubble.</summary>
    internal async Task<bool> PreviewAsync(string text)
    {
        if (!avatar.IsShowing) return false;
        var id = ++current;
        Say(text);
        var shown = await bubbleSends;
        ClearAfterAsync(Task.Delay(PreviewTime, lifetime.Token), id).Forget();
        return shown;
    }

    private async Task ReadAsync()
    {
        try
        {
            await foreach (var line in Feed.Lines.ReadAllAsync(lifetime.Token))
            {
                var id = ++current;
                if (Preferences.SpeechBubbles && avatar.IsShowing) Say(line.Text);
                if (Preferences.Subtitles) (overlay ??= new()).ShowLine(line.Text);
                ClearAfterAsync(line.Finished, id).Forget();
            }
        }
        catch (OperationCanceledException) { }
    }

    private async Task ClearAfterAsync(Task finished, long id)
    {
        await Task.WhenAny(finished);
        try { await Task.Delay(Linger, lifetime.Token); }
        catch (OperationCanceledException) { return; }
        if (id != current) return;
        overlay?.ClearLine();
        if (bubbleShown) Say(null);
    }

    // Bubble updates are chained so a late "hide" can never overtake the next sentence.
    private void Say(string? text)
    {
        bubbleShown = text is not null;
        bubbleSends = SendAfterAsync(bubbleSends, text);
    }

    private async Task<bool> SendAfterAsync(Task previous, string? text)
    {
        await previous;
        try { return await avatar.SayAsync(text, lifetime.Token); }
        catch (Exception error) when (error is IOException or InvalidDataException or InvalidOperationException or
            OperationCanceledException or TimeoutException) { return false; }
    }

    public void Dispose()
    {
        lifetime.Cancel();
        overlay?.Close();
        overlay = null;
    }
}
