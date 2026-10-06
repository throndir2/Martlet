using Martlet.Discord.Calls;

namespace Martlet.Discord.Tests;

/// <summary>Martlet in your own Discord calls: the speaking-indicator heuristics on fixture pictures of Discord's layouts, who
/// spoke, the call lines, the saved mode and choosing the output for Martlet's voice.</summary>
public sealed class DiscordCallTests
{
    private const int Width = 640, Height = 360;
    // Discord's dark theme and its speaking green.
    private static readonly (byte R, byte G, byte B) Dark = (49, 51, 56), Green = (35, 165, 90), OldGreen = (67, 181, 129),
        Avatar = (88, 101, 242), YellowGreen = (160, 200, 40), Teal = (20, 160, 160);

    private sealed class Picture(int width = Width, int height = Height)
    {
        public readonly byte[] Pixels = Fill(width, height);
        public int W => width;
        public int H => height;

        private static byte[] Fill(int width, int height)
        {
            var pixels = new byte[width * height * 4];
            for (var i = 0; i < width * height; i++) Set(pixels, i, Dark);
            return pixels;
        }

        public void Ring(int cx, int cy, double radius, double thickness, (byte, byte, byte) color)
        {
            for (var y = 0; y < height; y++)
            for (var x = 0; x < width; x++)
            {
                var d = Math.Sqrt((x - cx) * (x - cx) + (y - cy) * (y - cy));
                if (d >= radius - thickness && d <= radius) Set(Pixels, y * width + x, color);
            }
        }

        public void Disc(int cx, int cy, double radius, (byte, byte, byte) color)
        {
            for (var y = 0; y < height; y++)
            for (var x = 0; x < width; x++)
                if ((x - cx) * (x - cx) + (y - cy) * (y - cy) <= radius * radius) Set(Pixels, y * width + x, color);
        }

        public void Box(int left, int top, int w, int h, (byte, byte, byte) color, int border = 0)
        {
            for (var y = top; y < top + h; y++)
            for (var x = left; x < left + w; x++)
                if (border == 0 || x < left + border || x >= left + w - border || y < top + border || y >= top + h - border)
                    Set(Pixels, y * width + x, color);
        }

        private static void Set(byte[] pixels, int index, (byte R, byte G, byte B) color)
        {
            pixels[index * 4] = color.B;
            pixels[index * 4 + 1] = color.G;
            pixels[index * 4 + 2] = color.R;
            pixels[index * 4 + 3] = 255;
        }
    }

    // A voice channel's member list: 24 px avatars, the speaker's with a 2 px green ring, names to the right.
    private static Picture MemberList(int speaking, (byte, byte, byte)? ring = null)
    {
        var picture = new Picture();
        for (var i = 0; i < 3; i++)
        {
            picture.Disc(40, 40 + i * 40, 11, Avatar);
            if (i == speaking) picture.Ring(40, 40 + i * 40, 14, 2.5, ring ?? Green);
        }
        return picture;
    }

    private sealed class FakeReader(Func<PixelRect, IReadOnlyList<TextLine>> read) : ICallTextReader
    {
        public List<(int Width, int Height)> Reads { get; } = [];
        public PixelRect? Area { get; set; }

        public Task<IReadOnlyList<TextLine>> ReadAsync(byte[] bgra, int width, int height, CancellationToken token)
        {
            Reads.Add((width, height));
            return Task.FromResult(read(Area ?? new(0, 0, width, height)));
        }
    }

    [Fact]
    public void SpeakingRingInTheMemberListIsFoundWithItsNameToTheRight()
    {
        var picture = MemberList(speaking: 1);
        var marks = DiscordSpeakingDetector.Find(picture.Pixels, picture.W, picture.H);
        var mark = Assert.Single(marks);
        Assert.Equal(SpeakingKind.Ring, mark.Kind);
        Assert.InRange(mark.Box.Width, 26, 30);
        Assert.InRange(mark.Box.Y + mark.Box.Height / 2, 78, 82);
        Assert.True(mark.NameArea.X >= mark.Box.Right && mark.NameArea.Y <= mark.Box.Y && mark.NameArea.Bottom >= mark.Box.Bottom);
    }

    [Fact]
    public void OlderDiscordGreenCountsToo() =>
        Assert.Single(DiscordSpeakingDetector.Find(MemberList(0, OldGreen).Pixels, Width, Height));

    [Fact]
    public void NobodySpeakingFindsNothing() => Assert.Empty(DiscordSpeakingDetector.Find(MemberList(-1).Pixels, Width, Height));

    [Fact]
    public void FilledGreenStatusDotsButtonsAndOtherGreensAreNotSpeaking()
    {
        var picture = new Picture();
        picture.Disc(100, 100, 6, Green);                 // an online status dot
        picture.Box(200, 200, 120, 40, Green);           // a green "Join Voice" button
        picture.Ring(400, 100, 14, 2.5, YellowGreen);     // not Discord's green
        picture.Ring(500, 100, 14, 2.5, Teal);
        picture.Ring(560, 300, 5, 1.5, Green);            // too small to be an avatar ring
        Assert.Empty(DiscordSpeakingDetector.Find(picture.Pixels, picture.W, picture.H));
    }

    [Fact]
    public void SpeakingTileInTheCallGridIsFoundWithItsNameAtTheBottomLeft()
    {
        var picture = new Picture(960, 540);
        picture.Box(20, 20, 440, 248, (30, 31, 34));
        picture.Box(20, 20, 440, 248, Green, border: 3);
        picture.Box(480, 20, 440, 248, (30, 31, 34));
        var mark = Assert.Single(DiscordSpeakingDetector.Find(picture.Pixels, picture.W, picture.H));
        Assert.Equal(SpeakingKind.Tile, mark.Kind);
        Assert.Equal(new PixelRect(20, 20, 440, 248), mark.Box);
        Assert.True(mark.NameArea.X >= 20 && mark.NameArea.Bottom <= 268 && mark.NameArea.Y > 160 && mark.NameArea.Right < 460);
    }

    [Fact]
    public async Task TheNameBesideTheLitRingIsReadAndTheOwnerIsLeftOut()
    {
        var picture = MemberList(speaking: 1);
        picture.Ring(40, 120, 14, 2.5, Green);
        // The reader is given the enlarged crop; it answers with the line in the middle of what it was given.
        var names = new Queue<string>(["Alice", "Ben"]);
        var reader = new FakeReader(area => [new(names.Dequeue(), new(4, area.Height / 2 - 12, 90, 24))]);
        var speaking = await new DiscordSpeakerReader(reader).SpeakingAsync(picture.Pixels, picture.W, picture.H, "Ben", default);
        Assert.Equal(["Alice"], speaking);
        // Small text is enlarged (twice) before reading.
        Assert.All(reader.Reads, read => Assert.True(read.Height >= 60));
    }

    [Fact]
    public async Task TheOwnersOwnTileIsNeverSomeoneElse()
    {
        var reader = new FakeReader(area => [new("Ben", new(4, area.Height / 2 - 12, 60, 24))]);
        var speaking = await new DiscordSpeakerReader(reader).SpeakingAsync(MemberList(0).Pixels, Width, Height, "ben", default);
        Assert.Empty(speaking);
    }

    [Fact]
    public async Task NoTextBesideARingTriesBelowItThenGivesUp()
    {
        var reader = new FakeReader(_ => []);
        var speaking = await new DiscordSpeakerReader(reader).SpeakingAsync(MemberList(0).Pixels, Width, Height, null, default);
        Assert.Empty(speaking);
        // Beside, then below, each at two sizes.
        Assert.Equal(4, reader.Reads.Count);
    }

    [Fact]
    public void PickChoosesTheLineLevelWithTheRingOrTheLowestInATile()
    {
        var ring = new SpeakingMark(SpeakingKind.Ring, new(10, 100, 28, 28), new(40, 96, 200, 36), null);
        Assert.Equal("Alice", DiscordSpeakerNames.Pick(ring, [new("General", new(40, 60, 60, 14)), new("Alice", new(42, 106, 40, 16))]));
        var tile = new SpeakingMark(SpeakingKind.Tile, new(0, 0, 400, 225), new(4, 170, 240, 50), null);
        Assert.Equal("Carol", DiscordSpeakerNames.Pick(tile, [new("LIVE", new(10, 175, 30, 12)), new("Carol", new(10, 200, 50, 14))]));
    }

    [Theory]
    [InlineData("Alice", "Alice")]
    [InlineData("  ~Alice_99 ✓ ", "Alice_99")]
    [InlineData("é", null)]
    [InlineData("|||", null)]
    [InlineData("Mary Jane Watson-Parker the Second of Many", "Mary Jane Watson-Parker the Seco")]
    public void NamesAreCleanedFromOcr(string text, string? expected) => Assert.Equal(expected, DiscordSpeakerNames.Clean(text));

    [Fact]
    public void TheNameSeenMostOftenWinsAndTheLatestBreaksATie()
    {
        var vote = new DiscordSpeakerVote();
        Assert.Null(vote.Winner);
        vote.Add(["Alice"]);
        vote.Add(["Alice", "Bob"]);
        vote.Add(["Bob"]);
        Assert.Equal("Bob", vote.Winner);
        vote.Add(["alice"]);
        Assert.Equal("Alice", vote.Winner);
        vote.Add([]);
        Assert.Equal(5, vote.Samples);
    }

    [Fact]
    public void CallLinesNameTheSpeakerAndNoticeMartletsName()
    {
        Assert.Equal("Alice in the call: hey Jane, you there?", DiscordCallLine.Format("Alice", " hey Jane, you there? "));
        Assert.Equal("Someone in the call: hello", DiscordCallLine.Format(null, "hello"));
        Assert.Equal("Someone in the call: hello", DiscordCallLine.Format("!!", "hello"));
        Assert.True(DiscordCallLine.Addressed("hey Jane, you there?", ["Martlet", "Jane Doe"]));
        Assert.False(DiscordCallLine.Addressed("janet said hi", ["Martlet", "Jane"]));
    }

    [Fact]
    public void TheCallModeIsOffByDefaultAndSavesWhatItCanKeep()
    {
        var directory = Directory.CreateTempSubdirectory("martlet-calls-").FullName;
        try
        {
            var fresh = DiscordCallPreferences.Load(directory);
            Assert.False(fresh.On);
            Assert.Equal(DiscordCallCapture.DiscordApp, fresh.Capture);
            Assert.True(fresh.SeeSpeakers && fresh.AlsoSpeakers && fresh.BargeIn);
            Assert.Null(fresh.OutputId);
            var saved = fresh with
            {
                On = true, Capture = DiscordCallCapture.EverythingButMartlet, OwnerName = " Ben\u0007 ", OutputId = "{0.0.0}.{cable}",
                OutputName = "CABLE Input (VB-Audio Virtual Cable)", CameraBackground = DiscordCameraBackground.Magenta,
                CameraPicture = "a1b2c3d4"
            };
            Assert.True(saved.Save(directory));
            var loaded = DiscordCallPreferences.Load(directory);
            Assert.Equal(saved with { OwnerName = "Ben" }, loaded);
            Assert.Equal("#FF00FF", loaded.CameraColor);
            Assert.Null((saved with { CameraPicture = "not a key" }).Normalized().CameraPicture);
            File.WriteAllText(Path.Combine(directory, DiscordCallPreferences.FileName), "{ not json");
            Assert.False(DiscordCallPreferences.Load(directory).On);
            File.WriteAllText(Path.Combine(directory, DiscordCallPreferences.FileName), "{\"On\":true,\"OutputId\":\"\",\"OutputName\":\"x\"}");
            var blank = DiscordCallPreferences.Load(directory);
            Assert.True(blank.On);
            Assert.Null(blank.OutputId);
            Assert.Null(blank.OutputName);
        }
        finally { Directory.Delete(directory, true); }
    }

    private static readonly CallOutput Speakers = new("{speakers}", "Speakers (Realtek(R) Audio)", true);
    private static readonly CallOutput Cable = new("{cable}", "CABLE Input (VB-Audio Virtual Cable)", false);

    [Fact]
    public void WithoutAChoiceMartletsVoiceStaysOnItsUsualOutput()
    {
        var plan = DiscordCallOutputs.Plan(new() { On = true }, [Speakers, Cable]);
        Assert.Null(plan.OutputId);
        Assert.False(plan.AlsoSpeakers);
        Assert.True(plan.Present);
        Assert.Equal(Cable, DiscordCallOutputs.Suggest([Speakers, Cable]));
        Assert.True(DiscordCallOutputs.LooksVirtual("VoiceMeeter Input (VB-Audio VoiceMeeter VAIO)"));
        Assert.False(DiscordCallOutputs.LooksVirtual(Speakers.Name));
    }

    [Fact]
    public void AChosenCableCarriesTheVoiceAndTheUsualOutputToo()
    {
        var chosen = new DiscordCallPreferences { On = true, OutputId = Cable.Id, OutputName = Cable.Name };
        var plan = DiscordCallOutputs.Plan(chosen, [Speakers, Cable]);
        Assert.Equal((Cable.Id, true, true), (plan.OutputId, plan.AlsoSpeakers, plan.Present));
        Assert.False(DiscordCallOutputs.Plan(chosen with { AlsoSpeakers = false }, [Speakers, Cable]).AlsoSpeakers);
        // The usual output itself chosen: nothing to mirror.
        Assert.False(DiscordCallOutputs.Plan(chosen with { OutputId = Speakers.Id }, [Speakers, Cable]).AlsoSpeakers);
        // Not listed yet: the saved choice is trusted.
        Assert.Equal(Cable.Id, DiscordCallOutputs.Plan(chosen, null).OutputId);
    }

    [Fact]
    public void ARenumberedCableIsFoundByNameAndAMissingOneLeavesTheVoiceWhereItWas()
    {
        var chosen = new DiscordCallPreferences { On = true, OutputId = "{old}", OutputName = Cable.Name };
        Assert.Equal(Cable.Id, DiscordCallOutputs.Plan(chosen, [Speakers, Cable]).OutputId);
        var missing = DiscordCallOutputs.Plan(chosen, [Speakers]);
        Assert.Null(missing.OutputId);
        Assert.False(missing.Present);
        Assert.Contains("isn't connected", missing.Summary);
    }
}
