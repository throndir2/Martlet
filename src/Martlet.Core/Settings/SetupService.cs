using Martlet.Core.Contracts;

namespace Martlet.Core.Settings;

public sealed record SetupSaveResult(SettingsSaveResult Save, AppSettings Settings,
    CredentialError CredentialError = CredentialError.None)
{
    public string Summary => (Save.Saved
        ? Save.MigratedFromSchemaVersion is { } previous
            ? $"Setup saved. Version {previous} was migrated with an atomic original-file snapshot. Saving does not establish live account/device readiness."
            : "Setup saved. Saving does not establish live account/device readiness."
        : Save.Error?.Summary ?? "Setup was not saved.") +
        (CredentialError != CredentialError.None ? " " + CredentialMessages.Describe(CredentialError) : "");
}

public interface ISetupService
{
    Task<SettingsLoadResult> LoadAsync(CancellationToken token = default);
    Task<SetupSaveResult> SaveAsync(AppSettings settings, string? revision, CancellationToken token = default);
    Task<SetupSaveResult> ReplaceCredentialAsync(AppSettings settings, string? revision, SetupRole role, SecretLease secret, CancellationToken token = default);
    Task<SetupSaveResult> DetachCredentialAsync(AppSettings settings, string? revision, SetupRole role, CancellationToken token = default);
    Task<SetupSaveResult> RemoveDetachedAsync(AppSettings settings, string? revision, PendingCredentialRemoval removal, CancellationToken token = default);
    CredentialError CheckCredential(AppSettings settings, SetupRole role);
}

// Call from a worker: native vault calls and filesystem open/flush/replace can block.
public sealed class SetupService(SettingsStore settingsStore, ICredentialStore credentials) : ISetupService
{
    public Task<SettingsLoadResult> LoadAsync(CancellationToken token = default) => settingsStore.LoadAsync(token);

    public async Task<SetupSaveResult> SaveAsync(AppSettings settings, string? revision, CancellationToken token = default)
    {
        var save = await settingsStore.SaveAsync(settings, revision, token);
        return new(save, settings);
    }

    public Task<SetupSaveResult> ReplaceCredentialAsync(AppSettings settings, string? revision,
        SetupRole role, SecretLease secret, CancellationToken token = default)
    {
        // Validate before any vault write. Changes to the key invalidate the old destination acceptance.
        settings.Validate();
        var route = RequireRoute(settings, role);
        var id = Guid.NewGuid();
        var updated = SetupSettings.ReplaceRoute(settings, route.WithCredential(id));
        if (route.CredentialId is { } old)
            updated = QueueRemoval(updated, role, old);
        updated.Validate();
        var staged = QueueRemoval(settings, role, id);
        return settingsStore.ReplaceCredentialAsync(staged, updated, revision, role, id, secret, credentials, token);
    }

    public async Task<SetupSaveResult> DetachCredentialAsync(AppSettings settings, string? revision,
        SetupRole role, CancellationToken token = default)
    {
        settings.Validate();
        var route = RequireRoute(settings, role);
        if (route.CredentialId is not { } old)
            throw new ContractException(ErrorCode.InvalidContract, "No credential is selected for this role.");
        var updated = QueueRemoval(SetupSettings.ReplaceRoute(settings, route.WithCredential(null)), route, old);
        return await SaveAsync(updated, revision, token);
    }

    public async Task<SetupSaveResult> RemoveDetachedAsync(AppSettings settings, string? revision,
        PendingCredentialRemoval removal, CancellationToken token = default)
    {
        settings.Validate();
        ContractRules.Require(settings.Setup!.PendingRemovals.Contains(removal),
            "Only a detached credential listed in this profile can be removed.");
        // Hold the same cooperating-writer lock as settings saves for the irreversible native delete.
        // A stale profile can never revoke a reference restored by a newer writer.
        return await settingsStore.RemoveDetachedCredentialAsync(settings, revision, removal, credentials, token);
    }

    public CredentialError CheckCredential(AppSettings settings, SetupRole role)
    {
        settings.Validate();
        var route = RequireRoute(settings, role);
        if (route.CredentialId is not { } id) return CredentialError.Missing;
        using var result = credentials.Read(CredentialBinding.For(settings, role, id));
        return result.Error;
    }

    private static SetupRoute RequireRoute(AppSettings settings, SetupRole role) =>
        settings.Setup?.Routes.SingleOrDefault(r => r.Role == role) ??
        throw new ContractException(ErrorCode.InvalidContract, "Select and apply a named route before managing its credential.");

    private static AppSettings QueueRemoval(AppSettings settings, SetupRole role, Guid old)
    {
        var route = RequireRoute(settings, role);
        return QueueRemoval(settings, route, old);
    }

    private static AppSettings QueueRemoval(AppSettings settings, SetupRoute route, Guid old)
    {
        var updated = settings with { Setup = settings.Setup! with
        {
            PendingRemovals = settings.Setup!.PendingRemovals.Append(new()
            {
                Role = route.Role,
                CredentialId = old,
                Scope = route.RouteType is SetupRouteType.GatewayOllama or SetupRouteType.GatewayF5
                    ? CredentialScopeSettings.From(route)
                    : null
            }).ToArray()
        } };
        updated.Validate();
        return updated;
    }

}
