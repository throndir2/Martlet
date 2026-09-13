using System.Reflection;
using System.Text.Json;

namespace Martlet.Participation.Tests;

public sealed class ValidationAndPrivacyTests
{
    [Theory]
    [InlineData("")]
    [InlineData(" Martlet")]
    [InlineData("Martlet ")]
    [InlineData("Little  Bird")]
    [InlineData("A B C D")]
    [InlineData("Bird,")]
    [InlineData("\"Bird\"")]
    [InlineData("\u200bBird")]
    [InlineData("A123")]
    public void Names_reject_ambiguous_or_unbounded_configuration(string name)
    {
        Assert.Equal(PolicyValidationCode.InvalidConfiguration,
            Assert.Throws<PolicyValidationException>(() => new ParticipationConfiguration(name)).Code);
    }

    [Fact]
    public void Names_and_aliases_are_bounded_validated_and_defensively_copied()
    {
        var aliases = new List<string> { "Bird" };
        var config = new ParticipationConfiguration(aliases: aliases);
        aliases[0] = "Changed";
        Assert.Equal("Bird", Assert.Single(config.Aliases));
        Assert.Throws<NotSupportedException>(() => ((IList<string>)config.Aliases).Add("Changed"));
        Assert.Throws<PolicyValidationException>(() => new ParticipationConfiguration(new string('a', 33)));
        Assert.Throws<PolicyValidationException>(() => new ParticipationConfiguration(aliases: ["martlet"]));
        Assert.Throws<PolicyValidationException>(() => new ParticipationConfiguration("\u00c9lan", ["E\u0301lan"]));
        Assert.Throws<PolicyValidationException>(() => new ParticipationConfiguration(aliases: TooManyAliases()));
        Assert.Throws<PolicyValidationException>(() => new ParticipationConfiguration("\ud800"));

        static IEnumerable<string> TooManyAliases()
        {
            while (true) yield return "Bird";
        }
    }

    [Fact]
    public void Invalid_config_has_authored_error_instead_of_silent_defaults()
    {
        Action[] invalid =
        [
            () => new ParticipationConfiguration(mode: (ParticipationMode)99),
            () => new ParticipationConfiguration(language: (PolicyLanguage)99),
            () => new ParticipationConfiguration(addressedGap: TimeSpan.Zero),
            () => new ParticipationConfiguration(addressedGap: TimeSpan.FromSeconds(4)),
            () => new ParticipationConfiguration(unsolicitedGap: TimeSpan.FromMilliseconds(1199)),
            () => new ParticipationConfiguration(automaticCooldown: TimeSpan.Zero),
            () => new ParticipationConfiguration(automaticCooldown: TimeSpan.FromSeconds(61)),
            () => new ParticipationConfiguration(intentLifetime: TimeSpan.FromSeconds(6)),
            () => new ParticipationConfiguration(intentLifetime: TimeSpan.FromSeconds(1)),
            () => new ParticipationConfiguration(unsolicitedTurnsPerMinute: -1),
            () => new ParticipationConfiguration(unsolicitedTurnsPerMinute: 3),
            () => new ParticipationConfiguration(minimumConfidence: double.NaN),
            () => new ParticipationConfiguration(minimumConfidence: double.PositiveInfinity),
            () => new ParticipationConfiguration(minimumConfidence: -0.01),
            () => new ParticipationConfiguration(minimumConfidence: 1.01)
        ];
        foreach (var action in invalid)
            Assert.Equal(PolicyValidationCode.InvalidConfiguration, Assert.Throws<PolicyValidationException>(action).Code);
    }

    [Fact]
    public void Invalid_inputs_do_not_echo_raw_content_in_errors()
    {
        Action[] invalid =
        [
            () => new Transcript(null!),
            () => new Transcript(new string('x', 4097)),
            () => new Transcript("\ud800"),
            () => new Transcript("secret-canary", confidence: double.NaN),
            () => new Transcript("secret-canary", confidence: double.PositiveInfinity),
            () => new Transcript("secret-canary", confidence: -0.1),
            () => new Transcript("secret-canary", confidence: 1.1),
            () => new Transcript("secret-canary", evidence: (SpeechEvidence)99),
            () => new ParticipationInput((InputSource)99, new("secret-canary")),
            () => new ParticipationInput(InputSource.AmbientSpeech, new("secret-canary"), (AudioOrigin)99),
            () => new ParticipationInput(InputSource.AmbientSpeech, new("secret-canary"), trustedTypedAddress: true),
            () => new ParticipationInput(InputSource.TypedControl, new("secret-canary"), AudioOrigin.External),
            () => new ParticipationInput(InputSource.TypedControl, new("secret-canary", isFinal: false)),
            () => new ParticipationInput(InputSource.PushToTalkControl, null!)
        ];
        foreach (var action in invalid)
        {
            var error = Assert.Throws<PolicyValidationException>(action);
            Assert.Equal(PolicyValidationCode.InvalidInput, error.Code);
            Assert.DoesNotContain("secret-canary", error.ToString(), StringComparison.Ordinal);
        }
    }

    [Fact]
    public void State_and_cross_session_handles_are_validated()
    {
        var policy = PolicyFixtures.Policy(new());
        var other = PolicyFixtures.Policy(new());
        var intent = other.CreateIntent(PolicyFixtures.Ptt());
        Assert.Equal(PolicyValidationCode.InvalidHandle,
            Assert.Throws<PolicyValidationException>(() => policy.Evaluate(intent)).Code);
        Assert.Equal(PolicyValidationCode.InvalidHandle,
            Assert.Throws<PolicyValidationException>(() => policy.TryCommit(other.Evaluate(intent))).Code);
        var lease = other.Accept(PolicyFixtures.Ptt());
        Assert.Equal(PolicyValidationCode.InvalidHandle,
            Assert.Throws<PolicyValidationException>(() => policy.Release(lease)).Code);
        Assert.Equal(PolicyValidationCode.InvalidState,
            Assert.Throws<PolicyValidationException>(() => policy.SetState(new() { Activity = (ResponseActivity)99 })).Code);
        Assert.Throws<PolicyValidationException>(() => policy.SetState(new() { TextDestinationAuthorized = true }));
        policy.SetState(PolicyFixtures.Consented with { AuthorizationRevision = 2 });
        Assert.Equal(PolicyValidationCode.AuthorizationRevisionReversed,
            Assert.Throws<PolicyValidationException>(() => policy.SetState(PolicyFixtures.Consented)).Code);
    }

    [Fact]
    public void Content_is_deliberate_not_in_default_serialization_or_summaries()
    {
        const string canary = "private-transcript-credential-like-canary";
        var clock = new ManualClock();
        var policy = PolicyFixtures.Policy(clock);
        var transcript = new Transcript(canary);
        var input = new ParticipationInput(InputSource.PushToTalkControl, transcript);
        var intent = policy.CreateIntent(input);
        var decision = policy.Evaluate(intent);
        var commit = policy.TryCommit(decision);
        object[] metadata = [transcript, input, intent, decision, commit, commit.Lease!, policy.Snapshot,
            policy.Configuration, policy.SetState(PolicyFixtures.Consented)];
        foreach (var item in metadata)
        {
            Assert.DoesNotContain(canary, item.ToString()!, StringComparison.Ordinal);
            Assert.DoesNotContain(canary, JsonSerializer.Serialize(item, item.GetType()), StringComparison.Ordinal);
        }
        Assert.Equal(canary, transcript.Text);
        var named = new ParticipationConfiguration("PrivateAliasCanary");
        Assert.DoesNotContain("PrivateAliasCanary", named.ToString(), StringComparison.Ordinal);
        Assert.DoesNotContain("PrivateAliasCanary", JsonSerializer.Serialize(named), StringComparison.Ordinal);
    }

    [Fact]
    public void Production_has_only_BCL_dependencies_and_no_permission_or_execution_injection_points()
    {
        var assembly = typeof(ParticipationPolicy).Assembly;
        var referenced = assembly.GetReferencedAssemblies();
        Assert.All(referenced, reference => Assert.StartsWith("System.", reference.Name, StringComparison.Ordinal));
        Assert.DoesNotContain(referenced, reference => reference.Name!.StartsWith("System.Net", StringComparison.Ordinal));
        Assert.DoesNotContain(referenced, reference => reference.Name!.StartsWith("System.IO", StringComparison.Ordinal));
        var apiTypes = assembly.GetExportedTypes();
        Assert.DoesNotContain(apiTypes, type => typeof(Delegate).IsAssignableFrom(type) || type.IsInterface);
        Assert.Empty(typeof(ParticipationDecision).GetConstructors());
        Assert.Empty(typeof(ParticipationIntent).GetConstructors());
        Assert.Empty(typeof(DispatchLease).GetConstructors());
    }

    [Theory]
    [InlineData("intentSequence")]
    [InlineData("epoch")]
    [InlineData("dispatchRevision")]
    public void Sequence_exhaustion_fails_closed_without_reusing_identifiers(string fieldName)
    {
        var policy = PolicyFixtures.Policy(new());
        var permit = policy.Decision(PolicyFixtures.Ptt());
        typeof(ParticipationPolicy).GetField(fieldName, BindingFlags.Instance | BindingFlags.NonPublic)!
            .SetValue(policy, long.MaxValue);
        Action operation = fieldName switch
        {
            "intentSequence" => () => policy.CreateIntent(PolicyFixtures.Ptt()),
            "epoch" => () => policy.Reconfigure(new()),
            _ => () => policy.TryCommit(policy.Decision(PolicyFixtures.Ptt()))
        };
        Assert.Equal(PolicyValidationCode.SequenceExhausted,
            Assert.Throws<PolicyValidationException>(operation).Code);
        Assert.Null(policy.Snapshot.ActiveIntentId);
        Assert.Equal(0, policy.Snapshot.RecentUnsolicitedDispatches);
        Assert.Equal(0, policy.Snapshot.AcceptedThroughIntentId);
        Assert.NotNull(permit);
    }
}
