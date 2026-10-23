using System.Buffers.Binary;
using System.Security.Cryptography;

namespace Martlet.Core.Hardware;

/// <summary>A TPM in software for tests and MCP self-tests: it answers the three commands <see cref="Tpm2"/> sends
/// (CreatePrimary, Sign, FlushContext) with one P-256 key per hierarchy, the same key each time, like a real chip. A new
/// instance is another computer's chip. <see cref="Refuse"/> makes a hierarchy refuse, like a TPM whose hierarchy has a
/// password.</summary>
public sealed class SimulatedTpm
{
    private readonly Dictionary<uint, ECDsa> keys = [];
    private readonly Dictionary<uint, uint> handles = [];
    private uint next = 0x80000000;

    /// <summary>Hierarchies that answer TPM_RC_BAD_AUTH.</summary>
    public HashSet<uint> Refuse { get; } = [];

    /// <summary>A transport to this chip (each <see cref="HardwareKeys"/> call opens its own, as with a real device).</summary>
    public ITpmTransport Open() => new Transport(this);

    private byte[] Answer(byte[] command)
    {
        var code = BinaryPrimitives.ReadUInt32BigEndian(command.AsSpan(6));
        var size = BinaryPrimitives.ReadUInt32BigEndian(command.AsSpan(2));
        if (size != command.Length) return Header(0x8001, 0x95, []); // TPM_RC_COMMAND_SIZE
        switch (code)
        {
            case 0x131:
            {
                var hierarchy = BinaryPrimitives.ReadUInt32BigEndian(command.AsSpan(10));
                if (Refuse.Contains(hierarchy)) return Header(0x8001, 0x9A2, []);
                if (!keys.TryGetValue(hierarchy, out var key)) keys[hierarchy] = key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
                var handle = next++;
                handles[handle] = hierarchy;
                var q = key.ExportParameters(false).Q;
                var area = new List<byte>();
                U16(area, 0x0023); U16(area, 0x000B); U32(area, 0x00040072); U16(area, 0);
                U16(area, 0x0010); U16(area, 0x0018); U16(area, 0x000B); U16(area, 0x0003); U16(area, 0x0010);
                Sized(area, q.X!); Sized(area, q.Y!);
                var body = new List<byte>();
                U32(body, handle);
                var parameters = new List<byte>();
                Sized(parameters, [.. area]);
                U32(body, (uint)parameters.Count);
                body.AddRange(parameters);
                return Header(0x8002, 0, [.. body]);
            }
            case 0x15D:
            {
                var handle = BinaryPrimitives.ReadUInt32BigEndian(command.AsSpan(10));
                if (!handles.TryGetValue(handle, out var hierarchy)) return Header(0x8001, 0x18B, []); // TPM_RC_HANDLE
                var at = 14 + 4 + (int)BinaryPrimitives.ReadUInt32BigEndian(command.AsSpan(14));
                var length = BinaryPrimitives.ReadUInt16BigEndian(command.AsSpan(at));
                var digest = command.AsSpan(at + 2, length).ToArray();
                var signature = keys[hierarchy].SignHash(digest);
                var parameters = new List<byte>();
                U16(parameters, 0x0018); U16(parameters, 0x000B);
                Sized(parameters, signature[..32]); Sized(parameters, signature[32..]);
                var body = new List<byte>();
                U32(body, (uint)parameters.Count);
                body.AddRange(parameters);
                return Header(0x8002, 0, [.. body]);
            }
            case 0x165:
                handles.Remove(BinaryPrimitives.ReadUInt32BigEndian(command.AsSpan(10)));
                return Header(0x8001, 0, []);
            default:
                return Header(0x8001, 0x143, []); // TPM_RC_COMMAND_CODE
        }
    }

    private static byte[] Header(ushort tag, uint rc, byte[] body)
    {
        var all = new List<byte>();
        U16(all, tag); U32(all, (uint)(10 + body.Length)); U32(all, rc);
        all.AddRange(body);
        return [.. all];
    }

    private static void U16(List<byte> b, ushort v) { b.Add((byte)(v >> 8)); b.Add((byte)v); }
    private static void U32(List<byte> b, uint v) { U16(b, (ushort)(v >> 16)); U16(b, (ushort)v); }
    private static void Sized(List<byte> b, byte[] v) { U16(b, (ushort)v.Length); b.AddRange(v); }

    private sealed class Transport(SimulatedTpm tpm) : ITpmTransport
    {
        public byte[] Submit(byte[] command)
        {
            lock (tpm) return tpm.Answer(command);
        }

        public void Dispose() { }
    }
}
