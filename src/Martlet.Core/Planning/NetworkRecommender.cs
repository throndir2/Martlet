namespace Martlet.Core.Planning;

/// <summary>Recommends the setup for all the owner's computers. Pure: it reads nothing and contacts nothing.</summary>
public static class NetworkRecommender
{
    /// <summary>The recommended setup for <paramref name="request"/>'s computers and the changes from today's.</summary>
    public static NetworkRecommendation Recommend(NetworkSetupRequest request, FootprintCatalog? catalog = null)
    {
        ArgumentNullException.ThrowIfNull(request);
        var today = Today(request, catalog);
        return new(today, today, []);
    }

    /// <summary>Today's setup as <paramref name="request"/> describes it: each computer's roles, who does each job and the
    /// Thinking pool, with how full each computer is.</summary>
    public static NetworkSetup Today(NetworkSetupRequest request, FootprintCatalog? catalog = null)
    {
        ArgumentNullException.ThrowIfNull(request);
        return new(request.Machines.Select(m => new MachinePlan(m.Specs.Id, m.Kind, m.Roles)).ToArray(), request.CurrentJobs)
        {
            ThinkingPool = request.CurrentThinkingPool
        };
    }
}
