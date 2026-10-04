using System.Buffers.Binary;
using System.Net;
using System.Security.Cryptography;
using System.Text.Json;
using Martlet.Gateway;

namespace Martlet.Gateway.Tests;

public sealed class PairingAuthenticationTests
{
    [Fact]
    public void Legacy_pairing_version_does_not_silently_issue_permanent_trust()
    {
        var identity = new GatewayHostIdentity { HostId = "host", SpkiFingerprint = "sha256:" + new string('0', 64) };
        var store = new GatewayCredentialStore(identity);
        var pairing = new GatewayPairingService(identity, new("https://127.0.0.1:9443"), store);
        var card = pairing.OpenWindow(new() { DeviceId = "device", DisplayName = "Device", Roles = [GatewayRole.Voice] });
        var proof = new GatewayPairingProof
        {
            ProtocolVersion = new() { Major = 1, Minor = 0 }, PairingId = card.PairingId,
            PairingToken = card.Token.Reveal(), HostId = card.HostId, SpkiFingerprint = card.SpkiFingerprint,
            DeviceId = "device"
        };
        Assert.Equal("protocol.unsupported", Assert.Throws<GatewayProtocolException>(() => pairing.Exchange(proof)).Failure.Code);
        Assert.Empty(store.ListRegistrations());
        Assert.IsType<PairedDeviceLifetime>(pairing.Exchange(proof with { ProtocolVersion = GatewayProtocolVersion.Current }).Lifetime);
    }

    [Fact]
    public void Short_code_has_no_deadline_and_closes_only_when_used_or_mistyped()
    {
        var identity = new GatewayHostIdentity { HostId = "host", SpkiFingerprint = "sha256:" + new string('0', 64) };
        var clock = new ManualGatewayClock(new DateTimeOffset(2026, 10, 4, 12, 0, 0, TimeSpan.Zero));
        var store = new GatewayCredentialStore(identity, clock);
        var pairing = new GatewayPairingService(identity, new("https://127.0.0.1:9443"), store, clock);
        var card = pairing.OpenCodeWindow(new() { Roles = [GatewayRole.Voice] });
        // Card windows end after five minutes; a typed code still works days later.
        var invitation = pairing.OpenWindow(new() { DeviceId = "device", DisplayName = "Device", Roles = [GatewayRole.Voice] });
        clock.Advance(TimeSpan.FromDays(3));
        Assert.False(pairing.IsOpen(invitation.PairingId));
        Assert.True(pairing.IsOpen(card.PairingId));

        var (credential, hostProof) = pairing.Exchange(CodeProof(card.Code.Reveal(), identity.SpkiFingerprint, "fixture-desktop"));
        Assert.Equal("fixture-desktop", credential.DeviceId);
        Assert.False(string.IsNullOrEmpty(hostProof));
        Assert.False(pairing.IsOpen(card.PairingId));
        Assert.Equal("pairing.closed", Assert.Throws<GatewayProtocolException>(() =>
            pairing.Exchange(CodeProof(card.Code.Reveal(), identity.SpkiFingerprint, "other-desktop"))).Failure.Code);

        var mistyped = pairing.OpenCodeWindow(new() { Roles = [GatewayRole.Voice] });
        var code = mistyped.Code.Reveal();
        var wrong = (code[0] == '2' ? "3" : "2") + code[1..];
        for (var attempt = 0; attempt < GatewayPairingService.MaximumFailedAttempts; attempt++)
        {
            Assert.True(pairing.IsOpen(mistyped.PairingId));
            Assert.Equal("pairing.invalid", Assert.Throws<GatewayProtocolException>(() =>
                pairing.Exchange(CodeProof(wrong, identity.SpkiFingerprint, "guessing-desktop"))).Failure.Code);
        }
        Assert.False(pairing.IsOpen(mistyped.PairingId));
        Assert.Equal("pairing.closed", Assert.Throws<GatewayProtocolException>(() =>
            pairing.Exchange(CodeProof(code, identity.SpkiFingerprint, "fixture-desktop"))).Failure.Code);
        Assert.Single(store.ListRegistrations());
    }

    private static GatewayCodePairingProof CodeProof(string code, string spkiFingerprint, string deviceId)
    {
        Assert.True(GatewayPairingCode.TryNormalize(code, out var canonical));
        var nonce = RandomNumberGenerator.GetBytes(16);
        var key = GatewayPairingCode.DeriveKey(canonical, spkiFingerprint, nonce);
        return new()
        {
            ProtocolVersion = GatewayProtocolVersion.Current, DeviceId = deviceId, DisplayName = "Fixture PC",
            ClientNonce = System.Buffers.Text.Base64Url.EncodeToString(nonce),
            Proof = System.Buffers.Text.Base64Url.EncodeToString(GatewayPairingCode.ClientProof(key, deviceId, "Fixture PC", nonce))
        };
    }

    [Theory]
    [InlineData("{}")]
    [InlineData("{\"expires_at\":\"2099-01-01T00:00:00Z\"}")]
    [InlineData("{\"kind\":\"paired\",\"expires_at\":\"2099-01-01T00:00:00Z\"}")]
    [InlineData("{\"kind\":\"timed\"}")]
    public void Lifetime_requires_explicit_recognized_discriminator_without_expiry_sentinel(string json)
    {
        Assert.ThrowsAny<Exception>(() => JsonSerializer.Deserialize<GatewayCredentialLifetime>(json, GatewayTestHost.Json));
    }
    [Fact]
    public async Task Unpaired_client_sees_only_minimal_liveness_and_bounded_auth_error()
    {
        await using var host = await GatewayTestHost.StartAsync();
        using var live = await host.Client.SendAsync(new(
            HttpMethod.Get, host.Origin.CanonicalOrigin + "/health/live"));
        Assert.Equal("""{"status":"live"}""",
            await live.Content.ReadAsStringAsync());

        using var denied = await host.Client.SendAsync(new(
            HttpMethod.Get, host.Origin.CanonicalOrigin + "/martlet/v1/capabilities"));
        Assert.Equal(HttpStatusCode.Unauthorized, denied.StatusCode);
        var body = await denied.Content.ReadAsStringAsync();
        Assert.Contains("\"code\":\"auth.missing\"", body, StringComparison.Ordinal);
        Assert.DoesNotContain(host.Identity.SpkiFingerprint, body, StringComparison.Ordinal);
        Assert.DoesNotContain("credential_secret", body, StringComparison.Ordinal);
        using var document = JsonDocument.Parse(body);
        var traceId = document.RootElement.GetProperty("trace_id").GetGuid();
        var audit = Assert.Single(host.Audit.Events);
        Assert.Equal(traceId, audit.TraceId);
        Assert.Equal("auth.missing", audit.Code);
        Assert.Equal((int)HttpStatusCode.Unauthorized, audit.HttpStatus);
    }

    [Fact]
    public async Task Pairing_is_explicit_pinned_one_use_and_returns_a_redacted_scoped_credential()
    {
        await using var host = await GatewayTestHost.StartAsync();
        var card = host.OpenPairing();
        Assert.Equal(TimeSpan.FromMinutes(5), card.ExpiresAt - host.Clock.GetUtcNow());
        Assert.Equal(nameof(GatewaySecret), card.Token.ToString());

        using var paired = await host.SendPairingAsync(card, card.SpkiFingerprint);
        Assert.Equal(HttpStatusCode.Created, paired.StatusCode);
        var body = await paired.Content.ReadAsStringAsync();
        Assert.Contains("\"roles\":[\"voice\"]", body, StringComparison.Ordinal);
        Assert.DoesNotContain(card.Token.Reveal(), body, StringComparison.Ordinal);

        using var replay = await host.SendPairingAsync(card, card.SpkiFingerprint);
        Assert.Equal(HttpStatusCode.Unauthorized, replay.StatusCode);
        Assert.Equal("pairing.closed", await GatewayTestHost.FailureCode(replay));
    }

    [Fact]
    public async Task Pairing_rejects_bad_pin_and_expired_window_without_echoing_proofs()
    {
        await using var host = await GatewayTestHost.StartAsync();
        var badPinCard = host.OpenPairing();
        var badPin = "sha256:" + new string('0', 64);
        using var mismatch = await host.SendPairingAsync(badPinCard, badPin);
        var mismatchBody = await mismatch.Content.ReadAsStringAsync();
        Assert.Equal(HttpStatusCode.Unauthorized, mismatch.StatusCode);
        Assert.Contains("\"code\":\"host.pin_mismatch\"", mismatchBody, StringComparison.Ordinal);
        Assert.DoesNotContain(badPinCard.Token.Reveal(), mismatchBody, StringComparison.Ordinal);

        var expiredCard = host.OpenPairing();
        host.Clock.Advance(TimeSpan.FromMinutes(5) + TimeSpan.FromSeconds(1));
        using var expired = await host.SendPairingAsync(expiredCard, expiredCard.SpkiFingerprint);
        Assert.Equal("pairing.expired", await GatewayTestHost.FailureCode(expired));
    }

    [Fact]
    public async Task Pairing_window_and_failed_proof_limits_fail_closed()
    {
        await using var windowHost = await GatewayTestHost.StartAsync();
        for (var index = 0; index < GatewayPairingService.MaximumOpenWindows; index++)
            _ = windowHost.OpenPairing(deviceId: $"fixture-device-{index}");
        var excess = Assert.Throws<GatewayProtocolException>(() =>
            windowHost.OpenPairing(deviceId: "fixture-device-excess"));
        Assert.Equal("pairing.closed", excess.Failure.Code);

        await using var proofHost = await GatewayTestHost.StartAsync();
        var target = proofHost.OpenPairing();
        var wrongToken = proofHost.OpenPairing(deviceId: "decoy-device").Token.Reveal();
        for (var attempt = 0; attempt < GatewayPairingService.MaximumFailedAttempts; attempt++)
        {
            using var rejected = await proofHost.SendPairingAsync(
                target, target.SpkiFingerprint, wrongToken);
            Assert.Equal("pairing.invalid", await GatewayTestHost.FailureCode(rejected));
        }
        using var closed = await proofHost.SendPairingAsync(target, target.SpkiFingerprint);
        Assert.Equal("pairing.closed", await GatewayTestHost.FailureCode(closed));
    }

    [Fact]
    public async Task Signed_request_is_bound_to_path_role_time_and_one_nonce()
    {
        await using var host = await GatewayTestHost.StartAsync();
        var credential = await host.PairAsync();
        var signer = new GatewayRequestSigner(host.Identity, credential, host.Clock);
        using var request = host.SignedGet("/martlet/v1/version", GatewayRole.Voice, signer);
        using var replay = GatewayTestHost.Clone(request);
        using var tampered = GatewayTestHost.Clone(request);
        tampered.RequestUri = new(host.Origin.CanonicalOrigin + "/martlet/v1/status");

        using var accepted = await host.Client.SendAsync(request);
        Assert.Equal(HttpStatusCode.OK, accepted.StatusCode);
        using var replayed = await host.Client.SendAsync(replay);
        Assert.Equal(HttpStatusCode.Conflict, replayed.StatusCode);
        Assert.Equal("auth.replay", await GatewayTestHost.FailureCode(replayed));
        using var changedPath = await host.Client.SendAsync(tampered);
        Assert.Equal(HttpStatusCode.Unauthorized, changedPath.StatusCode);
        Assert.Equal("auth.invalid", await GatewayTestHost.FailureCode(changedPath));
    }

    [Fact]
    public async Task Wrong_role_and_stale_timestamp_fail_before_worker_metadata_is_exposed()
    {
        await using var host = await GatewayTestHost.StartAsync();
        var credential = await host.PairAsync(GatewayRole.Voice);
        var signer = new GatewayRequestSigner(host.Identity, credential, host.Clock);
        using var wrongRoleRequest = host.SignedGet(
            "/martlet/v1/capabilities", GatewayRole.Memory, signer);
        using var wrongRole = await host.Client.SendAsync(wrongRoleRequest);
        Assert.Equal(HttpStatusCode.Forbidden, wrongRole.StatusCode);
        Assert.Equal("auth.role", await GatewayTestHost.FailureCode(wrongRole));

        using var staleRequest = host.SignedGet(
            "/martlet/v1/capabilities", GatewayRole.Voice, signer);
        host.Clock.Advance(TimeSpan.FromMinutes(2) + TimeSpan.FromSeconds(1));
        using var stale = await host.Client.SendAsync(staleRequest);
        Assert.Equal(HttpStatusCode.Unauthorized, stale.StatusCode);
        Assert.Equal("auth.clock", await GatewayTestHost.FailureCode(stale));
    }

    [Fact]
    public async Task Credential_expiry_rotation_overlap_and_revocation_are_enforced()
    {
        await using var host = await GatewayTestHost.StartAsync();
        var original = await host.PairAsync();
        var replacement = host.Server.Credentials.Rotate(
            original.CredentialId, TimeSpan.FromMinutes(1));
        Assert.Equal(nameof(GatewaySecret), replacement.Secret.ToString());

        var originalSigner = new GatewayRequestSigner(host.Identity, original, host.Clock);
        var replacementSigner = new GatewayRequestSigner(host.Identity, replacement, host.Clock);
        using (var oldRequest = host.SignedGet(
            "/martlet/v1/version", GatewayRole.Voice, originalSigner))
        using (var oldAccepted = await host.Client.SendAsync(oldRequest))
            Assert.Equal(HttpStatusCode.OK, oldAccepted.StatusCode);
        using (var newRequest = host.SignedGet(
            "/martlet/v1/version", GatewayRole.Voice, replacementSigner))
        using (var newAccepted = await host.Client.SendAsync(newRequest))
            Assert.Equal(HttpStatusCode.OK, newAccepted.StatusCode);

        host.Clock.Advance(TimeSpan.FromMinutes(1) + TimeSpan.FromSeconds(1));
        using (var expiredRequest = host.SignedGet(
            "/martlet/v1/version", GatewayRole.Voice, originalSigner))
        using (var expired = await host.Client.SendAsync(expiredRequest))
            Assert.Equal("auth.expired", await GatewayTestHost.FailureCode(expired));

        Assert.True(host.Server.Credentials.RevokeCredential(replacement.CredentialId));
        using var revokedRequest = host.SignedGet(
            "/martlet/v1/version", GatewayRole.Voice, replacementSigner);
        using var revoked = await host.Client.SendAsync(revokedRequest);
        Assert.Equal("auth.revoked", await GatewayTestHost.FailureCode(revoked));
        Assert.All(host.Server.Credentials.ListRegistrations(),
            registration => Assert.DoesNotContain("GatewaySecret", registration.ToString()));
    }

    [Fact]
    public async Task Paired_credential_has_explicit_nonexpiring_lifetime()
    {
        await using var host = await GatewayTestHost.StartAsync();
        var credential = await host.PairAsync();
        host.Clock.Advance(TimeSpan.FromDays(180));
        var signer = new GatewayRequestSigner(host.Identity, credential, host.Clock);
        using var request = host.SignedGet(
            "/martlet/v1/version", GatewayRole.Voice, signer);
        using var response = await host.Client.SendAsync(request);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.IsType<PairedDeviceLifetime>(credential.Lifetime);
        var text = await response.Content.ReadAsStringAsync();
        Assert.Contains("\"credential_lifetime\":{\"kind\":\"paired\"}", text);
        Assert.DoesNotContain("expires_at", text);
    }

    [Fact]
    public void Canonical_base64url_credential_ids_remain_valid_when_they_start_with_punctuation()
    {
        var identity = new GatewayHostIdentity
        {
            HostId = "fixture-host",
            SpkiFingerprint = "sha256:" + new string('0', 64)
        };
        var clock = new ManualGatewayClock(
            new DateTimeOffset(2026, 9, 21, 20, 0, 0, TimeSpan.Zero));
        var crypto = new PunctuationPrefixCrypto();
        var store = new GatewayCredentialStore(identity, clock, crypto);
        var credential = store.Issue(
            "fixture-device", "Fixture device", [GatewayRole.Voice]);
        Assert.StartsWith("-", credential.CredentialId, StringComparison.Ordinal);
        _ = new GatewayRequestSigner(identity, credential, clock, crypto);
        var replacement = store.Rotate(credential.CredentialId, TimeSpan.Zero);
        Assert.StartsWith("-", replacement.CredentialId, StringComparison.Ordinal);
        Assert.True(store.RevokeCredential(replacement.CredentialId));
    }

    [Fact]
    public void Replay_cache_saturation_has_a_rate_remedy_without_evicting_live_nonces()
    {
        var identity = new GatewayHostIdentity
        {
            HostId = "fixture-host",
            SpkiFingerprint = "sha256:" + new string('0', 64)
        };
        var clock = new ManualGatewayClock(
            new DateTimeOffset(2026, 9, 21, 20, 0, 0, TimeSpan.Zero));
        var crypto = new SystemGatewayCrypto();
        var store = new GatewayCredentialStore(identity, clock, crypto);
        var credential = store.Issue(
            "fixture-device", "Fixture device", [GatewayRole.Voice]);
        Assert.True(Base64Url.TryDecode(credential.Secret.Reveal(), 32, out var secret));
        var verifier = crypto.Sha256(secret);
        CryptographicOperations.ZeroMemory(secret);
        var timestamp = clock.GetUtcNow().ToUnixTimeSeconds();

        for (var index = 0; index <= GatewayCredentialStore.MaximumNoncesPerCredential; index++)
        {
            var nonceBytes = new byte[24];
            BinaryPrimitives.WriteInt32BigEndian(nonceBytes.AsSpan(20), index);
            var nonce = Base64Url.Encode(nonceBytes);
            var canonical = GatewayRequestSigner.Canonical(
                identity.HostId,
                credential.CredentialId,
                "GET",
                "/martlet/v1/status",
                "voice",
                timestamp,
                nonce,
                SHA256.HashData([]));
            var request = new GatewaySignedRequest
            {
                CredentialId = credential.CredentialId,
                Signature = crypto.HmacSha256(verifier, canonical),
                Nonce = nonce,
                Timestamp = clock.GetUtcNow(),
                Role = GatewayRole.Voice,
                CanonicalBytes = canonical
            };
            if (index < GatewayCredentialStore.MaximumNoncesPerCredential)
                Assert.Equal(credential.CredentialId, store.Authenticate(request).CredentialId);
            else
                Assert.Equal("auth.rate",
                    Assert.Throws<GatewayProtocolException>(() => store.Authenticate(request))
                        .Failure.Code);
        }
    }

    private sealed class PunctuationPrefixCrypto : IGatewayCrypto
    {
        private readonly SystemGatewayCrypto inner = new();
        private byte sequence;

        public byte[] RandomBytes(int count)
        {
            var value = inner.RandomBytes(count);
            value[0] = 0xf8;
            value[1] = sequence++;
            return value;
        }

        public byte[] Sha256(ReadOnlyMemory<byte> value) => inner.Sha256(value);
        public byte[] HmacSha256(ReadOnlyMemory<byte> key, ReadOnlyMemory<byte> value) =>
            inner.HmacSha256(key, value);
        public bool FixedTimeEquals(ReadOnlyMemory<byte> left, ReadOnlyMemory<byte> right) =>
            inner.FixedTimeEquals(left, right);
        public string SpkiFingerprint(System.Security.Cryptography.X509Certificates.X509Certificate2 certificate) =>
            inner.SpkiFingerprint(certificate);
    }
}
