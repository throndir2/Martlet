namespace Martlet.Core.Settings;

/// <summary>The one place that finds the current Thinking route (the <see cref="SetupRole.Llm"/> route in
/// <see cref="SetupSettings.Routes"/>).
/// <para>Rule: <see cref="SetupSettings.Validate"/> allows at most one route for each role, so the look-up takes the first
/// match and never throws. It returns null when there is no Thinking route. It does not check
/// <see cref="SetupRoute.Enabled"/>: a caller that needs a route that is on checks it, for example
/// <c>settings.ThinkingRoute() is { Enabled: not false } route</c>.</para></summary>
public static class SetupRouteLookup
{
    /// <summary>The Thinking route in these routes, or null.</summary>
    public static SetupRoute? ThinkingRoute(this IEnumerable<SetupRoute>? routes) =>
        routes?.FirstOrDefault(r => r.Role == SetupRole.Llm);

    /// <summary>The Thinking route of this setup, or null.</summary>
    public static SetupRoute? ThinkingRoute(this SetupSettings? setup) => setup?.Routes.ThinkingRoute();

    /// <summary>The Thinking route of these settings, or null when there are no settings, no setup or no Thinking route.</summary>
    public static SetupRoute? ThinkingRoute(this AppSettings? settings) => settings?.Setup.ThinkingRoute();
}
