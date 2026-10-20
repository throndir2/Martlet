namespace Martlet.Core.Sync;

/// <summary>A shared setting whose apply needs the setting's secret (an API key or a bot token), unlike
/// <see cref="DelegateSection"/>, which passes none.</summary>
public sealed class SecretSection(string key, string title, Func<CancellationToken, Task<SharedLocal?>> read,
    Func<SharedSetting, string?, CancellationToken, Task<SharedApply>> apply, Func<string?>? defaultValue = null) : ISharedSection
{
    public string Key => key;
    public string Title => title;
    public string? Default => defaultValue?.Invoke();
    public Task<SharedLocal?> ReadAsync(CancellationToken token) => read(token);
    public Task<SharedApply> ApplyAsync(SharedSetting setting, string? secret, CancellationToken token) => apply(setting, secret, token);
}
