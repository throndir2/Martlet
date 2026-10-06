using Martlet.Core.Creations;
using Martlet.Core.Pictures;

namespace Martlet.Conversation.Tests;

public sealed class PictureToolsTests
{
    [Fact]
    public void Draw_arguments_are_parsed_with_defaults_and_bounds()
    {
        var (arguments, problem) = PictureTools.Parse("""{"description":"a fox\nin snow","shape":"wide","avoid":"people"}""");
        Assert.Null(problem);
        Assert.Equal("a fox in snow", arguments!.Description);
        Assert.Equal("a fox in snow", arguments.Title);
        Assert.Equal(PictureShape.Wide, arguments.Shape);
        Assert.Equal("people", arguments.Avoid);
        Assert.Equal(PictureShape.Square, PictureTools.Parse("""{"description":"x","shape":"hexagon"}""").Arguments!.Shape);
        Assert.NotNull(PictureTools.Parse("""{"title":"no description"}""").Problem);
        Assert.NotNull(PictureTools.Parse("not json").Problem);
        var longOne = PictureTools.Parse($$"""{"description":"{{new string('a', 5000)}}"}""").Arguments!;
        Assert.Equal(PictureRequest.MaximumPromptCharacters, longOne.Description.Length);
    }

    [Fact]
    public void The_tool_definition_is_valid_and_stable()
    {
        Assert.Equal(PictureTools.DrawName, PictureTools.Definition.Name);
        Assert.Same(PictureTools.Definition, PictureTools.Definition);
        PictureTools.Kind.Validate();
    }

    [Fact]
    public async Task A_drawn_picture_is_kept_as_a_picture_creation_and_read_back()
    {
        var directory = Directory.CreateTempSubdirectory("martlet-pictures-").FullName;
        try
        {
            var registry = new CreationRegistry();
            registry.Register(PictureCreations.Kind);
            var request = new PictureRequest { Prompt = "a fox", Shape = PictureShape.Portrait };
            var result = await new FixturePictureMaker(TimeSpan.Zero).GenerateAsync(request, null, CancellationToken.None);
            var creation = await CreationStore.AddAsync(directory, PictureCreations.Draft(result, "Fox", "a fox", request,
                new CreationAuthor { Device = "test", Computer = "PC" }), registry, DateTimeOffset.UtcNow, CancellationToken.None);
            Assert.Equal(PictureCreations.KindName, creation.Kind);
            Assert.Equal("a fox", creation.Text);
            Assert.StartsWith("FIXTURE - NOT AI", creation.Summary);
            Assert.Equal("portrait", PictureCreations.Metadata(creation)!.Shape);
            var (image, mediaType, problem) = await PictureCreations.LoadAsync(creation, CreationStore.Assets(directory, creation), CancellationToken.None);
            Assert.Null(problem);
            Assert.Equal(PictureImages.Png, mediaType);
            Assert.Equal(result.Image, image);
            Assert.Contains(creation.Key, PictureTools.Ready(creation.Key, "Fox", result));
            Assert.Equal("Drawn from", PictureCreations.Kind.DetailsFor(creation)[0].Heading);
        }
        finally { Directory.Delete(directory, recursive: true); }
    }
}
