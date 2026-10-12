using Martlet.Core.Creations;

namespace Martlet.Conversation.Tests;

public sealed class MotionToolsTests
{
    private static MotionMade Made(bool fixture = true) =>
        new([.. "glTF"u8, 2, 0, 0, 0, 12, 0, 0, 0], 3, 7, "HY-Motion-1.0", "the motion PC", fixture, TimeSpan.FromSeconds(12.3));

    [Fact]
    public void Motion_arguments_are_parsed_with_a_tag_name_defaults_and_bounds()
    {
        var (arguments, problem) = MotionTools.Parse("""{"name":"Deep Bow!","motion":"A person bows\ndeeply.","seconds":30}""");
        Assert.Null(problem);
        Assert.Equal("deep_bow", arguments!.Name);
        Assert.Equal("A person bows deeply.", arguments.Motion);
        Assert.Equal(MotionTools.MaximumSeconds, arguments.Seconds);
        var named = MotionTools.Parse("""{"name":"{cheer}"}""").Arguments!;
        Assert.Equal("cheer", named.Name);
        Assert.Null(named.Motion);
        Assert.Equal(MotionTools.DefaultSeconds, named.Seconds);
        Assert.Equal(1.5, MotionTools.Parse("""{"name":"hop","seconds":"1.5"}""").Arguments!.Seconds);
        Assert.Equal(MotionTools.MinimumSeconds, MotionTools.Parse("""{"name":"hop","seconds":0}""").Arguments!.Seconds);
        Assert.NotNull(MotionTools.Parse("""{"motion":"A person jumps."}""").Problem);
        Assert.NotNull(MotionTools.Parse("""{"name":"!!!"}""").Problem);
        Assert.NotNull(MotionTools.Parse("not json").Problem);
        var longOne = MotionTools.Parse($$"""{"name":"{{new string('a', 80)}}","motion":"{{new string('b', 900)}}"}""").Arguments!;
        Assert.Equal(MotionTools.MaximumNameCharacters, longOne.Name.Length);
        Assert.Equal(MotionTools.MaximumMotionCharacters, longOne.Motion!.Length);
    }

    [Fact]
    public void The_tool_definition_is_valid_stable_and_quiet()
    {
        Assert.Equal(MotionTools.Name, MotionTools.Definition.Name);
        Assert.Same(MotionTools.Definition, MotionTools.Definition);
        Assert.True(MotionTools.Description.Length <= Martlet.Providers.TextToolDefinition.MaxDescriptionCharacters);
        MotionTools.Kind.Validate();
        Assert.True(MotionTools.Kind.Quiet);
        Assert.Equal(1, MotionTools.Kind.MaxActive);
        Assert.All(MotionTools.Starters, starter =>
        {
            Assert.Equal(starter.Name, MotionTools.TagOf(starter.Name));
            Assert.StartsWith("A person ", starter.Motion);
        });
        // Martlet's own gestures already have these tags, so the starters never take them.
        Assert.DoesNotContain(MotionTools.Starters, s => s.Name is "nod" or "shake_head" or "wave" or "shrug" or "bow");
    }

    [Fact]
    public async Task A_quiet_kind_is_never_brought_up_on_its_own_but_waits_for_the_next_message()
    {
        using var jobs = new BackgroundJobs();
        var job = jobs.Start(MotionTools.Kind, "cheer", (_, _) => Task.FromResult(BackgroundJobOutcome.Done("Motion cheer ready"))).Job!;
        for (var i = 0; i < 500 && !job.Finished; i++) await Task.Delay(10);
        Assert.True(job.Finished);
        Assert.True(job.Quiet);
        Assert.False(jobs.HasNews);
        Assert.Null(jobs.Take(onItsOwn: true));
        var delivery = jobs.Take(onItsOwn: false);
        Assert.NotNull(delivery);
        Assert.Contains("Motion cheer ready", BackgroundJobs.Results(delivery.Jobs));
    }

    [Fact]
    public async Task A_made_motion_is_kept_as_a_motion_creation_and_read_back()
    {
        var directory = Directory.CreateTempSubdirectory("martlet-motions-").FullName;
        try
        {
            var registry = new CreationRegistry();
            registry.Register(MotionCreations.Kind);
            var arguments = new MotionArguments("cheer", "A person raises both fists above the head and jumps once.", 3);
            var made = Made();
            var creation = await CreationStore.AddAsync(directory, MotionCreations.Draft(arguments, made,
                new CreationAuthor { Device = "test", Computer = "PC" }, madeFor: "Aiko"), registry, DateTimeOffset.UtcNow, CancellationToken.None);
            Assert.Equal(MotionCreations.KindName, creation.Kind);
            Assert.Equal("cheer", creation.Title);
            Assert.StartsWith("FIXTURE - NOT AI", creation.Summary);
            var metadata = MotionCreations.Metadata(creation)!;
            Assert.Equal("cheer", metadata.Name);
            Assert.Equal("Aiko", metadata.MadeFor);
            Assert.Equal(7, metadata.Seed);
            Assert.Equal("cheer", MotionCreations.NameOf(creation));
            Assert.Equal(3, MotionCreations.SecondsOf(creation));
            var (clip, problem) = await MotionCreations.LoadAsync(creation, CreationStore.Assets(directory, creation), CancellationToken.None);
            Assert.Null(problem);
            Assert.Equal(made.Clip, clip);
            Assert.Equal([creation.Id], MotionCreations.Kept(CreationStore.View(directory)).Select(c => c.Id));
            var ready = MotionTools.Ready(creation.Key, "cheer", made);
            Assert.Contains(creation.Key, ready);
            Assert.Contains("FIXTURE", ready);
            Assert.Contains("{cheer}", ready);
            Assert.Contains("it didn't play (the character is hidden)", MotionTools.Ready(creation.Key, "cheer", made, "the character is hidden."));
        }
        finally { Directory.Delete(directory, recursive: true); }
    }

    [Fact]
    public async Task Kept_motions_are_left_out_of_the_creation_tools()
    {
        var directory = Directory.CreateTempSubdirectory("martlet-motions-").FullName;
        try
        {
            var registry = new CreationRegistry();
            registry.Register(FixtureCreations.Kind);
            var before = CreationTools.Definitions(registry.ToolKinds);
            registry.Register(MotionCreations.Kind);
            var after = CreationTools.Definitions(registry.ToolKinds);
            Assert.Equal(before.Select(d => d.Description), after.Select(d => d.Description));
            Assert.Equal([FixtureCreations.KindName], registry.ToolKinds.Select(k => k.Name));
            var author = new CreationAuthor { Device = "test", Computer = "PC" };
            var motion = await CreationStore.AddAsync(directory, MotionCreations.Draft(new("cheer", "A person jumps.", 3), Made(), author),
                registry, DateTimeOffset.UtcNow, CancellationToken.None);
            var tone = await CreationStore.AddAsync(directory, FixtureCreations.Draft(author, "Morning tone", 1, 440), registry,
                DateTimeOffset.UtcNow, CancellationToken.None);
            var library = CreationStore.View(directory);
            var listed = CreationTools.List(library, registry, "{}", _ => true).Output;
            Assert.Contains(tone.Key, listed);
            Assert.DoesNotContain(motion.Key, listed);
            Assert.DoesNotContain(motion.Key, CreationTools.List(library, registry, "{\"kind\":\"motion\"}", _ => true).Output);
            var performed = await CreationTools.PerformAsync(library, registry, $$"""{"id":"{{motion.Key}}"}""", c => CreationStore.Assets(directory, c),
                CancellationToken.None);
            Assert.True(performed.IsError);
            Assert.Contains("There is no creation with id", performed.Output);
        }
        finally { Directory.Delete(directory, recursive: true); }
    }
}
