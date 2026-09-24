using System.Security.Cryptography;
using Martlet.Gateway.Persistence;
using static Martlet.Gateway.Persistence.LinuxFileSystem;

namespace Martlet.Gateway.Persistence.Portable.Tests;

// Models only the syscall boundary. Production ownership and redo code run unchanged.
internal sealed class FakeLinuxFileSystem : ILinuxFileSystem, IDisposable
{
    internal sealed class Node
    {
        internal LinuxFileIdentity Identity;
        internal byte[] Bytes = [];
        internal byte[] DurableBytes = [];
        internal Dictionary<string, Node> Children = new(StringComparer.Ordinal);
        internal Dictionary<string, Node> DurableChildren = new(StringComparer.Ordinal);
        internal bool Acl, Locked;
    }

    private sealed class Opened(Node node)
    {
        internal Node Node = node;
        internal int Offset;
        internal bool OwnsLock;
    }

    private readonly Dictionary<int, Opened> handles = [];
    private readonly List<Node> nodes = [];
    private int nextFd = 10;
    internal List<string> Calls { get; } = [];
    internal Action<string>? Fault { get; set; }
    internal int TransferLimit { get; set; } = int.MaxValue;
    internal bool SupportedFileSystem { get; set; } = true;
    internal Guid Boot { get; set; } = Guid.NewGuid();
    public uint UserId { get; set; } = 1000;
    public uint GroupId { get; set; } = 1000;
    internal int OpenHandles => handles.Count;
    private readonly Node root;
    internal Node Parent => root.Children["srv"].Children["martlet"];
    internal Node Store => Parent.Children["store"];

    internal FakeLinuxFileSystem()
    {
        root = New(true, 0x1ed, 0);
        var srv = New(true, 0x1ed, 0);
        root.Children.Add("srv", srv);
        srv.Children.Add("martlet", New(true, 0x1c0, UserId));
        foreach (var node in nodes) node.DurableChildren = new(node.Children, StringComparer.Ordinal);
    }

    private Node New(bool directory, uint mode, uint user)
    {
        var node = new Node { Identity = new((ulong)nodes.Count + 1, 42, 8, 1, user,
            (ushort)((directory ? 0x4000u : 0x8000u) | mode), directory ? 2u : 1u, 0) };
        nodes.Add(node);
        return node;
    }

    private int Handle(Node node)
    {
        var fd = nextFd++;
        handles.Add(fd, new(node));
        return fd;
    }

    private void Call(string name)
    {
        Calls.Add(name);
        Fault?.Invoke(name);
    }

    public int OpenRoot() { Call("root"); return Handle(root); }

    public int OpenAt(int directory, string name, int flags, uint mode, ulong resolve)
    {
        Assert.NotEqual(0, flags & CloseOnExec);
        Assert.NotEqual(0, flags & NoFollow);
        Assert.Equal(NoLinks | Beneath, resolve & (NoLinks | Beneath));
        if (ReferenceEquals(handles[directory].Node, StoreOrNull()))
            Assert.NotEqual(0ul, resolve & NoMounts);
        Call("open:" + name);
        var parent = handles[directory].Node;
        if (!parent.Children.TryGetValue(name, out var node))
        {
            if ((flags & Create) == 0) throw Failure(GatewayPersistenceFailure.StoreMissing);
            Assert.NotEqual(0, flags & Exclusive);
            node = New(false, mode, UserId);
            parent.Children.Add(name, node);
        }
        else if ((flags & Exclusive) != 0) throw Failure(GatewayPersistenceFailure.RecoveryRequired);
        return Handle(node);
    }

    private Node? StoreOrNull() => Parent.Children.GetValueOrDefault("store");

    public void Close(int descriptor)
    {
        if (handles.Remove(descriptor, out var opened) && opened.OwnsLock) opened.Node.Locked = false;
    }

    public LinuxFileIdentity Stat(int descriptor)
    {
        var node = handles[descriptor].Node;
        return node.Identity with { Length = node.Bytes.Length };
    }

    public LinuxFileIdentity? StatAt(int directory, string name)
    {
        Call("stat:" + name);
        return handles[directory].Node.Children.TryGetValue(name, out var node)
            ? node.Identity with { Length = node.Bytes.Length } : null;
    }

    public bool HasAcl(int descriptor, bool directory) => handles[descriptor].Node.Acl;
    public void VerifyFileSystem(int descriptor)
    {
        Call("filesystem");
        if (!SupportedFileSystem) throw Failure(GatewayPersistenceFailure.UnsupportedPlatform);
    }

    public void MakeDirectory(int parent, string name, uint mode)
    {
        Assert.Equal(0x1c0u, mode);
        Call("mkdir:" + name);
        if (!handles[parent].Node.Children.TryAdd(name, New(true, mode, UserId)))
            throw Failure(GatewayPersistenceFailure.RecoveryRequired);
    }

    public void Lock(int descriptor)
    {
        Call("lock");
        var handle = handles[descriptor];
        if (handle.Node.Locked) throw Failure(GatewayPersistenceFailure.StoreBusy);
        handle.Node.Locked = true;
        handle.OwnsLock = true;
    }

    public string[] Enumerate(int directory) => handles[directory].Node.Children.Keys.ToArray();

    public int Read(int descriptor, byte[] bytes, int offset, int count)
    {
        Call("read");
        var opened = handles[descriptor];
        var available = Math.Min(Math.Min(count, TransferLimit), opened.Node.Bytes.Length - opened.Offset);
        opened.Node.Bytes.AsSpan(opened.Offset, available).CopyTo(bytes.AsSpan(offset));
        opened.Offset += available;
        return available;
    }

    public int Write(int descriptor, byte[] bytes, int offset, int count)
    {
        Call("write");
        var opened = handles[descriptor];
        var written = Math.Min(count, TransferLimit);
        Array.Resize(ref opened.Node.Bytes, opened.Offset + written);
        bytes.AsSpan(offset, written).CopyTo(opened.Node.Bytes.AsSpan(opened.Offset));
        opened.Offset += written;
        return written;
    }

    public void Flush(int descriptor)
    {
        var node = handles[descriptor].Node;
        Call((node.Identity.Mode & 0xf000) == 0x4000 ? "fsync:directory" : "fsync:file");
        CryptographicOperations.ZeroMemory(node.DurableBytes);
        node.DurableBytes = node.Bytes.ToArray();
        node.DurableChildren = new(node.Children, StringComparer.Ordinal);
    }

    public void Rename(int directory, string source, string target, bool replace)
    {
        Call($"rename:{source}:{target}:{replace}");
        var children = handles[directory].Node.Children;
        if (!replace && children.ContainsKey(target)) throw Failure(GatewayPersistenceFailure.RecoveryRequired);
        var node = children[source];
        children.Remove(source);
        children[target] = node;
    }

    public void Unlink(int directory, string name)
    {
        Call("unlink:" + name);
        Assert.True(handles[directory].Node.Children.Remove(name));
    }

    public Guid BootIdentity() { Call("boot"); return Boot; }

    internal void Put(string name, byte[] bytes)
    {
        var node = New(false, 0x180, UserId);
        node.Bytes = bytes.ToArray();
        node.DurableBytes = bytes.ToArray();
        Store.Children[name] = node;
        Store.DurableChildren = new(Store.Children, StringComparer.Ordinal);
    }

    internal void Crash()
    {
        Assert.Empty(handles);
        foreach (var node in nodes)
        {
            CryptographicOperations.ZeroMemory(node.Bytes);
            node.Bytes = node.DurableBytes.ToArray();
            node.Children = new(node.DurableChildren, StringComparer.Ordinal);
            node.Locked = false;
        }
    }

    internal static GatewayPersistenceException Failure(GatewayPersistenceFailure failure) => new(failure);

    public void Dispose()
    {
        Assert.Empty(handles);
        foreach (var node in nodes)
        {
            CryptographicOperations.ZeroMemory(node.Bytes);
            CryptographicOperations.ZeroMemory(node.DurableBytes);
        }
    }
}
