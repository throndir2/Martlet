using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Martlet.Core.Settings;
using Martlet.Providers;

namespace Martlet.Desktop.Tests;

public sealed class SenseModelCardsTests
{
    private const string OpenRouter = "https://openrouter.ai/api/v1";
    private static readonly DateTimeOffset At = new(2026, 10, 8, 19, 0, 0, TimeSpan.Zero);

    private static SetupRoute Thinking(string origin, string model, SetupRouteType type = SetupRouteType.ChatCompletions) => new()
    {
        Role = SetupRole.Llm, RouteType = type, ProviderAlias = ChatCompletionsSetup.Alias, Origin = origin, ModelId = model,
        ConfigurationRevision = Guid.NewGuid()
    };

    private static DeepThinkingSettings Endpoint(string origin, string model) =>
        new() { Place = DeepThinkingPlace.Endpoint, Origin = origin, ModelId = model };

    private static readonly DeepThinkingSettings Diva = new()
    {
        Place = DeepThinkingPlace.Host, ModelId = "qwen2.5vl:7b", HostId = "diva", HostOrigin = "https://diva.local:9443",
        HostSpkiFingerprint = "sha256:" + new string('0', 64), HostDeviceId = "device", HostCredentialId = Guid.NewGuid()
    };

    private static SenseModel Own(DeepThinkingSettings model) => new() { Source = SenseSource.Own, Own = model, ChosenAt = At };

    [Fact]
    public void Now_says_which_model_takes_each_kind()
    {
        var thinking = Thinking(GenerationSupport.LocalOllamaChatBaseUrl, "qwen3:8b");
        Assert.Equal("Use the same model as the text model (Thinking: qwen3:8b).", MainWindow.SenseNow(SenseKind.Image, new(), thinking));
        Assert.Equal("Use the same model as the text model (Thinking isn't set up yet).", MainWindow.SenseNow(SenseKind.Audio, new(), null));

        var senses = new SenseModels
        {
            Image = Own(Endpoint(GenerationSupport.LocalOllamaChatBaseUrl, "qwen2.5vl:7b")),
            Audio = new() { Source = SenseSource.OtherSense }
        };
        Assert.StartsWith("A model of its own: Ollama on this PC (qwen2.5vl:7b), chosen on ", MainWindow.SenseNow(SenseKind.Image, senses, thinking));
        Assert.Equal("Use the same model as the image model, now Ollama on this PC (qwen2.5vl:7b).",
            MainWindow.SenseNow(SenseKind.Audio, senses, thinking));
        var circle = new SenseModels { Image = new() { Source = SenseSource.OtherSense }, Audio = new() { Source = SenseSource.OtherSense } };
        Assert.Contains("the text model, because it uses the same model as this one", MainWindow.SenseNow(SenseKind.Image, circle, thinking));
    }

    [Fact]
    public void The_saved_choice_shows_as_its_option()
    {
        Assert.Equal(MainWindow.SenseChoice.Thinking, MainWindow.ChoiceOf(new()));
        Assert.Equal(MainWindow.SenseChoice.OtherSense, MainWindow.ChoiceOf(new() { Source = SenseSource.OtherSense }));
        Assert.Equal(MainWindow.SenseChoice.ThisPc, MainWindow.ChoiceOf(Own(Endpoint(GenerationSupport.LocalOllamaChatBaseUrl, "qwen2.5vl:7b"))));
        Assert.Equal(MainWindow.SenseChoice.Cloud, MainWindow.ChoiceOf(Own(Endpoint(OpenRouter, "google/gemini-2.5-flash"))));
        // A model app on this PC is chosen like any server.
        Assert.Equal(MainWindow.SenseChoice.Cloud, MainWindow.ChoiceOf(Own(Endpoint("http://127.0.0.1:1234/v1", "qwen2.5-vl-7b"))));
        Assert.Equal(MainWindow.SenseChoice.Computer, MainWindow.ChoiceOf(Own(Diva)));
    }

    [Fact]
    public void Known_says_what_Martlet_found_out_and_where_else_what_the_name_says()
    {
        var abilities = new ModelAbilities()
            .With(new() { Origin = OpenRouter, ModelId = "acme/sight", Sees = true, Hears = false, Source = "OpenRouter's model list", CheckedAt = At })
            .With(new() { Origin = Diva.HostOrigin!, ModelId = Diva.ModelId!, Sees = false, Source = "a refused picture", CheckedAt = At });
        var thinking = Thinking(GenerationSupport.LocalOllamaChatBaseUrl, "qwen3:8b");

        var sight = new SenseModels { Image = Own(Endpoint(OpenRouter, "acme/sight")), Audio = new() { Source = SenseSource.OtherSense } };
        Assert.StartsWith("acme/sight sees pictures, as Martlet found out (last check on ", MainWindow.SenseKnown(SenseKind.Image, sight, thinking, abilities));
        Assert.EndsWith(": OpenRouter's model list).", MainWindow.SenseKnown(SenseKind.Image, sight, thinking, abilities));
        Assert.StartsWith("acme/sight doesn't hear recordings, as Martlet found out (last check on ",
            MainWindow.SenseKnown(SenseKind.Audio, sight, thinking, abilities));

        // A paired computer's model is known by its gateway's origin.
        var diva = new SenseModels { Image = Own(Diva) };
        Assert.EndsWith(": a refused picture).", MainWindow.SenseKnown(SenseKind.Image, diva, thinking, abilities));
        Assert.StartsWith("qwen2.5vl:7b doesn't see pictures, as Martlet found out", MainWindow.SenseKnown(SenseKind.Image, diva, thinking, abilities));

        // The text model by its name, and an unknown model.
        Assert.Equal("By its name, qwen3:8b doesn't see pictures. Test vision makes sure.", MainWindow.SenseKnown(SenseKind.Image, new(), thinking, abilities));
        var unknown = new SenseModels { Image = Own(Endpoint("https://api.example.com/v1", "acme/mystery")) };
        Assert.StartsWith("Martlet doesn't know yet whether acme/mystery sees pictures.", MainWindow.SenseKnown(SenseKind.Image, unknown, thinking, abilities));
        Assert.Null(MainWindow.SenseKnown(SenseKind.Image, new(), null, abilities));
        // A route that takes no recordings says so, not a name.
        Assert.Contains("gets no recordings on this route",
            MainWindow.SenseKnown(SenseKind.Audio, new(), Thinking("https://api.openai.com", "gpt-4.1-mini", SetupRouteType.OpenAi), abilities));
    }

    [Fact]
    public void Sent_says_what_goes_where()
    {
        Assert.Equal("Pictures stay on this PC: Ollama describes them here.",
            MainWindow.SenseSent(SenseKind.Image, Endpoint(GenerationSupport.LocalOllamaChatBaseUrl, "qwen2.5vl:7b")));
        Assert.Equal("Pictures of your screen or camera and a few lines of the conversation go to openrouter.ai, and requests may cost money.",
            MainWindow.SenseSent(SenseKind.Image, Endpoint(OpenRouter, "acme/sight")));
        Assert.Equal("Recordings of your voice and of what this PC plays, with a few lines of the conversation, go to openrouter.ai, and " +
            "requests may cost money.", MainWindow.SenseSent(SenseKind.Audio, Endpoint(OpenRouter, "google/gemini-2.5-flash")));
        Assert.Contains("on this PC, an app that may pass them on", MainWindow.SenseSent(SenseKind.Image, Endpoint("http://127.0.0.1:1234/v1", "qwen")));
        Assert.Equal("Pictures of your screen or camera and a few lines of the conversation go to diva through its paired, pinned connection.",
            MainWindow.SenseSent(SenseKind.Image, Diva));
        Assert.Contains("are sent there, and requests may cost money.", MainWindow.SenseConsent(SenseKind.Audio, "OpenRouter", onThisPc: false));
        Assert.StartsWith("I choose OpenRouter as the audio model. Recordings of your voice and of what this PC plays",
            MainWindow.SenseConsent(SenseKind.Audio, "OpenRouter", onThisPc: false));
    }

    [Fact]
    public void Thinking_says_it_is_the_text_model_and_where_pictures_and_recordings_go()
    {
        var omni = Thinking(GenerationSupport.LocalOllamaChatBaseUrl, "gemma4:e2b");
        Assert.Equal("Thinking is the text model: it writes every reply. Pictures go to Thinking itself (Companion › Vision), and recordings " +
            "go to Thinking itself (Companion › Hearing).", MainWindow.TextModelText(new(), omni, null));
        var senses = new SenseModels { Image = Own(Endpoint(GenerationSupport.LocalOllamaChatBaseUrl, "qwen2.5vl:7b")) };
        Assert.Contains("Pictures go to the image model, Ollama on this PC (qwen2.5vl:7b)", MainWindow.TextModelText(senses, omni, null));
        // A model of its own that is exactly Thinking's reads as the text model.
        var same = new SenseModels { Audio = Own(Endpoint(GenerationSupport.LocalOllamaChatBaseUrl, "gemma4:e2b")) };
        Assert.Contains("recordings go to Thinking itself", MainWindow.TextModelText(same, omni, null));
        // A text-only Thinking with no image or audio model: nothing takes them.
        var text = Thinking(GenerationSupport.LocalOllamaChatBaseUrl, "qwen3:8b");
        Assert.Equal("Thinking is the text model: it writes every reply. Pictures go to no model, so Martlet can't see (Companion › Vision), " +
            "and recordings go to no model, so Thinking gets the transcript only (Companion › Hearing).", MainWindow.TextModelText(new(), text, null));
        // Recordings to the same model as a paired computer's image model go nowhere: its gateway takes no recordings.
        var host = new SenseModels { Image = Own(Diva), Audio = new() { Source = SenseSource.OtherSense } };
        Assert.Contains("recordings go to no model, so Thinking gets the transcript only", MainWindow.TextModelText(host, text, null));
    }

    [Fact]
    public void Each_test_says_what_it_sends_and_where()
    {
        Assert.Equal("Sends qwen2.5vl:7b one picture of a single word, drawn on this PC (never your screen), and asks which word it shows; " +
            "it stays on this PC. Choosing a model already asks its server what it takes, when the server says.",
            MainWindow.TestAbout(SenseKind.Image, "qwen2.5vl:7b", local: true, "Ollama on this PC"));
        Assert.Contains("which word it heard, to OpenRouter. It's one small request that may cost a little.",
            MainWindow.TestAbout(SenseKind.Audio, "google/gemini-2.5-flash", local: false, "OpenRouter"));
    }

    [Fact]
    public async Task The_test_picture_is_one_word_drawn_on_white()
    {
        var pictures = new Dictionary<string, BoundedImage>();
        await OnDispatcher(() =>
        {
            foreach (var word in new[] { "lighthouse", "snowman" }) pictures[word] = VisionTestPicture.Render(word);
        });
        foreach (var picture in pictures.Values)
        {
            Assert.Equal(ImageMediaType.Png, picture.MediaType);
            Assert.Equal((VisionTestPicture.Width, VisionTestPicture.Height), (picture.Width, picture.Height));
            Assert.InRange(picture.ByteCount, 1_000, BoundedImage.HardMaxBytes);
            var frame = BitmapFrame.Create(new MemoryStream(picture.Content.ToArray()), BitmapCreateOptions.None, BitmapCacheOption.OnLoad);
            var pixels = new byte[frame.PixelWidth * frame.PixelHeight * 4];
            new FormatConvertedBitmap(frame, PixelFormats.Bgra32, null, 0).CopyPixels(pixels, frame.PixelWidth * 4, 0);
            var dark = Enumerable.Range(0, pixels.Length / 4).Count(i => pixels[i * 4] < 128) / (double)(pixels.Length / 4);
            // The word covers part of the picture; the corners stay white.
            Assert.InRange(dark, 0.01, 0.5);
            Assert.True(pixels[0] > 250 && pixels[^4] > 250);
        }
        Assert.False(pictures["lighthouse"].Content.Span.SequenceEqual(pictures["snowman"].Content.Span));
    }

    private static async Task OnDispatcher(Action action)
    {
        var finished = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var thread = new Thread(() =>
        {
            try { action(); finished.SetResult(); }
            catch (Exception error) { finished.SetException(error); }
            finally { Dispatcher.CurrentDispatcher.InvokeShutdown(); }
        }) { IsBackground = true };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        await finished.Task.WaitAsync(TimeSpan.FromSeconds(20));
        Assert.True(thread.Join(TimeSpan.FromSeconds(5)));
    }
}
