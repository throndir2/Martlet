using System.Text.Json;
using Martlet.Core.Contracts;
using Martlet.Core.Settings;
using Martlet.Credentials.Windows;

namespace Martlet.Core.Tests;

public sealed class WindowsCredentialTests
{
    private static CredentialBinding Binding => new(Guid.NewGuid(), Guid.NewGuid(), SetupRole.Llm, "openai-llm", OpenAiSetup.Origin);

    [Theory]
    [InlineData(1168, CredentialError.Missing)]
    [InlineData(5, CredentialError.AccessDenied)]
    [InlineData(87, CredentialError.InvalidInput)]
    [InlineData(13, CredentialError.InvalidInput)]
    [InlineData(1312, CredentialError.Unavailable)]
    [InlineData(999, CredentialError.Unavailable)]
    public void NativeFailuresAreTypedSanitizedAndNeverSuccessShaped(int nativeError, CredentialError expected)
    {
        using var native = new FakeCredentialNative { ReadError = nativeError, WriteError = nativeError, DeleteError = nativeError };
        var store = new WindowsCredentialStore(native);
        using var secret = new SecretLease("PRIVATE-CANARY");
        Assert.Equal(expected, store.Write(Binding, secret));
        using var read = store.Read(Binding);
        Assert.Equal(expected, read.Error);
        Assert.Null(read.Secret);
        Assert.Equal(expected, store.Delete(Binding));
        Assert.DoesNotContain("PRIVATE-CANARY", read.ToString() + CredentialMessages.Describe(expected));
    }

    [Fact]
    public void PlatformGuardAndBindingPolicyRejectBeforeNativeAccess()
    {
        using var native = new FakeCredentialNative();
        var store = new WindowsCredentialStore(native);
        using var secret = new SecretLease("PRIVATE-CANARY");
        foreach (var binding in new[]
        {
            Binding with { Origin = "https://untrusted.example" },
            Binding with { Origin = OpenAiSetup.Origin + "/" },
            Binding with { ProviderAlias = "openai-stt" },
            Binding with { Role = (SetupRole)99 },
            Binding with { CredentialId = Guid.Empty }, Binding with { ProfileId = Guid.Empty }
        })
        {
            Assert.Equal(CredentialError.InvalidInput, store.Write(binding, secret));
            using var read = store.Read(binding);
            Assert.Equal(CredentialError.InvalidInput, read.Error);
            Assert.Equal(CredentialError.InvalidInput, store.Delete(binding));
        }
        native.IsSupported = false;
        Assert.Equal(CredentialError.UnsupportedPlatform, store.Write(Binding, secret));
        using var unavailable = store.Read(Binding);
        Assert.Equal(CredentialError.UnsupportedPlatform, unavailable.Error);
        Assert.Equal(CredentialError.UnsupportedPlatform, store.Delete(Binding));
        Assert.Empty(native.Events);
    }

    [Theory]
    [InlineData("")]
    [InlineData("a\r\nInjected:value")]
    [InlineData("a b")]
    [InlineData("private\u0000key")]
    [InlineData("key:abc")]
    public void SecretsRejectHeaderInjectionWithoutEchoingInput(string input)
    {
        var exception = Assert.Throws<ContractException>(() => new SecretLease(input));
        Assert.DoesNotContain(input.Length > 0 ? input : "PRIVATE-CANARY", exception.ToString());
    }

    [Fact]
    public void SecretsAreBoundedDisposedAndNotSerializedOrPrinted()
    {
        Assert.Throws<ContractException>(() => new SecretLease(new string('a', SecretLease.MaximumLength + 1)));
        var chars = "PRIVATE-CANARY".ToCharArray();
        var secret = new SecretLease(chars);
        Array.Clear(chars);
        secret.Use(value => Assert.Equal("PRIVATE-CANARY", new string(value)));
        Assert.DoesNotContain("PRIVATE-CANARY", secret.ToString());
        Assert.Equal("\"[credential redacted]\"", JsonSerializer.Serialize(secret));
        Assert.Throws<JsonException>(() => JsonSerializer.Deserialize<SecretLease>("\"PRIVATE-CANARY\""));
        using var read = new CredentialReadResult(CredentialError.None, secret);
        Assert.DoesNotContain("PRIVATE-CANARY", JsonSerializer.Serialize(read));
        Assert.DoesNotContain("PRIVATE-CANARY", read.ToString());
        secret.Dispose();
        Assert.Throws<ObjectDisposedException>(() => secret.Use(_ => { }));
        secret.Dispose();
    }

    [Fact]
    public void SameOpaqueIdCannotResolveAcrossRoleProfileOrOrigin()
    {
        using var native = new FakeCredentialNative();
        var store = new WindowsCredentialStore(native);
        var binding = Binding;
        using var secret = new SecretLease("PRIVATE-CANARY");
        Assert.Equal(CredentialError.None, store.Write(binding, secret));
        using var wrongProfile = store.Read(binding with { ProfileId = Guid.NewGuid() });
        Assert.Equal(CredentialError.Missing, wrongProfile.Error);
        using var wrongRole = store.Read(binding with { Role = SetupRole.Stt, ProviderAlias = "openai-stt" });
        Assert.Equal(CredentialError.Missing, wrongRole.Error);
        using var good = store.Read(binding);
        Assert.Equal(CredentialError.None, good.Error);
        Assert.NotNull(good.Secret);
        Assert.All(native.Events, item => Assert.DoesNotContain("PRIVATE-CANARY", item));
    }
}
