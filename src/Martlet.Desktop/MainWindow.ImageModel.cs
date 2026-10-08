using Martlet.Conversation;
using Martlet.Core.Settings;

namespace Martlet.Desktop;

// Where pictures go, as Companion and Home show it (docs/SENSE_MODELS.md): to Thinking, to an image model of its own that
// describes them for Thinking, or nowhere.
public partial class MainWindow
{
    /// <summary>Where pictures go for <paramref name="thinking"/>: the running conversation's image and audio model choices (or
    /// this PC's saved sense-models.json without one) and what Martlet found out about the models.</summary>
    private SenseRoute SavedImageRoute(SetupRoute? thinking, ModelAbilities abilities) =>
        SenseRouting.For(SenseKind.Image, conversation?.SenseModels ?? SenseModels.Load(store?.DataDirectory), thinking, abilities);
}
