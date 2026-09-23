using System.Text;
using Martlet.Gateway.Trust;

namespace Martlet.Gateway.Persistence.Tests;

public sealed class FormatTests
{
    [Theory]
    [InlineData("{}")]
    [InlineData("null")]
    [InlineData("{\"Version\":1,\"Version\":1}")]
    [InlineData("{\"Version\":2}")]
    public void MalformedDocumentsHaveOnlySanitizedFailure(string json)
    {
        var error = Assert.Throws<GatewayPersistenceException>(() => StoreFormat.Read(Encoding.UTF8.GetBytes(json)));
        Assert.Equal(GatewayPersistenceFailure.InvalidState, error.Failure);
    }

    [Fact]
    public void StrictBoundedRoundtripRejectsUnknownMembersDevicesScopesAndLifetimeInflation()
    {
        var now = DateTimeOffset.UtcNow;
        using var document = new StoreDocument(1, Guid.NewGuid(), Guid.NewGuid(), 1, [1],
            new(now, [new(Guid.NewGuid(), GatewayScope.Status,
                new(new byte[32], new(now.AddDays(90), TimeSpan.FromDays(90).Ticks)), null, null)]));
        var bytes = StoreFormat.Write(document);
        using var restored = StoreFormat.Read(bytes);
        Assert.Equal(document.Trust.Devices[0].DeviceId, restored.Trust.Devices[0].DeviceId);
        var text = Encoding.UTF8.GetString(bytes);
        foreach (var mutation in new[]
        {
            text.Insert(1, "\"Unknown\":1,"),
            text.Replace("\"Version\":1", "\"Version\":2", StringComparison.Ordinal),
            text.Replace("\"Scopes\":1", "\"Scopes\":128", StringComparison.Ordinal),
            text.Replace("\"Revision\":1", "\"Revision\":0", StringComparison.Ordinal),
            text.Replace("\"Devices\":[{", "\"Devices\":[null,{", StringComparison.Ordinal),
            text.Replace("\"RemainingTicks\":77760000000000", "\"RemainingTicks\":77760000000001", StringComparison.Ordinal)
        })
            Assert.Throws<GatewayPersistenceException>(() => StoreFormat.Read(Encoding.UTF8.GetBytes(mutation)));
        Assert.Throws<GatewayPersistenceException>(() => StoreFormat.Read(new byte[StoreFormat.MaximumBytes]));
        using var tooMany = new StoreDocument(1, Guid.NewGuid(), Guid.NewGuid(), 1, [1],
            new(now, Enumerable.Repeat(document.Trust.Devices[0], 129).ToArray()));
        Assert.Throws<GatewayPersistenceException>(() => StoreFormat.Write(tooMany));
    }

    [Fact]
    public void EnvelopeRejectsVersionLengthTruncationAndTrailingBytes()
    {
        var wrapped = StoreFormat.Wrap([1, 2, 3]);
        Assert.Equal([1, 2, 3], StoreFormat.Unwrap(wrapped));
        foreach (var offset in new[] { 0, 8, 12 })
        {
            var changed = wrapped.ToArray();
            changed[offset]++;
            Assert.Throws<GatewayPersistenceException>(() => StoreFormat.Unwrap(changed));
        }
        Assert.Throws<GatewayPersistenceException>(() => StoreFormat.Unwrap(wrapped[..^1]));
        Assert.Throws<GatewayPersistenceException>(() => StoreFormat.Unwrap([.. wrapped, 0]));
    }
}
