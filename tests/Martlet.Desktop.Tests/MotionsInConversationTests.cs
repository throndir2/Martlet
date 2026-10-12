using System.Text;
using System.Text.Json;
using Martlet.Avatar.Hosting;
using Martlet.Avatars;
using Martlet.Conversation;
using Martlet.Conversation.Tests;
using Martlet.Core.Creations;
using Martlet.Core.Motion;
using Martlet.Providers.Tests;

namespace Martlet.Desktop.Tests;

/// <summary>Motions in conversation (docs/AVATARS.md#motions-in-conversation): body_motion for a VRM with motions set up, the
/// background motion job, kept motions and the reply tags they get from the next conversation.</summary>
public sealed class MotionsInConversationTests
{
    private const string Cheer = """{"name":"cheer","motion":"A person raises both fists above the head and jumps once.","seconds":3}""";

    [Fact]
    public async Task BodyMotionIsOfferedLastOnlyForAVrmWithMotionsSetUpAndToolsStayTheSameWithout()
    {
        await using var fixture = await LiveFixture.Create(history: true, tools: true);
        fixture.Controller.AutoCapture = false;
        fixture.Answer("Hello!");
        await fixture.Finish(fixture.Start("Hi there."));
        var before = Tools(fixture.Llm.Body);
        using var setup = new Setup(fixture.DirectoryPath, vrm: false);
        fixture.Controller.Motions = setup.Motions;
        await fixture.Finish(fixture.Start("Hi again."));
        Assert.Equal(before, Tools(fixture.Llm.Body));

        setup.Motions.ChosenVrm = true;
        setup.SetUp = false;
        await fixture.Finish(fixture.Start("Hello again."));
        Assert.Equal(before, Tools(fixture.Llm.Body));

        setup.SetUp = true;
        await fixture.Finish(fixture.Start("And once more."));
        var after = ToolNames(fixture.Llm.Body);
        Assert.Equal([.. ToolNames(before), MotionTools.Name], after);
        var withMotion = Tools(fixture.Llm.Body);
        await fixture.Finish(fixture.Start("Still here."));
        Assert.Equal(withMotion, Tools(fixture.Llm.Body));
    }

    [Fact]
    public async Task BodyMotionMakesKeepsAndPlaysANewMotionQuietlyInTheBackground()
    {
        await using var fixture = await LiveFixture.Create(history: true, tools: true);
        fixture.Controller.AutoCapture = false;
        using var setup = new Setup(fixture.DirectoryPath);
        fixture.Controller.Motions = setup.Motions;
        var calls = 0;
        fixture.Llm.Respond = (_, _) => Task.FromResult(TextRecordingHandler.Sse(Interlocked.Increment(ref calls) == 1
            ? CallTrace(MotionTools.Name, Cheer) : Harness.Trace("We did it!")));
        await fixture.Finish(fixture.Start("We won the match!"));
        Assert.Contains("being made on the test motion PC", Encoding.UTF8.GetString(fixture.Llm.Body));

        await fixture.Advance(() => setup.Player.PlayedKept.Count == 1 && fixture.Controller.Jobs.Recent.Any(j => j.Kind == MotionTools.Kind && j.Finished));
        var kept = Assert.Single(MotionCreations.Kept(CreationStore.View(setup.Folder)));
        Assert.Equal("cheer", MotionCreations.NameOf(kept));
        Assert.Equal(kept.Key, setup.Player.PlayedKept[0]);
        Assert.Equal(1, setup.Studio.Calls);
        Assert.True(setup.Studio.Freed);
        var job = fixture.Controller.Jobs.Recent.Single(j => j.Kind == MotionTools.Kind);
        Assert.Equal(BackgroundJobState.Succeeded, job.State);
        Assert.Contains("it played on you", job.Result);
        // Quiet: Martlet doesn't start a reply of its own about a motion; the note waits for the user's next message.
        Assert.False(fixture.Controller.Jobs.HasNews);
        await fixture.Pass(TimeSpan.FromSeconds(2));
        Assert.Equal(2, calls);
        // The kept motion joins every VRM character's emotes and motions.
        Assert.Equal("cheer", Assert.Single(setup.Motions.Kept()).Name);
    }

    [Fact]
    public async Task ATagTheCharacterHasPlaysAtOnceAndANameAloneAsksForTheMotion()
    {
        await using var fixture = await LiveFixture.Create(history: true, tools: true);
        fixture.Controller.AutoCapture = false;
        using var setup = new Setup(fixture.DirectoryPath);
        setup.Player.Tags.Add("cheer");
        fixture.Controller.Motions = setup.Motions;
        var calls = 0;
        fixture.Llm.Respond = (_, _) => Task.FromResult(TextRecordingHandler.Sse(Interlocked.Increment(ref calls) switch
        {
            1 => CallTrace(MotionTools.Name, Cheer),
            3 => CallTrace(MotionTools.Name, """{"name":"moonwalk"}"""),
            _ => Harness.Trace("Yay!")
        }));
        await fixture.Finish(fixture.Start("We won!"));
        Assert.Equal(["cheer"], setup.Player.PlayedTags);
        Assert.Contains("Doing cheer now", Encoding.UTF8.GetString(fixture.Llm.Body));
        await fixture.Finish(fixture.Start("Do a moonwalk!"));
        Assert.Contains("You have no motion called moonwalk yet", Encoding.UTF8.GetString(fixture.Llm.Body));
        Assert.Equal(0, setup.Studio.Calls);
        Assert.DoesNotContain(fixture.Controller.Jobs.Active.Concat(fixture.Controller.Jobs.Recent), j => j.Kind == MotionTools.Kind);
    }

    [Fact]
    public async Task AMotionThatCantBeMadeSaysWhyAndKeepsNothing()
    {
        using var setup = new Setup(Directory.CreateTempSubdirectory("martlet-motions-").FullName, problem: "the motion PC is starting");
        var error = await Assert.ThrowsAsync<MotionStudioException>(() =>
            setup.Motions.MakeAndKeepAsync(MotionTools.Parse(Cheer).Arguments!, setup.Studio, _ => { }, default));
        Assert.Equal("the motion PC is starting", error.Message);
        Assert.Empty(MotionCreations.Kept(CreationStore.View(setup.Folder)));
        Assert.Equal(0, setup.Studio.Calls);
    }

    [Fact]
    public async Task KeptMotionsJoinTheReplyTagsAtTheNextConversation()
    {
        using var setup = new Setup(Directory.CreateTempSubdirectory("martlet-motions-").FullName);
        var vrm = Vrm();
        CharacterActionCatalog Catalog(params KeptMotion[] kept)
        {
            var inventory = vrm.WithKeptMotions(kept);
            return new(inventory, CharacterActions.Merge(inventory, null));
        }
        var wave = new KeptMotion("aa11bb22cc33", "big_wave", "A person waves the whole arm above the head.", 3);
        var cheer = new KeptMotion("3f9a0c1b2d4e", "cheer", "A person raises both fists above the head and jumps once.", 3);
        var first = Guid.NewGuid();
        var start = setup.Motions.OfferedIn(first, Catalog(wave)).Prompt(null, null)!;
        Assert.Contains("{big_wave}", start.Tags);
        // cheer was kept during the conversation: the same instructions until it ends.
        var later = setup.Motions.OfferedIn(first, Catalog(wave, cheer)).Prompt(null, null)!;
        Assert.Equal(start.Instructions, later.Instructions);
        Assert.Equal(start.Tags, later.Tags);
        // The next conversation offers it.
        var next = setup.Motions.OfferedIn(Guid.NewGuid(), Catalog(wave, cheer)).Prompt(null, null)!;
        Assert.Contains("{cheer}", next.Tags);
        // A removed motion leaves at once (its clip is gone).
        Assert.DoesNotContain("{big_wave}", setup.Motions.OfferedIn(first, Catalog(cheer)).Prompt(null, null)!.Tags);
    }

    [Fact]
    public async Task TheStarterMotionsAreMadeOnceAndNotAgainAfterTheOwnerDeletesThem()
    {
        using var setup = new Setup(Directory.CreateTempSubdirectory("martlet-motions-").FullName);
        Assert.True(setup.Motions.StartersDue());
        var said = await setup.Motions.MakeStartersAsync(default);
        Assert.Contains("made its starter motions", said);
        var kept = MotionCreations.Kept(CreationStore.View(setup.Folder));
        Assert.Equal(MotionTools.Starters.Select(s => s.Name), kept.Select(MotionCreations.NameOf));
        Assert.All(kept, c => Assert.True(MotionCreations.Metadata(c)!.Starter));
        Assert.True(setup.Studio.Freed);
        Assert.Equal(MotionTools.Starters.Count, setup.Motions.PreloadKeys().Count);
        foreach (var creation in kept) await CreationStore.RemoveAsync(setup.Folder, creation.Id, "owner", DateTimeOffset.UtcNow, default);
        Assert.False(setup.Motions.StartersDue());

        // Another computer that sees them kept (made elsewhere) marks them seen and never makes them.
        using var other = new Setup(Directory.CreateTempSubdirectory("martlet-motions-").FullName);
        await other.Motions.MakeAndKeepAsync(MotionTools.Starters[0], other.Studio, _ => { }, default, starter: true);
        Assert.False(other.Motions.StartersDue());
        Assert.True(File.Exists(Path.Combine(other.Folder, CharacterMotions.StartersFile)));
        Assert.False(new Setup(other.Root, folder: other.Folder).Motions.StartersDue());
    }

    [Fact]
    public void BodyMotionIsOfferedOnlyWhileMotionsAreSetUpAndTheChosenCharacterIsAVrm()
    {
        using var setup = new Setup(Directory.CreateTempSubdirectory("martlet-motions-").FullName, vrm: false);
        Assert.False(setup.Motions.Offered);
        setup.Motions.ChosenVrm = true;
        Assert.True(setup.Motions.Offered);
        setup.SetUp = false;
        Assert.False(setup.Motions.Offered);
    }

    private static CharacterActionInventory Vrm()
    {
        var bones = string.Join(",", new[] { "hips", "spine", "head" }.Select((b, i) => $"\"{b}\":{{\"node\":{i}}}"));
        var glb = CharacterGestureTests.Glb("{\"asset\":{\"version\":\"2.0\"},\"extensions\":{\"VRMC_vrm\":{\"specVersion\":\"1.0\",\"humanoid\":{\"humanBones\":{" +
            bones + "}}}}}");
        return CharacterActionInventory.From(AvatarRenderer.Vrm, "m.vrm", [new("m.vrm", glb, "model/gltf-binary")]);
    }

    private sealed class Setup : IDisposable
    {
        private readonly bool owned;

        internal Setup(string root, bool vrm = true, string? problem = null, string? folder = null)
        {
            Root = root;
            owned = folder is null;
            Folder = folder ?? Path.Combine(root, "account-creations");
            Directory.CreateDirectory(Folder);
            Registry.Register(MotionCreations.Kind);
            Studio = new FakeStudio(problem);
            Motions = new CharacterMotions(root, () => Folder, _ => SetUp, _ => Studio, Player, Registry) { ChosenVrm = vrm };
        }

        internal string Root { get; }
        internal string Folder { get; }
        internal bool SetUp { get; set; } = true;
        internal CreationRegistry Registry { get; } = new();
        internal FakePlayer Player { get; } = new();
        internal FakeStudio Studio { get; }
        internal CharacterMotions Motions { get; }

        public void Dispose()
        {
            if (!owned) return;
            if (Root.Contains("martlet-motions-", StringComparison.Ordinal) && Directory.Exists(Root)) Directory.Delete(Root, recursive: true);
            else if (Directory.Exists(Folder)) Directory.Delete(Folder, recursive: true);
        }
    }

    internal sealed class FakePlayer : ICharacterMotionPlayer
    {
        internal HashSet<string> Tags { get; } = [];
        internal List<string> PlayedTags { get; } = [];
        internal List<string> PlayedKept { get; } = [];

        public Task<string?> PlayTagAsync(string tag, string reason, CancellationToken token)
        {
            if (!Tags.Contains(tag)) return Task.FromResult<string?>(null);
            lock (PlayedTags) PlayedTags.Add(tag);
            return Task.FromResult<string?>(MotionTools.Played(tag));
        }

        public Task<string?> PlayKeptAsync(string key, string reason, CancellationToken token)
        {
            lock (PlayedKept) PlayedKept.Add(key);
            return Task.FromResult<string?>(null);
        }
    }

    internal sealed class FakeStudio(string? problem) : IMotionStudio
    {
        private int calls;
        internal int Calls => Volatile.Read(ref calls);
        internal bool Freed { get; private set; }

        public string Where => "the test motion PC";
        public string? HostId => "motion-host";
        public Task<string?> ProblemAsync(CancellationToken token) => Task.FromResult(problem);

        public async Task<MotionMade> MakeAsync(MotionArguments arguments, long seed, IProgress<string>? progress, CancellationToken token)
        {
            Interlocked.Increment(ref calls);
            var made = await new FixtureMotionMaker(TimeSpan.Zero).GenerateAsync(
                new MotionRequest { Prompt = arguments.Motion!, Seconds = arguments.Seconds, Seed = seed }, token);
            return new(made.Vrma, made.Seconds, made.Seed, made.Model, Where, true, made.Took);
        }

        public void FreeLater() => Freed = true;
        public void Dispose() { }
    }

    private static string Tools(byte[] body)
    {
        using var json = JsonDocument.Parse(body);
        return json.RootElement.TryGetProperty("tools", out var tools) ? tools.GetRawText() : "";
    }

    private static string[] ToolNames(string tools) => tools.Length == 0 ? []
        : [.. JsonDocument.Parse(tools).RootElement.EnumerateArray().Select(tool => tool.GetProperty("name").GetString()!)];

    private static string[] ToolNames(byte[] body) => ToolNames(Tools(body));

    // The model answers with one function call and no text.
    private static string CallTrace(string name, string arguments)
    {
        var done = new { id = "fc_1", type = "function_call", call_id = "call_a", name, arguments, status = "completed" };
        return string.Concat(
            TextFixtures.Event("response.created", 0, new { response = Response("in_progress", []) }),
            TextFixtures.Event("response.in_progress", 1, new { response = Response("in_progress", []) }),
            TextFixtures.Event("response.output_item.added", 2, new { output_index = 0,
                item = new { id = "fc_1", type = "function_call", call_id = "call_a", name, arguments = "", status = "in_progress" } }),
            TextFixtures.Event("response.function_call_arguments.delta", 3, new { output_index = 0, item_id = "fc_1", delta = arguments }),
            TextFixtures.Event("response.function_call_arguments.done", 4, new { output_index = 0, item_id = "fc_1", arguments }),
            TextFixtures.Event("response.output_item.done", 5, new { output_index = 0, item = done }),
            TextFixtures.Event("response.completed", 6, new { response = Response("completed", [done]) }));
    }

    private static object Response(string status, object[] output) => new
    {
        id = "resp_call", @object = "response", model = TextFixtures.Model, status, store = false, background = false,
        output, error = (object?)null, incomplete_details = (object?)null
    };
}
