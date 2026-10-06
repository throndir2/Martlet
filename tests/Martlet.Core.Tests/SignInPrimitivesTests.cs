using System.Text;
using Martlet.Core.Access;
using Martlet.Core.Contracts;
using Martlet.Core.Network;

namespace Martlet.Core.Tests;

public sealed class SignInPrimitivesTests
{
    // RFC 6238 appendix B, SHA-1 with the 20-byte ASCII secret "12345678901234567890". The RFC lists 8 digits; a 6-digit code
    // is the same truncated value modulo 10^6, so its last six digits.
    [Theory]
    [InlineData(59L, "94287082")]
    [InlineData(1111111109L, "07081804")]
    [InlineData(1111111111L, "14050471")]
    [InlineData(1234567890L, "89005924")]
    [InlineData(2000000000L, "69279037")]
    [InlineData(20000000000L, "65353130")]
    public void Totp_matches_the_rfc_6238_vectors(long unixSeconds, string eightDigits)
    {
        var secret = Encoding.ASCII.GetBytes("12345678901234567890");
        Assert.Equal(eightDigits[2..], Totp.Code(secret, unixSeconds / Totp.StepSeconds));
        var base32 = Base32.Encode(secret);
        Assert.Equal("GEZDGNBVGY3TQOJQGEZDGNBVGY3TQOJQ", base32);
        Assert.Equal(secret, Base32.Decode(base32.ToLowerInvariant()));
        Assert.Equal(eightDigits[2..], Totp.Code(base32, DateTimeOffset.FromUnixTimeSeconds(unixSeconds)));
    }

    [Fact]
    public void Totp_accepts_one_step_of_drift_and_never_a_used_step()
    {
        var secret = Totp.NewSecret();
        var now = DateTimeOffset.FromUnixTimeSeconds(1_800_000_000);
        var step = Totp.Step(now);
        Assert.Equal(step, Totp.Verify(secret, Totp.Code(secret, now), now, -1));
        Assert.Equal(step - 1, Totp.Verify(secret, Totp.Code(secret, now.AddSeconds(-30)), now, -1));
        Assert.Null(Totp.Verify(secret, Totp.Code(secret, now.AddSeconds(-60)), now, -1));
        Assert.Null(Totp.Verify(secret, Totp.Code(secret, now), now, step));
        Assert.Null(Totp.Verify(secret, "12345", now, -1));
        Assert.StartsWith("otpauth://totp/Martlet%20home:owner?secret=" + secret, Totp.Uri("Martlet home", "owner", secret));
    }

    [Fact]
    public void Invites_round_trip_and_list_outside_addresses_first()
    {
        var invite = new NetworkInvite
        {
            HostId = "gpu-host", SpkiFingerprint = "sha256:" + new string('b', 64), Origin = "https://192.168.1.20:9443",
            Addresses = ["home.example.net:9443", "[2001:db8::5]:9443", "203.0.113.7:443"], NetworkId = "net-1", Label = "Home"
        };
        var text = invite.Write();
        Assert.StartsWith(NetworkInvite.Prefix, text);
        var parsed = NetworkInvite.Parse(" " + text[..20] + "\n" + text[20..]);
        Assert.Equal(invite.HostId, parsed.HostId);
        Assert.Equal(invite.Addresses, parsed.Addresses);
        Assert.Equal("Home", parsed.Label);
        Assert.Equal(["https://home.example.net:9443", "https://[2001:db8::5]:9443", "https://203.0.113.7:443", "https://192.168.1.20:9443"], parsed.Origins());
        Assert.Throws<ContractException>(() => NetworkInvite.Parse("martlet-pair-v1.abc"));
        Assert.Throws<ContractException>(() => NetworkInvite.Parse(text[..^4]));
    }

    [Theory]
    [InlineData("home.example.net", "home.example.net:9443")]
    [InlineData("https://home.example.net:8443/", "home.example.net:8443")]
    [InlineData("203.0.113.7", "203.0.113.7:9443")]
    [InlineData("2001:db8::5", "[2001:db8::5]:9443")]
    [InlineData("[2001:db8::5]:443", "[2001:db8::5]:443")]
    [InlineData("user@host:22", null)]
    [InlineData("host:99999", null)]
    [InlineData("host/path:1", null)]
    [InlineData("", null)]
    public void Outside_addresses_are_normalized_from_what_people_type(string typed, string? expected) =>
        Assert.Equal(expected, NetworkInvite.NormalizeAddress(typed));
}
