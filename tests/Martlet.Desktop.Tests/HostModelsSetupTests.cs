using System.IO;
using Martlet.Core.Cluster;
using Martlet.Core.Planning;
using Martlet.Desktop;

namespace Martlet.Desktop.Tests;

/// <summary>Prefer models your hosts already have: the choice is saved on this PC, reaches the recommender and the review lists
/// the models your hosts keep.</summary>
public sealed class HostModelsSetupTests
{
    // MainWindow's statics include pack:// resources: register the scheme as a WPF app would before using them.
    public HostModelsSetupTests() => _ = System.IO.Packaging.PackUriHelper.UriSchemePack;

    [Fact]
    public void Prefer_models_your_hosts_already_have_is_off_unless_the_owner_turns_it_on()
    {
        Assert.False(new RecommendedSetupMemory().PreferHostModels);
        var directory = Path.Combine(Path.GetTempPath(), "martlet-hostmodels-" + Guid.NewGuid().ToString("N"));
        try
        {
            Assert.False(RecommendedSetupMemory.Load(directory).PreferHostModels);
            Directory.CreateDirectory(directory);
            File.WriteAllText(Path.Combine(directory, RecommendedSetupMemory.FileName), """{ "UseServedModels": false }""");
            Assert.False(RecommendedSetupMemory.Load(directory).PreferHostModels);
            Assert.True(RecommendedSetupMemory.Load(directory).WithHostModels(true).Save(directory));
            var on = RecommendedSetupMemory.Load(directory);
            Assert.True(on.PreferHostModels);
            Assert.False(on.UseServedModels);
        }
        finally
        {
            if (Directory.Exists(directory)) Directory.Delete(directory, true);
        }
    }

    [Fact]
    public void The_hostmodels_fixture_thinks_with_the_bigger_kept_model_and_the_review_lists_it()
    {
        var sources = RecommendedSetupInputs.HostModelsFixture(DateTimeOffset.UtcNow);
        var build = RecommendedSetupInputs.Request(sources);
        Assert.True(build.Request.PreferHostModels);

        var recommendation = NetworkRecommender.Recommend(build.Request, FootprintCatalog.Default);
        Assert.Equal("gpu-box", recommendation.Target.Job(ClusterJobs.Thinking)?.HostId);
        Assert.Contains(recommendation.Target.Machine("gpu-box")!.Roles, r => r.Kind == "ollama" && r.Model == "qwen2.5:14b");
        var change = Assert.Single(recommendation.Changes, c => c.RoleKind == "ollama" && c.Model == "qwen2.5:14b");
        Assert.Null(change.DownloadGb);
        Assert.Contains("prefer models your hosts already have", change.Why, StringComparison.Ordinal);

        var review = RecommendedSetupReview.From(recommendation, build);
        Assert.True(review.PreferHostModels);
        Assert.Equal(["gemma4:e4b on gpu-box", "qwen2.5:14b on gpu-box"], review.HostModels);

        var off = NetworkRecommender.Recommend(RecommendedSetupInputs.Request(sources with { PreferHostModels = false }).Request,
            FootprintCatalog.Default);
        Assert.DoesNotContain(off.Target.Machine("gpu-box")!.Roles, r => r.Kind == "ollama" && r.Model == "qwen2.5:14b");
    }
}
