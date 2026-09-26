using Martlet.Core.Contracts;
using Martlet.Core.Settings;

namespace Martlet.Credentials.Windows;

public interface ICredentialNative
{
    bool IsSupported { get; }
    int Write(string target, ReadOnlySpan<char> secret);
    int Read(string target, out SecretLease? secret);
    int Delete(string target);
}

public sealed class WindowsCredentialStore(ICredentialNative native) : ICredentialStore
{
    public WindowsCredentialStore() : this(new WindowsCredentialNative()) { }

    public CredentialError Write(CredentialBinding binding, SecretLease secret)
    {
        if (!Valid(binding)) return CredentialError.InvalidInput;
        if (!native.IsSupported) return CredentialError.UnsupportedPlatform;
        var error = CredentialError.InvalidInput;
        secret.Use(value => error = Map(native.Write(Target(binding), value)));
        return error;
    }

    public CredentialReadResult Read(CredentialBinding binding)
    {
        if (!Valid(binding)) return new(CredentialError.InvalidInput, null);
        if (!native.IsSupported) return new(CredentialError.UnsupportedPlatform, null);
        var result = Map(native.Read(Target(binding), out var secret));
        if (result != CredentialError.None) { secret?.Dispose(); return new(result, null); }
        return secret is null ? new(CredentialError.Unavailable, null) : new(result, secret);
    }

    public CredentialError Delete(CredentialBinding binding)
    {
        if (!Valid(binding)) return CredentialError.InvalidInput;
        return native.IsSupported ? Map(native.Delete(Target(binding))) : CredentialError.UnsupportedPlatform;
    }

    private static bool Valid(CredentialBinding binding)
    {
        try { binding.Validate(); return true; }
        catch (ContractException) { return false; }
    }

    private static string Target(CredentialBinding binding) =>
        binding.RouteType == SetupRouteType.OpenAi
            ? $"Martlet/v2/{binding.ProfileId:N}/api.openai.com/{binding.ProviderAlias}/{binding.CredentialId:N}"
            : binding.RouteType == SetupRouteType.ChatCompletions
                ? $"Martlet/v3/{binding.ProfileId:N}/chat-completions/{binding.ScopeDigest()}/{binding.CredentialId:N}"
                : $"Martlet/v3/{binding.ProfileId:N}/gateway/{binding.RouteType}/{binding.ScopeDigest()}/{binding.CredentialId:N}";

    private static CredentialError Map(int error) => error switch
    {
        0 => CredentialError.None, 1168 => CredentialError.Missing, 5 => CredentialError.AccessDenied,
        87 or 13 => CredentialError.InvalidInput, _ => CredentialError.Unavailable
    };
}
