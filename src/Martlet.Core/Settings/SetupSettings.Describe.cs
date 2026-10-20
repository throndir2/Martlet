namespace Martlet.Core.Settings;

public sealed partial record SetupSettings
{
    public static string Describe(AppSettings? settings)
    {
        if (settings?.Setup is not { } setup)
            return "Setup not started: choose Thinking, Voice and Listening on the Companion page.";
        var lines = new List<string>
        {
            $"Setup checkpoint: {setup.Checkpoint}.",
            "Saving settings does not record audio, contact providers or start local tools.",
            OpenAiSetup.Disclosure,
            SelfHostSetup.Disclosure
        };
        foreach (var role in Enum.GetValues<SetupRole>())
        {
            var route = setup.Routes.SingleOrDefault(r => r.Role == role);
            if (route is null)
            {
                lines.Add($"{RoleName(role)}: not set up.");
                continue;
            }
            var enabled = route.RouteType is null || route.Enabled == true;
            var consent = route.Consent == route.Selection() ? "selection saved" : "review required";
            var credential = route.RouteType == SetupRouteType.ChatCompletions
                ? route.CredentialId is null ? "key optional" : "key saved"
                : NeedsCredential(route)
                    ? route.CredentialId is null ? "key missing" : "key saved"
                    : "no key needed";
            var status = route.RouteType is null or SetupRouteType.OpenAi or SetupRouteType.ChatCompletions or SetupRouteType.ElevenLabs ||
                route.RouteType is SetupRouteType.LocalWindowsStt or SetupRouteType.LocalWindowsTts or SetupRouteType.LocalParakeet
                    ? enabled ? "ready when you use it" : "off"
                    : EvidenceReady(route) && enabled ? "ready when you use it" : "needs setup";
            lines.Add($"{RoleName(role)}: {RouteName(route.RouteType)}; {status}; {consent}; {credential}.");
        }
        if (setup.PendingRemovals.Count != 0)
            lines.Add($"Credential cleanup pending: {setup.PendingRemovals.Count}.");
        if (setup.RetainedGatewayCredentials is { Count: > 0 })
            lines.Add("A saved host pairing is waiting to be reconnected.");
        return string.Join(Environment.NewLine, lines);
    }

    private static bool NeedsCredential(SetupRoute route) => route.RouteType switch
    {
        SetupRouteType.LocalWhisper or SetupRouteType.LocalWindowsStt or SetupRouteType.LocalWindowsTts or SetupRouteType.LocalParakeet => false,
        SetupRouteType.ChatCompletions => false,
        _ => true
    };

    private static bool EvidenceReady(SetupRoute route) => route.RouteType switch
    {
        SetupRouteType.GatewayOllama or SetupRouteType.GatewayStt => route.GatewaySnapshot is not null,
        SetupRouteType.GatewayF5 => route.GatewaySnapshot is not null && route.Reference is not null,
        SetupRouteType.LocalWhisper => route.LocalStt is not null,
        _ => true
    };

    private static string RoleName(SetupRole role) => role switch
    {
        SetupRole.Stt => "Speech-to-text",
        SetupRole.Llm => "Thinking",
        _ => "Voice"
    };

    private static string RouteName(SetupRouteType? route) => route switch
    {
        null or SetupRouteType.OpenAi => "OpenAI",
        SetupRouteType.ChatCompletions => "custom chat endpoint",
        SetupRouteType.LocalWindowsStt => "Windows speech recognition",
        SetupRouteType.LocalWhisper or SetupRouteType.LocalParakeet => "local speech recognition",
        SetupRouteType.GatewayOllama => "paired-host model",
        SetupRouteType.GatewayF5 => "paired-host voice",
        SetupRouteType.GatewayStt => "paired-host speech recognition",
        SetupRouteType.ElevenLabs => "ElevenLabs voice",
        _ => "selected route"
    };
}
