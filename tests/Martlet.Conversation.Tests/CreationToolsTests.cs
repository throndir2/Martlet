using Martlet.Core.Creations;

namespace Martlet.Conversation.Tests;

public sealed class CreationToolsTests
{
    [Fact]
    public async Task ListsAndPerformsThroughTheKindsHandler()
    {
        var root = Path.Combine(Path.GetTempPath(), "martlet-creation-tools-" + Guid.NewGuid().ToString("N"));
        try
        {
            var registry = new CreationRegistry();
            registry.Register(FixtureCreations.Kind);
            var definitions = CreationTools.Definitions(registry.Kinds);
            Assert.Equal([CreationTools.ListName, CreationTools.PerformName], definitions.Select(d => d.Name));
            Assert.Equal(definitions[1].Description, CreationTools.Definitions(registry.Kinds)[1].Description);

            var author = new CreationAuthor { Device = "desktop-a" };
            var tone = await CreationStore.AddAsync(root, FixtureCreations.Draft(author, "Rain tone", 1), registry, DateTimeOffset.UtcNow, CancellationToken.None);
            var library = CreationStore.View(root);
            var listed = CreationTools.List(library, registry, "{\"query\":\"rain\"}", c => CreationStore.IsComplete(root, c));
            Assert.False(listed.IsError);
            Assert.Contains(tone.Key, listed.Output);
            Assert.DoesNotContain(tone.Id, listed.Output);

            ICreationAssets Assets(Creation c) => CreationStore.Assets(root, c);
            var refused = await CreationTools.PerformAsync(library, registry, $"{{\"id\":\"{tone.Key}\"}}", Assets, CancellationToken.None);
            Assert.True(refused.IsError);
            using (registry.Handle(FixtureCreations.KindName, FixtureCreations.Handler))
            {
                var performed = await CreationTools.PerformAsync(library, registry, $"{{\"id\":\"{tone.Key}\",\"options\":{{\"start_ms\":250}}}}",
                    Assets, CancellationToken.None);
                Assert.False(performed.IsError, performed.Output);
                Assert.Contains("250 ms", performed.Output);
                Assert.True((await CreationTools.PerformAsync(library, registry, "{\"id\":\"000000000000\"}", Assets, CancellationToken.None)).IsError);
            }
            Assert.Null(registry.HandlerFor(FixtureCreations.KindName));
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, recursive: true); }
    }
}
