using System.IO;
using System.Text.Json;
using Martlet.Avatar.Hosting;
using Martlet.Conversation;

namespace Martlet.Desktop;

// Where Martlet's spoken words are shown: speech bubbles beside the character are on by default (they only appear while the
// character is showing), subtitles are off. Saved locally beside the other desktop preferences (speech-display.json).
// By default the bubble follows the character's head (moves, zoom, pan) and BubbleOffsetX/BubbleOffsetY nudge it from there
// (device-independent pixels, +x right, +y down). StaticBubble keeps it in one place instead: the offsets are then measured
// from the top-left of the character's screen.
internal sealed record SpeechDisplayPreferences(bool SpeechBubbles = true, bool Subtitles = false, bool StaticBubble = false,
    double BubbleOffsetX = 0, double BubbleOffsetY = 0)
{
    internal const double MaximumOffset = 4000;

    internal RendererSay Bubble(string? text) => new(text, StaticBubble,
        Math.Clamp(double.IsFinite(BubbleOffsetX) ? BubbleOffsetX : 0, -MaximumOffset, MaximumOffset),
        Math.Clamp(double.IsFinite(BubbleOffsetY) ? BubbleOffsetY : 0, -MaximumOffset, MaximumOffset));

    internal const string FileName = "speech-display.json";

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

// Shows each sentence Martlet speaks as a speech bubble beside the character and/or a subtitle on the active screen. After the
// voice fails, the conversation still feeds it each sentence that wasn't said, timed for reading.
// Create on the UI thread: the feed is read there, so the overlay and preferences are only touched by the dispatcher.
internal sealed class SpeechCaptions : IDisposable
{
    private static readonly TimeSpan Linger = TimeSpan.FromSeconds(1.5);
    private static readonly TimeSpan PreviewTime = TimeSpan.FromSeconds(4);
    private readonly AvatarController avatar;
    private readonly string? directory;
    private readonly CancellationTokenSource lifetime = new();
    private SubtitleOverlayWindow? overlay;
    private Task<RendererBubble?> bubbleSends = Task.FromResult<RendererBubble?>(null);
    private string? bubbleText;
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

    /// <summary>Where the overlay last put the bubble (or that it hid it); null until it has answered.</summary>
    internal RendererBubble? LastBubble { get; private set; }

    /// <summary>Raised on the UI thread after the preferences change, so every place showing them can follow.</summary>
    internal event Action? Changed;

    internal bool Update(SpeechDisplayPreferences next)
    {
        Preferences = next;
        if (!next.Subtitles) overlay?.ClearLine();
        if (bubbleText is not null) Say(next.SpeechBubbles ? bubbleText : null);
        var saved = next.Save(directory);
        Changed?.Invoke();
        return saved;
    }

    /// <summary>Shows a sample bubble beside the showing character for a few seconds. Where the overlay put it, or null when the
    /// character is hidden or its overlay did not take the bubble.</summary>
    internal async Task<RendererBubble?> PreviewAsync(string text)
    {
        if (!avatar.IsShowing) return null;
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
                // A sung line grows word by word in the subtitles (karaoke); the bubble shows the whole line once.
                var bubble = line.Sung ?? line.Text;
                if (Preferences.SpeechBubbles && avatar.IsShowing && (line.Sung is null || bubble != bubbleText)) Say(bubble);
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
        // A Read more offer that waited for Martlet to finish speaking takes the bubble now.
        if (offer is not null)
        {
            ShowOffer();
            return;
        }
        if (bubbleText is not null) Say(null);
    }

    // Bubble updates are chained so a late "hide" can never overtake the next sentence.
    private void Say(string? text, string? button = null)
    {
        bubbleText = text;
        bubbleSends = SendAfterAsync(bubbleSends, Preferences.Bubble(text) with { Button = text is null ? null : button });
    }

    /// <summary>How long the speech bubble's Read more offer stays.</summary>
    internal static TimeSpan OfferTime { get; } = TimeSpan.FromSeconds(20);

    /// <summary>The app the shown (or waiting) Read more offer is for, or null.</summary>
    internal string? Offered { get; private set; }

    /// <summary>Raised on the UI thread when an offer appears or goes away.</summary>
    internal event Action? OfferChanged;

    /// <summary>Shows <paramref name="text"/> with a <paramref name="button"/> (Read more) in a bubble beside the showing
    /// character for <see cref="OfferTime"/>, or until Martlet speaks again or the button is pressed (<see cref="TakeOffer"/>).
    /// <paramref name="key"/> is the app it is for. While Martlet still speaks, it waits for the bubble to be free first. False
    /// when speech bubbles are off or the character isn't showing.</summary>
    internal bool Offer(string key, string text, string button)
    {
        if (!Preferences.SpeechBubbles || !avatar.IsShowing) return false;
        Offered = key;
        offer = (text, button);
        if (bubbleText is null) ShowOffer();
        else _ = DropWaitingAsync(key);
        OfferChanged?.Invoke();
        return true;
    }

    // An offer still waiting for the bubble after twice its time is dropped.
    private async Task DropWaitingAsync(string key)
    {
        try { await Task.Delay(OfferTime * 2, lifetime.Token); }
        catch (OperationCanceledException) { return; }
        if (Offered != key || offerShown) return;
        Offered = null;
        offer = null;
        OfferChanged?.Invoke();
    }

    /// <summary>The button was pressed: the offer goes away and its app is returned (null when there was none).</summary>
    internal string? TakeOffer()
    {
        var key = Offered;
        if (key is null) return null;
        Offered = null;
        offer = null;
        if (offerShown && bubbleText is not null) Say(null);
        offerShown = false;
        OfferChanged?.Invoke();
        return key;
    }

    private (string Text, string Button)? offer;
    private bool offerShown;

    private void ShowOffer()
    {
        if (offer is not { } waiting || Offered is not { } key) return;
        var id = ++current;
        offerShown = true;
        Say(waiting.Text, waiting.Button);
        offer = null;
        _ = EndOfferAsync(id, key);
    }

    private async Task EndOfferAsync(long id, string key)
    {
        try { await Task.Delay(OfferTime, lifetime.Token); }
        catch (OperationCanceledException) { return; }
        if (Offered != key) return;
        Offered = null;
        offerShown = false;
        if (id == current && bubbleText is not null) Say(null);
        OfferChanged?.Invoke();
    }

    private async Task<RendererBubble?> SendAfterAsync(Task previous, RendererSay say)
    {
        await previous;
        try { return LastBubble = await avatar.SayAsync(say, lifetime.Token); }
        catch (Exception error) when (error is IOException or InvalidDataException or InvalidOperationException or
            OperationCanceledException or TimeoutException or JsonException) { return null; }
    }

    public void Dispose()
    {
        lifetime.Cancel();
        overlay?.Close();
        overlay = null;
    }
}
