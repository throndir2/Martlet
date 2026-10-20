using System.IO;
using System.Text.Json;
using Martlet.Core.Planning;

// Also built into Martlet's MCP server (recommended_setup_status), in its own namespace.
#if MARTLET_MCP
namespace Martlet.Mcp.Shared;
#else
namespace Martlet.Desktop;
#endif

/// <summary>What an automatic check does with a recommendation: nothing, keep it until someone uses this PC, or ask now.</summary>
internal enum SetupAskStep { Nothing, Wait, Ask }

/// <summary>Which companion PC asks about a better setup after a computer came back or stayed away. Every companion PC checks;
/// only the one someone is using asks: someone used its keyboard or mouse, or talked with Martlet there, in the last
/// <see cref="InUse"/> (the measure reminders use to find the companion PC used most recently). A companion PC nobody uses
/// keeps the suggestion and asks when someone comes back to it within <see cref="Keep"/>, if it is still worth asking. So the
/// PC in front of the owner asks, and a gaming PC left on in another room stays quiet. Host PCs never ask.</summary>
internal static class SetupAskRule
{
    internal static readonly TimeSpan InUse = TimeSpan.FromMinutes(10);
    internal static readonly TimeSpan Keep = TimeSpan.FromHours(1);

    internal static (SetupAskStep Step, string Why) Decide(NetworkRecommendation recommendation, RecommendedSetupMemory memory,
        bool companion, TimeSpan idle)
    {
        ArgumentNullException.ThrowIfNull(recommendation);
        ArgumentNullException.ThrowIfNull(memory);
        if (!companion) return (SetupAskStep.Nothing, "this is a host PC; companion PCs ask");
        if (recommendation.AlreadyOptimal) return (SetupAskStep.Nothing, "your computers already use the recommended setup");
        if (!recommendation.WorthAsking) return (SetupAskStep.Nothing, "only minor changes, not worth asking about");
        if (memory.WasDeclined(recommendation.Fingerprint)) return (SetupAskStep.Nothing, "you declined this setup before");
        if (idle >= InUse) return (SetupAskStep.Wait, $"nobody used this PC in the last {InUse.TotalMinutes:0} minutes; it asks when someone does");
        return (SetupAskStep.Ask, "someone is using this PC");
    }
}
