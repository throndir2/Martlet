using System.Net;

namespace Martlet.Host.Setup.Tests;

public sealed class AcquisitionTransportConnectionTests
{
    [Theory]
    [InlineData("0.0.0.0")]
    [InlineData("0.1.2.3")]
    [InlineData("10.0.0.1")]
    [InlineData("100.64.0.1")]
    [InlineData("100.127.255.255")]
    [InlineData("127.0.0.1")]
    [InlineData("169.254.169.254")]
    [InlineData("172.16.0.1")]
    [InlineData("172.31.255.255")]
    [InlineData("192.168.0.1")]
    [InlineData("192.0.0.9")]
    [InlineData("192.0.2.1")]
    [InlineData("192.88.99.1")]
    [InlineData("198.18.0.1")]
    [InlineData("198.19.255.255")]
    [InlineData("198.51.100.1")]
    [InlineData("203.0.113.1")]
    [InlineData("224.0.0.1")]
    [InlineData("240.0.0.1")]
    [InlineData("255.255.255.255")]
    [InlineData("::")]
    [InlineData("::1")]
    [InlineData("::ffff:8.8.8.8")]
    [InlineData("::ffff:127.0.0.1")]
    [InlineData("64:ff9b::808:808")]
    [InlineData("100::1")]
    [InlineData("2001::1")]
    [InlineData("2001:2::1")]
    [InlineData("2001:db8::1")]
    [InlineData("2002:808:808::1")]
    [InlineData("3fff::1")]
    [InlineData("fc00::1")]
    [InlineData("fd00::1")]
    [InlineData("fe80::1")]
    [InlineData("ff02::1")]
    public void Non_global_addresses_are_rejected(string address) =>
        Assert.False(ArtifactDownloadConnectionPolicy.IsGloballyReachable(IPAddress.Parse(address)));

    [Theory]
    [InlineData("8.8.8.8")]
    [InlineData("140.82.112.5")]
    [InlineData("185.199.108.133")]
    [InlineData("2606:4700:4700::1111")]
    public void Ordinary_global_unicast_addresses_are_allowed_without_connecting(string address) =>
        Assert.True(ArtifactDownloadConnectionPolicy.IsGloballyReachable(IPAddress.Parse(address)));

    [Fact]
    public async Task Mixed_dns_fails_closed_before_any_connection()
    {
        var connects = 0;
        var error = await Assert.ThrowsAsync<ArtifactAcquisitionException>(async () =>
            await ArtifactDownloadConnectionPolicy.ResolveAndConnectAsync(
                new DnsEndPoint("api.github.com", 443), CancellationToken.None,
                (_, _) => Task.FromResult(new[] { IPAddress.Parse("8.8.8.8"), IPAddress.Loopback }),
                (_, _, _) => { connects++; return ValueTask.FromResult<Stream>(new MemoryStream()); }));
        Assert.Equal(ArtifactAcquisitionFailure.TransportFailed, error.Failure);
        Assert.Equal(0, connects);
    }

    [Fact]
    public async Task Vetted_address_is_connected_directly_once_without_second_resolution()
    {
        var resolves = 0;
        var connects = 0;
        var expected = IPAddress.Parse("8.8.8.8");
        using var stream = await ArtifactDownloadConnectionPolicy.ResolveAndConnectAsync(
            new DnsEndPoint("release-assets.githubusercontent.com", 443), CancellationToken.None,
            (host, _) =>
            {
                resolves++;
                Assert.Equal("release-assets.githubusercontent.com", host);
                return Task.FromResult(new[] { expected, IPAddress.Parse("1.1.1.1") });
            },
            (address, port, _) =>
            {
                connects++;
                Assert.Same(expected, address);
                Assert.Equal(443, port);
                return ValueTask.FromResult<Stream>(new MemoryStream([1, 2, 3]));
            });
        Assert.Equal(1, resolves);
        Assert.Equal(1, connects);
        Assert.Equal(1, stream.ReadByte());
    }

    [Theory]
    [InlineData("localhost", 443)]
    [InlineData("api.github.com.evil.invalid", 443)]
    [InlineData("api.github.com.", 443)]
    [InlineData("api.github.com", 80)]
    [InlineData("huggingface.co", 443)]
    public async Task Only_exact_approved_hostnames_and_port_can_be_resolved(string host, int port)
    {
        var resolves = 0;
        await Assert.ThrowsAsync<ArtifactAcquisitionException>(async () =>
            await ArtifactDownloadConnectionPolicy.ResolveAndConnectAsync(
                new DnsEndPoint(host, port), CancellationToken.None,
                (_, _) => { resolves++; return Task.FromResult(Array.Empty<IPAddress>()); },
                (_, _, _) => throw new InvalidOperationException()));
        Assert.Equal(0, resolves);
    }

    [Fact]
    public async Task Cancellation_after_dns_prevents_connect()
    {
        using var cancellation = new CancellationTokenSource();
        var connects = 0;
        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () =>
            await ArtifactDownloadConnectionPolicy.ResolveAndConnectAsync(
                new DnsEndPoint("api.github.com", 443), cancellation.Token,
                (_, _) =>
                {
                    cancellation.Cancel();
                    return Task.FromResult(new[] { IPAddress.Parse("8.8.8.8") });
                },
                (_, _, _) => { connects++; return ValueTask.FromResult<Stream>(new MemoryStream()); }));
        Assert.Equal(0, connects);
    }

    [Fact]
    public async Task Connection_failure_does_not_retry_other_dns_addresses()
    {
        var connects = 0;
        await Assert.ThrowsAsync<IOException>(async () =>
            await ArtifactDownloadConnectionPolicy.ResolveAndConnectAsync(
                new DnsEndPoint("api.github.com", 443), CancellationToken.None,
                (_, _) => Task.FromResult(new[] { IPAddress.Parse("8.8.8.8"), IPAddress.Parse("1.1.1.1") }),
                (_, _, _) => { connects++; throw new IOException("Inert connection failure."); }));
        Assert.Equal(1, connects);
    }
}
