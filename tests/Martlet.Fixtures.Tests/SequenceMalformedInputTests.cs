using System.Text;
using Martlet.Core.Contracts;
using Martlet.Core.Streaming;
using static Martlet.Fixtures.Tests.SequenceTestData;

namespace Martlet.Fixtures.Tests;

public sealed class SequenceMalformedInputTests
{
    [Theory]
    [InlineData("enum-case")]
    [InlineData("enum-number")]
    [InlineData("enum-composite")]
    [InlineData("duplicate-property")]
    [InlineData("missing-id")]
    [InlineData("ignored-surrogate")]
    [InlineData("invalid-utf8")]
    [InlineData("too-deep")]
    [InlineData("trailing-content")]
    [InlineData("null")]
    public void ParserFailuresBecomeSanitizedTerminalFailuresAtActualIngress(string kind)
    {
        var json = Encoding.UTF8.GetString(ContractJson.Write(Event(Request(), ProviderEventKind.Started, 0)));
        json = kind switch
        {
            "enum-case" => json.Replace("\"started\"", "\"Started\"", StringComparison.Ordinal),
            "enum-number" => json.Replace("\"started\"", "0", StringComparison.Ordinal),
            "enum-composite" => json.Replace("\"started\"", "\"started, completed\"", StringComparison.Ordinal),
            "duplicate-property" => json.Replace("\"epoch\": 0", "\"epoch\": 0, \"epoch\": 1", StringComparison.Ordinal),
            "missing-id" => json.Replace("\"request_id\"", "\"optional_request\"", StringComparison.Ordinal),
            "ignored-surrogate" => json.Insert(json.LastIndexOf('}'), ", \"ignored\": \"\\uD800\""),
            "invalid-utf8" => json.Insert(json.LastIndexOf('}'), ", \"ignored\": \"~\""),
            "too-deep" => "{\"ignored\":" + new string('[', 20) + "0" + new string(']', 20) + "}",
            "trailing-content" => json + " true",
            "null" => "null",
            _ => json
        };
        var bytes = Encoding.UTF8.GetBytes(json);
        if (kind == "invalid-utf8")
            bytes[Array.IndexOf(bytes, (byte)'~')] = 0xff;
        using var stream = new ProviderSequenceValidator(Request());
        var result = stream.AcceptJson(bytes).Snapshot;
        Assert.Equal(TurnOutcome.Failed, result.Result!.Outcome);
        Assert.Equal(ErrorCode.InvalidContract, result.Result.Error!.Code);
        Assert.Equal(SequenceIssue.InvalidInput, result.Issue);
        Assert.Equal(0, result.AcceptedEvents);
        Assert.DoesNotContain("ignored", result.Result.Error.Summary, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void JsonByteCapAndDirectEventCharacterCapAreBothEnforced()
    {
        using var bytes = new ProviderSequenceValidator(Request());
        Assert.Equal(ErrorCode.PayloadTooLarge,
            bytes.AcceptJson(new byte[ContractRules.MaxJsonBytes + 1]).Snapshot.Result!.Error!.Code);
        using var chars = new ProviderSequenceValidator(Request());
        chars.Accept(Event(Request(), ProviderEventKind.Started, 0));
        Assert.Equal(ErrorCode.InvalidContract, chars.Accept(
            Event(Request(), ProviderEventKind.Completed, 1, new string('X', ContractRules.MaxTextCharacters + 1)))
            .Snapshot.Result!.Error!.Code);
        using var unicode = new ProviderSequenceValidator(Request());
        unicode.Accept(Event(Request(), ProviderEventKind.Started, 0));
        Assert.Equal(SequenceIssue.InvalidInput,
            unicode.Accept(Event(Request(), ProviderEventKind.Completed, 1, "\ud800")).Snapshot.Issue);
        using var errorSummary = new ProviderSequenceValidator(Request());
        errorSummary.Accept(Event(Request(), ProviderEventKind.Started, 0));
        var failed = Event(Request(), ProviderEventKind.Failed, 1);
        Assert.Equal(SequenceIssue.InvalidInput,
            errorSummary.Accept(failed with { Error = failed.Error! with { Summary = "\ud800" } }).Snapshot.Issue);
    }

    [Fact]
    public void ExactEscapedTokensUnicodeAndAdditiveFieldsRemainValid()
    {
        var json = Encoding.UTF8.GetString(ContractJson.Write(Event(Request(), ProviderEventKind.Started, 0)))
            .Replace("\"started\"", "\"st\\u0061rted\"", StringComparison.Ordinal)
            .Replace("\"minor\": 0", "\"minor\": 99", StringComparison.Ordinal);
        json = json.Insert(json.LastIndexOf('}'), ", \"future\": {\"text\":\"\\uD83D\\uDE00\"}");
        using var stream = new ProviderSequenceValidator(Request());
        Assert.Equal(SequenceDecision.Accepted, stream.AcceptJson(Encoding.UTF8.GetBytes(json)).Decision);
        var completed = Event(Request(), ProviderEventKind.Completed, 1, "\ud83d\ude00");
        Assert.Equal(TurnOutcome.Completed, stream.AcceptJson(ContractJson.Write(completed)).Snapshot.Result!.Outcome);
        Assert.True(stream.TryReadText(out var chunk));
        Assert.Equal("\ud83d\ude00", chunk!.Text);
    }

    [Fact]
    public void WhitespaceOnlyDeltasAreNotASuccessfulAnswer()
    {
        using var stream = new ProviderSequenceValidator(Request());
        stream.Accept(Event(Request(), ProviderEventKind.Started, 0));
        stream.Accept(Event(Request(), ProviderEventKind.TextDelta, 1, " \t"));
        Assert.Equal(SequenceIssue.EmptyCompletion,
            stream.Accept(Event(Request(), ProviderEventKind.Completed, 2)).Snapshot.Issue);
    }

    [Fact]
    public void ClosedAttemptDiscardsEvenMalformedLateBytesWithoutChangingTerminal()
    {
        using var stream = new ProviderSequenceValidator(Request());
        var stopped = stream.Stop().Snapshot;
        Assert.Equal(SequenceDecision.ClosedDiscarded, stream.AcceptJson(new byte[300_000]).Decision);
        Assert.Equal(stopped, stream.Snapshot);
    }
}
