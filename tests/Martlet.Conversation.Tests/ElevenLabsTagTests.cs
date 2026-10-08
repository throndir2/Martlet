using Martlet.Audio;
using Martlet.Core.Contracts;
using Martlet.Core.Settings;
using Martlet.Providers;

namespace Martlet.Conversation.Tests;

public sealed class ElevenLabsTagTests
{
    private static readonly IReadOnlyList<VoiceTag> ElevenLabs = SpeechEngines.ElevenLabs.Tags;

    private static string Segment(string input, int split)
    {
        var segmenter = new SpeechSegmenter(1536, 16_384, tags: ElevenLabs);
        var pieces = segmenter.Push(input[..split]).Concat(segmenter.Push(input[split..])).Concat(segmenter.Finish()).ToArray();
        return string.Join('|', pieces.Where(x => x.Text is not null).Select(x => x.Text));
    }

    [Theory]
    [InlineData("[whispers] It's a secret. Shh.", "[whispers] It's a secret.|Shh.")]
    [InlineData("That's hilarious [laughs] okay. Next.", "That's hilarious [laughs] okay.|Next.")]
    [InlineData("[sad] I miss you. Okay.", "[sad] I miss you.|Okay.")]
    [InlineData("Ahem [clears throat] listen. Done.", "Ahem [clears throat] listen.|Done.")]
    // Another spelling of a tag ElevenLabs has is sent as ElevenLabs spells it; a tag it has no cue for is dropped.
    [InlineData("[whispering] Psst. Hi.", "[whispers] Psst.|Hi.")]
    [InlineData("Oh [laugh] stop. Fine.", "Oh [laughs] stop.|Fine.")]
    [InlineData("[gasp] What? Okay.", "What?|Okay.")]
    public void ElevenLabs_tags_reach_its_voice_as_it_spells_them_on_every_split(string input, string expected)
    {
        for (var split = 0; split <= input.Length; split++)
            Assert.Equal(expected, Segment(input, split));
    }

    [Theory]
    [InlineData("[laughs] Hi there. [whispers] A secret.", "Hi there. A secret.")]
    [InlineData("[excited] We did it! [shouts] Yes!", "We did it! Yes!")]
    [InlineData("Well [sighs] fine. [curious] Why?", "Well fine. Why?")]
    public void Chat_and_captions_never_show_ElevenLabs_tags_on_every_split(string input, string expected)
    {
        Assert.Equal(expected, VoiceTags.Strip(input));
        for (var split = 0; split <= input.Length; split++)
        {
            var stripper = new VoiceTagStripper(voiceTags: ElevenLabs);
            Assert.Equal(expected, stripper.Push(input[..split]) + stripper.Push(input[split..]) + stripper.Finish());
        }
    }

    [Fact]
    public void The_Thinking_prompt_lists_exactly_ElevenLabs_tags_and_their_cues_are_the_existing_ones()
    {
        var prompt = VoiceTags.Instructions(SpeechEngines.ElevenLabs, null);
        Assert.NotNull(prompt);
        Assert.Contains("spoken aloud by ElevenLabs", prompt);
        Assert.All(ElevenLabs, tag => Assert.Contains($"{tag.Text} - {tag.Usage}", prompt));
        Assert.DoesNotContain("[laugh] -", prompt);
        // Every ElevenLabs cue is one the character's voice emotes already follow, so the cue list doesn't grow.
        Assert.Equal(SpeechEngines.All.SelectMany(e => e.Tags).Select(t => t.Cue).Distinct().Order(), VoiceTags.Cues.Order());
        Assert.DoesNotContain(SpeechEngines.ElevenLabs, SpeechEngines.All);
        Assert.Equal(new VoiceAbilities(true, true, EmotionSupport.Tags), SpeechEngines.ElevenLabs.Abilities);
    }

    [Fact]
    public void A_request_spoken_by_ElevenLabs_keeps_its_tags_and_has_only_that_voice()
    {
        var target = new ElevenLabsVoiceTarget("fixtureVoice00000001", ElevenLabsSetup.V4Turbo);
        var limits = new SpeechSynthesisLimits();
        var speech = new SpeechOutput(ElevenLabsSpeechSynthesisStream.Selection(target), new OutputSelection(OutputPolicy.DefaultAtStart), limits);
        ConversationRequest Request(HostSpeechTarget? host = null) => new(new BoundedTextInput("Hi.", "Test."),
            new TextModelSelection(ChatCompletionsSetup.Alias, "model"), new TextGenerationLimits(), new ConversationLimits(), speech,
            new ChatCompletionsTarget("http://127.0.0.1:8080/v1", Keyless: true), hostSpeech: host) { ElevenLabsVoice = target };
        var request = Request();
        request.Validate();
        Assert.Same(ElevenLabs, request.KeptVoiceTags);
        var host = new HostSpeechTarget("https://127.0.0.1:9443", "host", "sha256:" + new string('0', 64), "device", Guid.NewGuid(),
            "chatterbox-turbo", Guid.NewGuid(), new string('0', 64));
        Assert.Throws<ContractException>(() => Request(host).Validate());
    }
}
