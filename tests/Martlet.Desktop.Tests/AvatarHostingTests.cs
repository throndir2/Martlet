using System.Buffers.Binary;
using System.Text;
using Martlet.Avatar.Hosting;
using Martlet.Avatars;
using Martlet.Core.Contracts;
using Martlet.Desktop;

namespace Martlet.Desktop.Tests;

public sealed class AvatarHostingTests
{
    internal sealed class Scope : IDisposable
    {
        internal string DirectoryPath { get; } = Path.Combine(Path.GetTempPath(), "Martlet.Avatar.Test." + Guid.NewGuid().ToString("N"));
        internal string Model => Path.Combine(DirectoryPath, "owned.vrm");
        internal Guid ProfileId { get; } = Guid.NewGuid();
        internal Scope()
        {
            Directory.CreateDirectory(DirectoryPath);
            File.WriteAllBytes(Model, [1, 2, 3]);
        }
        internal AvatarProfile Profile(string endpoint = "http://127.0.0.1:52000/") => new()
        {
            Version = 1, ProfileId = ProfileId, Renderer = AvatarRenderer.Vrm, ModelPath = Model,
            Endpoint = endpoint, Configuration = AvatarProfile.ConfigurationElement(AvatarConfiguration.Disabled)
        };
        public void Dispose() => Directory.Delete(DirectoryPath, true);
    }

    [Fact]
    public async Task Profile_store_is_atomic_revision_bound_and_not_global_settings()
    {
        using var scope = new Scope();
        var store = new AvatarProfileStore(scope.DirectoryPath);
        Assert.Null((await store.LoadAsync(scope.ProfileId)).Profile);
        var profile = scope.Profile();
        var revision = await store.SaveAsync(profile, null);
        var bytes = await File.ReadAllBytesAsync(store.FilePath);
        Assert.False(File.Exists(Path.Combine(scope.DirectoryPath, "settings.json")));
        Assert.Equal(revision, (await store.LoadAsync(scope.ProfileId)).Revision);
        await Assert.ThrowsAsync<ContractException>(() => store.SaveAsync(profile with { Endpoint = "http://127.0.0.1:52001/" }, null));
        await Assert.ThrowsAsync<ContractException>(() => store.LoadAsync(Guid.NewGuid()));
        Assert.Equal(bytes, await File.ReadAllBytesAsync(store.FilePath));
        Assert.Empty(Directory.GetFiles(scope.DirectoryPath, "*.tmp"));
    }

    [Theory]
    [InlineData("https://127.0.0.1:443/")]
    [InlineData("http://localhost:52000/")]
    [InlineData("http://192.168.1.1:52000/")]
    [InlineData("http://127.0.0.1:52000/path")]
    [InlineData("http://user:secret@127.0.0.1:52000/")]
    public void Host_profile_rejects_implicit_network_or_credentials(string endpoint)
    {
        using var scope = new Scope();
        Assert.Throws<ContractException>(() => scope.Profile(endpoint).Validate());
    }

    [Fact]
    public async Task Invalid_and_future_profile_bytes_are_never_overwritten()
    {
        using var scope = new Scope();
        var store = new AvatarProfileStore(scope.DirectoryPath);
        foreach (var bytes in new[] { Encoding.UTF8.GetBytes("{bad"),
            ContractJson.Write(scope.Profile(), AvatarProfile.MaximumBytes)
                .Select(b => b).ToArray() })
        {
            var content = bytes[0] == '{' && bytes[1] == 'b' ? bytes :
                Encoding.UTF8.GetBytes(Encoding.UTF8.GetString(bytes).Replace("\"version\": 1", "\"version\": 99"));
            await File.WriteAllBytesAsync(store.FilePath, content);
            await Assert.ThrowsAsync<ContractException>(() => store.SaveAsync(scope.Profile(), null));
            Assert.Equal(content, await File.ReadAllBytesAsync(store.FilePath));
        }
    }

    [Fact]
    public async Task Snapshot_revision_binds_owned_model_bytes()
    {
        using var scope = new Scope();
        var first = await LocalAvatarFiles.SnapshotAsync(scope.Profile(), default);
        await File.WriteAllBytesAsync(scope.Model, [1, 2, 4]);
        var next = await LocalAvatarFiles.SnapshotAsync(scope.Profile(), default);
        Assert.NotEqual(first.Revision, next.Revision);
        Assert.Equal(new byte[] { 1, 2, 3 }, first.Assets.Single().Bytes);
    }

    [Fact]
    public async Task Framing_rejects_oversize_and_truncated_messages_and_preserves_native_ranges()
    {
        using var pipe = new MemoryStream();
        var id = Guid.NewGuid();
        await RendererProtocol.WriteAsync(pipe, RendererProtocol.Message("apply", id, new { parameters = new { Param = -25.5 } }), default);
        pipe.Position = 0;
        var received = await RendererProtocol.ReadAsync(pipe, default);
        Assert.Equal(id, received.Activation);
        Assert.Equal(-25.5, received.Data.GetProperty("parameters").GetProperty("param").GetDouble());
        pipe.SetLength(4); pipe.Position = 0;
        BinaryPrimitives.WriteInt32LittleEndian(pipe.GetBuffer(), RendererProtocol.MaximumMessageBytes + 1);
        await Assert.ThrowsAsync<InvalidDataException>(() => RendererProtocol.ReadAsync(pipe, default));
        pipe.SetLength(2); pipe.Position = 0;
        await Assert.ThrowsAsync<EndOfStreamException>(() => RendererProtocol.ReadAsync(pipe, default));
    }

    [Fact]
    public async Task Controller_construction_and_disposal_do_not_create_renderer_or_enable_tee()
    {
        var starts = 0;
        await using (var controller = new AvatarController(createRenderer: () =>
        {
            starts++;
            throw new InvalidOperationException("must not activate");
        }))
        {
            Assert.False(controller.Observer.IsEnabled);
            Assert.False(controller.IsActive);
            Assert.Contains("OFF", controller.Status);
        }
        Assert.Equal(0, starts);
    }

    [Theory]
    [InlineData("https://example.com/a")]
    [InlineData("file:///C:/private.txt")]
    [InlineData("https://martlet-avatar.invalid/asset/../index.html")]
    [InlineData("https://martlet-avatar.invalid/asset/%2e%2e/index.html")]
    [InlineData("https://martlet-avatar.invalid/asset/%252e%252e/index.html")]
    [InlineData("https://martlet-avatar.invalid/asset/core.js?other=1")]
    [InlineData("https://martlet-avatar.invalid.evil/index.html")]
    public void Resource_policy_never_opens_arbitrary_paths_or_network(string url) =>
        Assert.Null(RendererResourcePolicy.ResourceName(url, "GET"));

    [Fact]
    public void Resource_policy_handles_authorized_spaces_but_not_non_get_requests()
    {
        Assert.Equal("asset/texture%20one.png", RendererResourcePolicy.ResourceName(
            RendererResourcePolicy.Origin + "asset/texture%20one.png", "GET"));
        Assert.Null(RendererResourcePolicy.ResourceName(RendererResourcePolicy.Document, "POST"));
        Assert.Equal(RendererResourcePolicy.CanonicalName("asset/My Model.model3.json"),
            RendererResourcePolicy.ResourceName(RendererResourcePolicy.Origin + "asset/My%20Model.model3.json", "GET"));
        Assert.Null(RendererResourcePolicy.ResourceName(RendererResourcePolicy.Origin + "asset%2fcore.js", "GET"));
    }

    [Fact]
    public void Wasm_permission_is_narrow_to_live2d_and_fatal_state_cannot_be_acknowledged_as_success()
    {
        Assert.DoesNotContain("wasm-unsafe-eval", RendererResourcePolicy.ContentSecurityPolicy(false));
        Assert.Contains("'wasm-unsafe-eval'", RendererResourcePolicy.ContentSecurityPolicy(true));
        Assert.DoesNotContain("'unsafe-eval'", RendererResourcePolicy.ContentSecurityPolicy(true));
        var state = new RendererFailureLatch();
        state.ThrowIfFailed();
        Assert.True(state.Fail());
        Assert.False(state.Fail());
        Assert.Throws<InvalidDataException>(state.ThrowIfFailed);
    }

    [Fact]
    public async Task Explicit_recovery_retains_invalid_original_and_resets_consent()
    {
        using var scope = new Scope();
        var store = new AvatarProfileStore(scope.DirectoryPath);
        byte[] invalid = Encoding.UTF8.GetBytes("{broken");
        await File.WriteAllBytesAsync(store.FilePath, invalid);
        var revision = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(invalid));
        await Assert.ThrowsAsync<ContractException>(() => store.RestoreAsync(scope.Profile(), "wrong"));
        await store.RestoreAsync(scope.Profile(), revision);
        var restored = await store.LoadAsync(scope.ProfileId);
        Assert.False(restored.Profile!.Settings.Enabled);
        Assert.Null(restored.Profile.ResourceRevision);
        Assert.Equal(invalid, await File.ReadAllBytesAsync(Assert.Single(Directory.GetFiles(scope.DirectoryPath, "*.bak"))));
    }
}
