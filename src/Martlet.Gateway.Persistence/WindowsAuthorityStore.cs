using System.Runtime.Versioning;

namespace Martlet.Gateway.Persistence;

[SupportedOSPlatform("windows")]
internal sealed class WindowsAuthorityStore(AuthorityStore store) : IGatewayPersistence, IDisposable
{
    internal AuthorityStore Store => store;
    internal string HostId => store.HostId;
    internal GatewayCheckpoint Initial => store.Initial;
    internal bool InitialSameBoot => store.InitialSameBoot;

    internal static WindowsAuthorityStore Create(string path, string hostId, byte[] certificate,
        DateTimeOffset now, Action<StoreStep>? fault, TimeProvider? clock = null)
    {
        var boot = WindowsBootIdentity.Read();
        return new(AuthorityStore.Create(WindowsOwnedDirectory.Open(path, true), new WindowsDpapiEnvelope(),
            boot, hostId, certificate, now, fault, clock));
    }

    internal static WindowsAuthorityStore Open(string path, DateTimeOffset now,
        Action<StoreStep>? fault, Guid? bootIdentity = null)
    {
        var boot = bootIdentity ?? WindowsBootIdentity.Read();
        return new(AuthorityStore.Open(WindowsOwnedDirectory.Open(path, false), new WindowsDpapiEnvelope(),
            boot, now, fault));
    }

    internal byte[] CopyCertificate() => store.CopyCertificate();
    internal void ReplaceCertificate(byte[] replacement, GatewayCheckpoint checkpoint) =>
        store.ReplaceCertificate(replacement, checkpoint);
    public void Commit(GatewayCheckpoint checkpoint) => store.Commit(checkpoint);
    public void Complete(GatewayCheckpoint checkpoint, Action validateBeforeClean) =>
        store.Complete(checkpoint, validateBeforeClean);
    public void Dispose() => store.Dispose();
}
