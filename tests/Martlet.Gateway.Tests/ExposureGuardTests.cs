using System.Net;
using System.Text.Json;
using Martlet.Gateway;
using Microsoft.AspNetCore.Http;

namespace Martlet.Gateway.Tests;

public sealed class ExposureGuardTests
{
    [Theory]
    [InlineData("127.0.0.1", GatewaySourceKind.Loopback)]
    [InlineData("::1", GatewaySourceKind.Loopback)]
    [InlineData("192.168.1.20", GatewaySourceKind.Home)]
    [InlineData("10.0.0.5", GatewaySourceKind.Home)]
    [InlineData("172.20.1.1", GatewaySourceKind.Home)]
    [InlineData("::ffff:192.168.1.20", GatewaySourceKind.Home)]
    [InlineData("fd12:3456::1", GatewaySourceKind.Home)]
    [InlineData("100.101.102.103", GatewaySourceKind.Outside)]
    [InlineData("8.8.8.8", GatewaySourceKind.Outside)]
    [InlineData("172.32.0.1", GatewaySourceKind.Outside)]
    [InlineData("2001:db8::1", GatewaySourceKind.Outside)]
    public void Sources_are_classified_by_their_connection_address(string address, GatewaySourceKind expected) =>
        Assert.Equal(expected, GatewayRequestGuard.Classify(IPAddress.Parse(address)));

    private static (GatewayRequestGuard Guard, ManualGatewayClock Clock, List<string> Log) NewGuard()
    {
        var clock = new ManualGatewayClock(new DateTimeOffset(2026, 10, 6, 8, 0, 0, TimeSpan.Zero));
        var log = new List<string>();
        return (new GatewayRequestGuard(clock, (level, message, _) => log.Add(level + " " + message)), clock, log);
    }

    private static DefaultHttpContext From(string address)
    {
        var context = new DefaultHttpContext();
        context.Connection.RemoteIpAddress = IPAddress.Parse(address);
        return context;
    }

    [Fact]
    public void Failures_lock_out_an_outside_address_with_doubling_waits_and_a_success_clears_it()
    {
        var (guard, clock, log) = NewGuard();
        var outside = From("203.0.113.9");
        for (var i = 0; i < GatewayRequestGuard.FreeFailures - 1; i++)
        {
            guard.Admit(outside, "credential");
            guard.Failed(outside, "credential", "auth.invalid");
        }
        guard.Admit(outside, "credential");
        guard.Failed(outside, "credential", "auth.invalid"); // fifth failure starts a 1 s lockout
        var error = Assert.Throws<GatewayProtocolException>(() => guard.Admit(outside, "credential"));
        Assert.Equal("auth.throttled", error.Failure.Code);
        Assert.Equal(429, error.Failure.HttpStatus);
        Assert.Equal("1", outside.Response.Headers.RetryAfter.ToString());
        clock.Advance(TimeSpan.FromSeconds(1));
        guard.Admit(outside, "credential");
        guard.Failed(outside, "credential", "auth.invalid"); // sixth: 2 s
        clock.Advance(TimeSpan.FromSeconds(1));
        Assert.Throws<GatewayProtocolException>(() => guard.Admit(outside, "credential"));
        clock.Advance(TimeSpan.FromSeconds(1));
        guard.Admit(outside, "credential");
        Assert.Contains(log, line => line.StartsWith("WARN Locked out credential requests from 203.0.113.9 (outside home)"));
        // Non-authentication failures never count.
        guard.Failed(outside, "credential", "job.busy");
        guard.Admit(outside, "credential");
        var events = guard.Recent();
        Assert.Contains(events, e => e is { Outcome: "throttled", Source: "203.0.113.9", SourceKind: "outside", Code: "auth.throttled" });
        Assert.Equal(6, events.Count(e => e.Outcome == "failure"));
    }

    [Fact]
    public void Home_and_loopback_requests_to_signed_routes_are_never_throttled_unless_the_host_is_internet_reachable()
    {
        var (guard, _, _) = NewGuard();
        foreach (var address in new[] { "127.0.0.1", "192.168.1.20" })
        {
            var context = From(address);
            for (var i = 0; i < 20; i++)
            {
                guard.Admit(context, "credential");
                guard.Failed(context, "credential", "auth.invalid");
            }
            guard.Admit(context, "pair");
            for (var i = 0; i < GatewayRequestGuard.UnauthenticatedRequestsPerMinute + 5; i++) guard.Admit(context, "health");
        }
        Assert.Empty(guard.Recent());
        guard.Exposure = new() { InternetReachable = true };
        var home = From("192.168.1.21");
        for (var i = 0; i < GatewayRequestGuard.FreeFailures; i++)
        {
            guard.Admit(home, "credential");
            guard.Failed(home, "credential", "auth.invalid");
        }
        Assert.Equal("auth.throttled", Assert.Throws<GatewayProtocolException>(() => guard.Admit(home, "credential")).Failure.Code);
    }

    [Fact]
    public void Outside_sources_have_a_request_budget_for_routes_anyone_may_call()
    {
        var (guard, clock, _) = NewGuard();
        var outside = From("198.51.100.4");
        for (var i = 0; i < GatewayRequestGuard.UnauthenticatedRequestsPerMinute; i++) guard.Admit(outside, "health");
        Assert.Equal("auth.throttled", Assert.Throws<GatewayProtocolException>(() => guard.Admit(outside, "health")).Failure.Code);
        guard.Admit(From("198.51.100.5"), "health");
        clock.Advance(TimeSpan.FromMinutes(1));
        guard.Admit(outside, "health");
    }

    [Fact]
    public void Pairing_from_outside_home_is_refused_unless_the_owner_allows_it()
    {
        var (guard, _, log) = NewGuard();
        var outside = From("203.0.113.30");
        Assert.Equal("pair.outside_home", Assert.Throws<GatewayProtocolException>(() => guard.Admit(outside, "pair")).Failure.Code);
        Assert.Contains(guard.Recent(), e => e is { Outcome: "refused", RouteClass: "pair" });
        Assert.Contains(log, line => line.Contains("Refused a pairing attempt from 203.0.113.30"));
        guard.Exposure = new() { AllowPairingOutsideHome = true };
        guard.Admit(outside, "pair");
        // Joining (a member desktop's signed key) is not a guessable pairing code and works from anywhere.
        guard.Exposure = new();
        guard.Admit(From("203.0.113.31"), "join");
    }

    [Fact]
    public void Sign_in_routes_lock_out_the_subject_across_addresses_and_record_their_own_outcomes()
    {
        var (guard, clock, log) = NewGuard();
        for (var i = 0; i < GatewayRequestGuard.FreeFailures; i++)
        {
            var request = new GatewayGuardRequest("signin", $"203.0.113.{i + 1}", "local:owner");
            Assert.True(guard.TryAdmit(request, out _));
            guard.Record(request, GatewayGuardOutcome.Failure, "signin.invalid", "local:owner");
        }
        // A sixth address guessing the same account is refused, and so is the home network.
        Assert.False(guard.TryAdmit(new("signin", "203.0.113.99", "local:owner"), out var wait));
        Assert.Equal(TimeSpan.FromSeconds(1), wait);
        Assert.False(guard.TryAdmit(new("signin", "192.168.1.5", "local:owner"), out _));
        Assert.True(guard.TryAdmit(new("signin", "203.0.113.99", "local:someone-else"), out _));
        clock.Advance(TimeSpan.FromSeconds(1));
        var good = new GatewayGuardRequest("signin", "192.168.1.5", "local:owner");
        Assert.True(guard.TryAdmit(good, out _));
        guard.Record(good, GatewayGuardOutcome.Success, "signin.ok", null);
        Assert.True(guard.TryAdmit(new("signin", "203.0.113.99", "local:owner"), out _));
        Assert.Contains(log, line => line == "INFO Sign-in succeeded for local:owner from 192.168.1.5 (home network).");
        Assert.Contains(guard.Recent(), e => e is { Outcome: "success", Subject: "local:owner", SourceKind: "home" });
        // The dispatcher never double-counts sign-in failures.
        var context = From("203.0.113.50");
        guard.Failed(context, "signin", "auth.invalid");
        Assert.DoesNotContain(guard.Recent(), e => e.Source == "203.0.113.50");
    }

    [Fact]
    public async Task A_host_listed_with_outside_addresses_is_internet_reachable_and_advertises_addresses_set_on_it()
    {
        await using var host = await GatewayTestHost.StartAsync();
        Assert.False(host.Server.Guard.InternetReachable);
        using var founder = Martlet.Core.Network.NetworkKey.Create("desktop-a");
        var now = host.Clock.GetUtcNow();
        var roster = Martlet.Core.Network.NetworkRoster.Found(founder, "A", now)
            .AddHost(founder, host.Identity.HostId, "Fixture", host.Origin.CanonicalOrigin, host.Identity.SpkiFingerprint, now)
            .SetHostAddresses(founder, host.Identity.HostId, ["home.example.net:9443"], now.AddSeconds(1));
        host.Server.AttachNetworkStorage(new RosterStorage(roster.Write()));
        Assert.True(host.Server.Guard.InternetReachable);

        var setAt = now.AddMinutes(1);
        host.Server.Exposure = new() { OutsideAddresses = ["gpu-box.tailnet.ts.net:9443"], OutsideAddressesSetAt = setAt };
        var credential = await host.PairAsync(GatewayRole.Voice, "desktop-a");
        using var signer = new GatewayRequestSigner(host.Identity, credential, host.Clock);
        using var response = await host.Client.SendAsync(host.SignedGet("/martlet/v1/network", GatewayRole.Voice, signer));
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.Equal("gpu-box.tailnet.ts.net:9443", document.RootElement.GetProperty("advertised_addresses")[0].GetString());
        Assert.Equal(setAt, document.RootElement.GetProperty("advertised_at").GetDateTimeOffset());
        Assert.Equal("home.example.net:9443", document.RootElement.GetProperty("roster").GetProperty("members")
            .EnumerateArray().Single(m => m.GetProperty("kind").GetString() == "host").GetProperty("addresses")[0].GetString());

        using var audit = await host.Client.SendAsync(host.SignedGet(GatewayHttpApplication.SecurityAuditPath, GatewayRole.Voice, signer));
        using var auditDocument = JsonDocument.Parse(await audit.Content.ReadAsStringAsync());
        Assert.True(auditDocument.RootElement.GetProperty("internet_reachable").GetBoolean());
        Assert.Equal("home.example.net:9443", auditDocument.RootElement.GetProperty("outside_addresses")[0].GetString());
    }

    private sealed class RosterStorage(byte[] bytes) : IGatewayNetworkStorage
    {
        private byte[]? saved = bytes;
        public byte[]? Load() => saved;
        public void Save(byte[] value) => saved = value;
    }

    [Fact]
    public async Task Gateway_refuses_outside_pairing_locks_out_guessing_and_serves_the_audit_to_paired_desktops()
    {
        await using var host = await GatewayTestHost.StartAsync();
        var credential = await host.PairAsync(GatewayRole.Voice, "desktop-a");
        using var signer = new GatewayRequestSigner(host.Identity, credential, host.Clock);
        // The test connects over loopback; the host is told every connection comes from outside (like a hidden proxy).
        host.Server.Exposure = new() { TreatAllAsOutside = true };

        var card = host.OpenPairing(deviceId: "desktop-b");
        using (var refused = await host.SendPairingAsync(card, card.SpkiFingerprint, deviceId: "desktop-b"))
        {
            Assert.Equal(HttpStatusCode.Forbidden, refused.StatusCode);
            Assert.Equal("pair.outside_home", await GatewayTestHost.FailureCode(refused));
        }
        Assert.True(host.Server.Pairing.IsOpen(card.PairingId));

        for (var i = 0; i < GatewayRequestGuard.FreeFailures; i++)
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, host.Origin.CanonicalOrigin + "/martlet/v1/status");
            using var response = await host.Client.SendAsync(request);
            Assert.Equal("auth.missing", await GatewayTestHost.FailureCode(response));
        }
        using (var request = new HttpRequestMessage(HttpMethod.Get, host.Origin.CanonicalOrigin + "/martlet/v1/status"))
        using (var locked = await host.Client.SendAsync(request))
        {
            Assert.Equal((HttpStatusCode)429, locked.StatusCode);
            Assert.Equal("auth.throttled", await GatewayTestHost.FailureCode(locked));
            Assert.True(locked.Headers.RetryAfter?.Delta >= TimeSpan.FromSeconds(1));
        }
        host.Clock.Advance(TimeSpan.FromSeconds(2));

        host.Server.Exposure = new() { TreatAllAsOutside = true, AllowPairingOutsideHome = true };
        using (var paired = await host.SendPairingAsync(card, card.SpkiFingerprint, deviceId: "desktop-b"))
            Assert.Equal(HttpStatusCode.Created, paired.StatusCode);

        using var audit = await host.Client.SendAsync(host.SignedGet(GatewayHttpApplication.SecurityAuditPath, GatewayRole.Voice, signer));
        Assert.Equal(HttpStatusCode.OK, audit.StatusCode);
        using var document = JsonDocument.Parse(await audit.Content.ReadAsStringAsync());
        var root = document.RootElement;
        Assert.True(root.GetProperty("allow_pairing_outside_home").GetBoolean());
        var events = root.GetProperty("events").EnumerateArray().ToArray();
        Assert.Contains(events, e => e.GetProperty("outcome").GetString() == "refused" && e.GetProperty("code").GetString() == "pair.outside_home");
        Assert.Contains(events, e => e.GetProperty("outcome").GetString() == "throttled");
        Assert.Contains(events, e => e.GetProperty("outcome").GetString() == "success" && e.GetProperty("subject").GetString() == "desktop-b" &&
            e.GetProperty("source").GetString() == "127.0.0.1" && e.GetProperty("source_kind").GetString() == "outside");
        Assert.True(root.GetProperty("failures").GetInt64() >= GatewayRequestGuard.FreeFailures);

        using var anonymous = new HttpRequestMessage(HttpMethod.Get, host.Origin.CanonicalOrigin + GatewayHttpApplication.SecurityAuditPath);
        using var refusedAudit = await host.Client.SendAsync(anonymous);
        Assert.Equal(HttpStatusCode.Unauthorized, refusedAudit.StatusCode);
    }
}
