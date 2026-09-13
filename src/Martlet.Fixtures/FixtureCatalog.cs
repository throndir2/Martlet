using Martlet.Core.Contracts;
using Martlet.Core.Streaming;

namespace Martlet.Fixtures;

/// <summary>Authored synthetic, in-memory provider scripts. No inference or external assets.</summary>
public static class FixtureCatalog
{
    public static IReadOnlyList<string> Names { get; } = Array.AsReadOnly(new[]
    {
        "complete", "streaming", "no-speech", "refused", "suppressed", "failed", "canceled",
        "first-deadline", "idle-deadline", "total-deadline", "duplicate", "conflicting-duplicate",
        "out-of-order", "wrong-ids", "wrong-epoch", "late-after-stop", "late-after-replace",
        "truncated", "unsupported", "unknown", "malformed", "text-limit", "event-limit",
        "backpressure", "overflow", "empty", "wrong-provenance", "stt-partial", "refused-after-partial", "version"
    });

    public static FixtureScenario Create(string name)
    {
        ContractRules.Require(Names.Contains(name, StringComparer.Ordinal), "Unknown fixture catalog entry.", ErrorCode.NotImplemented);
        var request = new TextStreamRequest
        {
            Ids = new()
            {
                SessionId = Guid.Parse("00000001-0000-0000-0000-000000000000"),
                TurnId = Guid.Parse("00000002-0000-0000-0000-000000000000"),
                RequestId = Guid.Parse("00000003-0000-0000-0000-000000000000")
            },
            Epoch = 0,
            Capabilities = new()
            {
                Version = ContractVersion.Current, ProviderId = "fixture", AdapterVersion = "f03a",
                ModelId = "synthetic", Role = name is "no-speech" or "stt-partial" ? ProviderRole.Stt : ProviderRole.Llm,
                Provenance = EvidenceProvenance.Fixture,
                SttPartials = name is "no-speech" or "stt-partial" ? CapabilitySupport.Supported : CapabilitySupport.Unsupported,
                LlmTextDeltas = name switch
                {
                    "unknown" => CapabilitySupport.Unknown,
                    "unsupported" or "no-speech" or "stt-partial" => CapabilitySupport.Unsupported,
                    _ => CapabilitySupport.Supported
                },
                TtsAudioTransport = CapabilitySupport.Unsupported, TtsIncrementalSynthesis = CapabilitySupport.Unsupported,
                Cancellation = CancellationCapability.DiscardOnly, MaxInputBytes = 1024
            }
        };
        var limits = new SequenceLimits();
        var steps = new List<FixtureStep>();
        ProviderEvent Event(ProviderEventKind kind, int seq, string? text = null) => new()
        {
            Version = ContractVersion.Current, Ids = request.Ids, ProviderId = "fixture", Epoch = request.Epoch,
            Sequence = seq, Provenance = EvidenceProvenance.Fixture, Kind = kind, Text = text
        };
        void Emit(ProviderEventKind kind, int seq, string? text = null, int at = 0) =>
            steps.Add(new() { AtMilliseconds = at, Action = FixtureAction.Event, Event = Event(kind, seq, text) });
        void Act(FixtureAction action, int at = 0) => steps.Add(new() { AtMilliseconds = at, Action = action });
        if (name == "suppressed")
            steps.Add(new() { AtMilliseconds = 0, Action = FixtureAction.Suppress, Suppression = SuppressionReason.NotAddressed });
        else if (name == "first-deadline")
            Act(FixtureAction.Poll, 15_000);
        else
        {
            Emit(ProviderEventKind.Started, 0);
            switch (name)
            {
                case "complete":
                    Emit(ProviderEventKind.Completed, 1, "A synthetic fixture response.");
                    Act(FixtureAction.Drain);
                    break;
                case "streaming":
                case "stt-partial":
                case "duplicate":
                case "backpressure":
                    limits = limits with { MaxQueuedChunks = 1 };
                    Emit(ProviderEventKind.TextDelta, 1, "Synthetic ");
                    if (name == "duplicate")
                        Emit(ProviderEventKind.TextDelta, 1, "Synthetic ");
                    if (name == "backpressure")
                        Emit(ProviderEventKind.TextDelta, 2, "text.");
                    Act(FixtureAction.Drain);
                    Emit(ProviderEventKind.TextDelta, 2, "text.");
                    Act(FixtureAction.Drain);
                    Emit(ProviderEventKind.Completed, 3);
                    break;
                case "no-speech":
                    Emit(ProviderEventKind.NoSpeech, 1);
                    break;
                case "refused":
                    Emit(ProviderEventKind.Refused, 1, "This is an authored fixture refusal.");
                    break;
                case "refused-after-partial":
                    Emit(ProviderEventKind.TextDelta, 1, "Partial fixture.");
                    Act(FixtureAction.Drain);
                    Emit(ProviderEventKind.Refused, 2, "Authored fixture refusal.");
                    break;
                case "failed":
                    steps.Add(new()
                    {
                        AtMilliseconds = 0, Action = FixtureAction.Event,
                        Event = Event(ProviderEventKind.Failed, 1) with
                        {
                            Error = new()
                            {
                                Code = ErrorCode.ProviderFailed, Stage = Stage.Generation, Retryable = false,
                                Summary = "Synthetic fixture failure.", ActionId = "fixture.failure"
                            }
                        }
                    });
                    break;
                case "canceled":
                    Emit(ProviderEventKind.Canceled, 1);
                    break;
                case "idle-deadline":
                    Act(FixtureAction.Poll, 10_000);
                    break;
                case "total-deadline":
                    for (var i = 1; i <= 6; i++)
                    {
                        Emit(ProviderEventKind.TextDelta, i, "Fixture.", i * 9000);
                        Act(FixtureAction.Drain, i * 9000);
                    }
                    Act(FixtureAction.Poll, 60_000);
                    break;
                case "conflicting-duplicate":
                    Emit(ProviderEventKind.Completed, 0, "Changed fixture.");
                    break;
                case "out-of-order":
                    Emit(ProviderEventKind.Completed, 2, "Fixture.");
                    break;
                case "wrong-ids":
                    steps.Add(new()
                    {
                        AtMilliseconds = 0, Action = FixtureAction.Event,
                        Event = Event(ProviderEventKind.Completed, 1, "Fixture.") with
                        {
                            Ids = request.Ids with { RequestId = Guid.Parse("00000004-0000-0000-0000-000000000000") }
                        }
                    });
                    break;
                case "wrong-epoch":
                    steps.Add(new() { AtMilliseconds = 0, Action = FixtureAction.Event,
                        Event = Event(ProviderEventKind.Completed, 1, "Fixture.") with { Epoch = 1 } });
                    break;
                case "late-after-stop":
                    Emit(ProviderEventKind.TextDelta, 1, "Queued.");
                    Act(FixtureAction.Stop);
                    Emit(ProviderEventKind.TextDelta, 2, "Late.");
                    Act(FixtureAction.Drain);
                    break;
                case "late-after-replace":
                    Emit(ProviderEventKind.TextDelta, 1, "Queued.");
                    var oldEvent = Event(ProviderEventKind.TextDelta, 2, "Late.");
                    request = request with
                    {
                        Epoch = 1, Ids = request.Ids with
                        {
                            TurnId = Guid.Parse("00000004-0000-0000-0000-000000000000"),
                            RequestId = Guid.Parse("00000005-0000-0000-0000-000000000000")
                        }
                    };
                    steps.Add(new() { AtMilliseconds = 0, Action = FixtureAction.Replace, NextRequest = request });
                    steps.Add(new() { AtMilliseconds = 0, Action = FixtureAction.Event, Event = oldEvent });
                    Emit(ProviderEventKind.Started, 0);
                    Emit(ProviderEventKind.Completed, 1, "Current fixture.");
                    Act(FixtureAction.Drain);
                    break;
                case "truncated":
                    Emit(ProviderEventKind.TextDelta, 1, "Partial fixture.");
                    Act(FixtureAction.Drain);
                    break;
                case "unsupported":
                case "unknown":
                    Emit(ProviderEventKind.TextDelta, 1, "Fixture.");
                    break;
                case "malformed":
                    steps.Add(new() { AtMilliseconds = 0, Action = FixtureAction.RawEvent, RawEvent = "{\"text\":\"\\uD800\"}" });
                    break;
                case "text-limit":
                    limits = limits with { MaxTextCharacters = 2 };
                    Emit(ProviderEventKind.TextDelta, 1, "Long.");
                    break;
                case "event-limit":
                    limits = limits with { MaxIngressEvents = 2 };
                    Emit(ProviderEventKind.Started, 0);
                    Emit(ProviderEventKind.Started, 0);
                    break;
                case "overflow":
                    limits = limits with { MaxQueuedChunks = 1 };
                    Emit(ProviderEventKind.TextDelta, 1, "Queued.");
                    steps.Add(new() { AtMilliseconds = 0, Action = FixtureAction.Event, Overflow = TextOverflowPolicy.Fail,
                        Event = Event(ProviderEventKind.TextDelta, 2, "Overflow.") });
                    break;
                case "empty":
                    Emit(ProviderEventKind.Completed, 1, "");
                    break;
                case "wrong-provenance":
                    var live = Event(ProviderEventKind.Completed, 1, "Deliberate provenance fault.") with { Provenance = EvidenceProvenance.Live };
                    steps.Add(new() { AtMilliseconds = 0, Action = FixtureAction.RawEvent,
                        RawEvent = System.Text.Encoding.UTF8.GetString(ContractJson.Write(live)) });
                    break;
                case "version":
                    var json = System.Text.Encoding.UTF8.GetString(ContractJson.Write(Event(ProviderEventKind.Completed, 1, "Fixture.")));
                    steps.Add(new() { AtMilliseconds = 0, Action = FixtureAction.RawEvent,
                        RawEvent = json.Replace("\"major\": 1", "\"major\": 2", StringComparison.Ordinal) });
                    break;
            }
        }
        // Replacement changes the producer's route; the script still starts at its original request.
        var initial = name == "late-after-replace" ? request with { Epoch = 0, Ids = steps[0].Event!.Ids } : request;
        return new()
        {
            Version = ContractVersion.Current, Name = name, Label = FixtureScenario.EvidenceLabel,
            Provenance = EvidenceProvenance.Fixture, Request = initial, Limits = limits, Steps = steps.AsReadOnly()
        };
    }
}
