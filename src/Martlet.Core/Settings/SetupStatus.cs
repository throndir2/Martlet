using Martlet.Core.Contracts;

namespace Martlet.Core.Settings;

public sealed record SetupRoleStatus(SetupRole Role, bool RouteSelected, bool DestinationSelected, bool CredentialReferenced);

// Metadata only: no upstream IDs, origins, credential references or secrets in diagnostic reports.
public sealed record SetupStatus : IContract
{
    public required SetupStep Checkpoint { get; init; }
    public required IReadOnlyList<SetupRoleStatus> Roles { get; init; }
    public required int PendingRemovals { get; init; }
    [System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)]
    public AudioSetupStatus? Audio { get; init; }

    public static SetupStatus? From(AppSettings? settings) => settings?.Setup is not { } setup ? null : new()
    {
        Checkpoint = setup.Checkpoint, PendingRemovals = setup.PendingRemovals.Count,
        Audio = settings.Audio is null ? null : AudioSetupStatus.From(settings.Audio),
        Roles = Enum.GetValues<SetupRole>().Select(role =>
        {
            var route = setup.Routes.SingleOrDefault(r => r.Role == role);
            return new SetupRoleStatus(role, route is not null, route?.Consent is not null, route?.CredentialId is not null);
        }).ToArray()
    };

    public void Validate()
    {
        ContractRules.Defined(Checkpoint);
        Audio?.Validate();
        ContractRules.Require(PendingRemovals is >= 0 and <= 16 && Roles is { Count: 3 }, "Invalid setup status metadata.");
        var roles = new HashSet<SetupRole>();
        foreach (var item in Roles!)
        {
            ContractRules.Require(item is not null, "A setup role status is required.");
            ContractRules.Defined(item!.Role);
            ContractRules.Require(roles.Add(item.Role) && (item.RouteSelected || !item.DestinationSelected && !item.CredentialReferenced),
                "Setup role status must be unique and consistent.");
        }
    }

    public string Describe()
    {
        Validate();
        return $"Setup checkpoint: {Checkpoint}; configuration only, voice setup NOT complete.{Environment.NewLine}" +
            string.Join(Environment.NewLine, Roles.Select(role =>
                $"{role.Role}: {(role.RouteSelected ? "route selected" : "not configured")}; " +
                $"{(role.DestinationSelected ? "destination selected, not per-turn permission" : "consent missing or invalidated; review in Setup")}; " +
                $"{(role.CredentialReferenced ? "key referenced, presence/API validity unknown" : "key not configured")}; not connected.")) +
            $"{Environment.NewLine}Capture/screen/memory OFF. {Audio?.Describe() ?? "Audio qualification NOT RUN."} Price and quota unknown. " +
            $"Detached key removals pending: {PendingRemovals}. Open Setup / resume; no secret lookup or network request was made.";
    }
}
