namespace Martlet.Launcher.Tests;

internal static class FixtureLaunchApproval
{
    // Only fixture tests synthesize a human review. The production API requires both objects.
    internal static async Task<DesktopLaunchResult> LaunchActiveAsync(this LocalDesktopLauncher launcher,
        long revision, CancellationToken token = default)
    {
        var plan = launcher.PrepareLaunch(revision, token);
        return await launcher.LaunchActiveAsync(plan, plan.Approve(plan.OperationId, plan.PlanDigest), token);
    }
}
