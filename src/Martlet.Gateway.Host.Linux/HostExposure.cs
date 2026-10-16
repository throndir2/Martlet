using System.Text.Json;
using Martlet.Core.Network;
using Martlet.Gateway.Persistence;

namespace Martlet.Gateway.Host.Linux;

/// <summary>exposure.json beside host.json (0600, service owner): how this host is reached from outside home, set by the owner
/// with martlet-host owner-exposure. Not part of the approved configuration; the service reads it when it starts.</summary>
internal sealed record HostExposure(IReadOnlyList<string> Outside, DateTimeOffset? OutsideSetAt, bool AllowPairingOutsideHome,
    bool TreatAllAsOutside, int? FrontDoorPort = null)
{
    internal static readonly HostExposure Default = new([], null, false, false);

    internal GatewayExposure ToGateway() => new()
    {
        AllowPairingOutsideHome = AllowPairingOutsideHome, TreatAllAsOutside = TreatAllAsOutside,
        OutsideAddresses = Outside, OutsideAddressesSetAt = OutsideSetAt, FrontDoorPort = FrontDoorPort
    };

    internal static HostExposure Read(LinuxControlDirectory directory)
    {
        if (directory.Read(LinuxControlDirectory.Exposure, LinuxControlDirectory.MaximumExposureBytes) is not { } bytes) return Default;
        try
        {
            using var document = JsonDocument.Parse(bytes, new JsonDocumentOptions { MaxDepth = 4 });
            var root = document.RootElement;
            if (root.GetProperty("schemaVersion").GetInt32() != 1) throw new HostInputException();
            var outside = root.GetProperty("outside").EnumerateArray().Select(e => NetworkRoster.NormalizeAddress(e.GetString()))
                .Take(NetworkRoster.MaximumAddresses).ToArray();
            if (outside.Any(a => a is null)) throw new HostInputException();
            DateTimeOffset? at = root.TryGetProperty("outsideSetAt", out var set) && set.ValueKind == JsonValueKind.String
                ? set.GetDateTimeOffset() : null;
            int? frontDoor = root.TryGetProperty("frontDoorPort", out var door) && door.ValueKind == JsonValueKind.Number
                ? door.GetInt32() is var port and >= 1024 and <= 65535 ? port : throw new HostInputException()
                : null;
            return new(outside!, at, root.GetProperty("allowPairingOutsideHome").GetBoolean(), root.GetProperty("treatAllAsOutside").GetBoolean(),
                frontDoor);
        }
        catch (Exception error) when (error is JsonException or KeyNotFoundException or InvalidOperationException or FormatException)
        {
            throw new HostInputException();
        }
    }

    internal void Write(LinuxControlDirectory directory) => directory.WriteExposure(JsonSerializer.SerializeToUtf8Bytes(new
    {
        schemaVersion = 1, outside = Outside, outsideSetAt = OutsideSetAt, allowPairingOutsideHome = AllowPairingOutsideHome,
        treatAllAsOutside = TreatAllAsOutside, frontDoorPort = FrontDoorPort
    }));

    internal HostExposure With(HostOptions options, DateTimeOffset now) => this with
    {
        Outside = options.ClearOutside ? [] : options.Outside.Count > 0 ? options.Outside : Outside,
        OutsideSetAt = options.ClearOutside || options.Outside.Count > 0 ? now : OutsideSetAt,
        AllowPairingOutsideHome = options.AllowPairingOutsideHome ?? AllowPairingOutsideHome,
        TreatAllAsOutside = options.TreatAllAsOutside ?? TreatAllAsOutside,
        FrontDoorPort = options.FrontDoor is { } door ? door == 0 ? null : door : FrontDoorPort
    };

    internal string Describe() =>
        $"outside addresses: {(Outside.Count == 0 ? "none" : string.Join(", ", Outside))}; " +
        $"pairing codes from outside home: {(AllowPairingOutsideHome ? "allowed" : "refused")}; " +
        $"treat every connection as outside home: {(TreatAllAsOutside ? "yes" : "no")}; " +
        $"front door: {(FrontDoorPort is { } port ? $"open on port {port}" : "closed")}";
}
