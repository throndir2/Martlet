using System.Globalization;
using System.Text;
using System.Text.Json;
using Martlet.Perception;

namespace Martlet.Gateway;

internal static class GatewayPerceptionOutput
{
    private static readonly UTF8Encoding StrictUtf8 = new(false, true);

    internal static void Validate(
        ReadOnlyMemory<byte> bytes,
        GatewayInferenceRequest request,
        string hostId,
        DateTimeOffset now)
    {
        GatewayRules.Require(
            request.Payload is GatewayPerceptionPayload &&
            bytes.Length <= request.Route.MaximumOutputBytes,
            "stream.limit");
        var payload = (GatewayPerceptionPayload)request.Payload;
        try
        {
            using var document = JsonDocument.Parse(bytes, new JsonDocumentOptions
            {
                MaxDepth = 16
            });
            var isOcr = request.Route.Kind == GatewayInferenceKind.PerceptionOcr;
            var fields = Object(
                document.RootElement,
                isOcr
                    ? ["role", "provenance", "expires_at_utc", "ocr"]
                    : ["role", "provenance", "expires_at_utc", "vlm"],
                []);
            var expectedRole = isOcr ? "ocr" : "visual_question_answering";
            Equal(Text(fields, "role", 32), expectedRole);
            var processedAt = ValidateProvenance(
                fields["provenance"],
                request,
                payload,
                hostId,
                expectedRole,
                now);
            var expiresAt = Utc(fields, "expires_at_utc");
            GatewayRules.Require(
                expiresAt > now &&
                expiresAt > processedAt &&
                expiresAt <= payload.Frame.CapturedAtUtc +
                    payload.MaximumFrameAge &&
                expiresAt <= request.DeadlineUtc,
                "stream.invalid");
            if (isOcr)
                ValidateOcr(fields["ocr"], request.Route.MaximumOutputBytes);
            else
                ValidateVlm(fields["vlm"], request.Route.MaximumOutputBytes);
        }
        catch (JsonException)
        {
            throw new GatewayProtocolException("stream.invalid");
        }
        catch (InvalidOperationException)
        {
            throw new GatewayProtocolException("stream.invalid");
        }
        catch (FormatException)
        {
            throw new GatewayProtocolException("stream.invalid");
        }
        catch (EncoderFallbackException)
        {
            throw new GatewayProtocolException("stream.invalid");
        }
    }

    private static DateTimeOffset ValidateProvenance(
        JsonElement element,
        GatewayInferenceRequest request,
        GatewayPerceptionPayload payload,
        string hostId,
        string expectedRole,
        DateTimeOffset now)
    {
        var fields = Object(
            element,
            [
                "frame_id",
                "capture_epoch",
                "selection_id",
                "source_revision",
                "capture_permission_revision",
                "frame_sha256",
                "captured_at_utc",
                "processed_at_utc",
                "destination_id",
                "host_id",
                "worker_id",
                "evidence",
                "role",
                "model_id",
                "model_revision",
                "model_sha256"
            ],
            []);
        Equal(Guid(fields, "frame_id"), payload.Frame.FrameId);
        Equal(Integer(fields, "capture_epoch", 1, int.MaxValue),
            payload.Frame.CaptureEpoch);
        Equal(Guid(fields, "selection_id"),
            payload.Frame.Source.SelectionId);
        Equal(Text(fields, "source_revision", 64),
            payload.Frame.Source.SourceRevision);
        Equal(Text(fields, "capture_permission_revision", 64),
            payload.Frame.Source.CapturePermissionRevision);
        Equal(Text(fields, "frame_sha256", 64),
            payload.Frame.Content.Sha256);
        var capturedAt = Utc(fields, "captured_at_utc");
        var processedAt = Utc(fields, "processed_at_utc");
        GatewayRules.Require(
            capturedAt == payload.Frame.CapturedAtUtc &&
            processedAt >= capturedAt &&
            processedAt <= now + PerceptionProtocol.MaximumClockSkew,
            "stream.invalid");
        Equal(Text(fields, "destination_id", 128),
            request.Route.DestinationId);
        Equal(Text(fields, "host_id", 64), hostId);
        Equal(Text(fields, "worker_id", 96), request.Route.WorkerId);
        var evidence = Text(fields, "evidence", 32);
        Equal(evidence, request.Route.PerceptionIdentity!.Evidence ==
            PerceptionEvidenceKind.LiveWorker ? "live_worker" : "synthetic_fixture");
        Equal(Text(fields, "role", 32), expectedRole);
        Equal(Text(fields, "model_id", 128), request.Route.ModelId);
        Equal(Text(fields, "model_revision", 128),
            request.Route.ModelRevision);
        Equal(Text(fields, "model_sha256", 64),
            request.Route.ModelSha256);
        return processedAt;
    }

    private static void ValidateOcr(
        JsonElement element,
        int maximumOutputBytes)
    {
        var fields = Object(element, ["regions"], ["detected_language"]);
        var regions = fields["regions"];
        GatewayRules.Require(regions.ValueKind == JsonValueKind.Array,
            "stream.invalid");
        var index = 0;
        var textBytes = 0;
        foreach (var region in regions.EnumerateArray())
        {
            GatewayRules.Require(index < PerceptionProtocol.MaximumOcrRegions,
                "stream.limit");
            var regionFields = Object(
                region,
                ["index", "text", "bounds", "confidence"],
                []);
            Equal(Integer(
                regionFields,
                "index",
                0,
                PerceptionProtocol.MaximumOcrRegions - 1), index);
            var text = Text(
                regionFields,
                "text",
                PerceptionProtocol.MaximumRegionTextCharacters,
                allowNewLines: true);
            var bytes = StrictUtf8.GetByteCount(text);
            GatewayRules.Require(bytes <=
                PerceptionProtocol.MaximumRegionTextUtf8Bytes,
                "stream.limit");
            textBytes = checked(textBytes + bytes);
            ValidateBounds(regionFields["bounds"]);
            ValidateConfidence(regionFields["confidence"]);
            index++;
        }
        GatewayRules.Require(textBytes <= maximumOutputBytes,
            "stream.limit");
        if (fields.TryGetValue("detected_language", out var language))
        {
            var value = Text(language, 16);
            GatewayRules.Require(
                char.IsAsciiLetterOrDigit(value[0]) &&
                value.All(character =>
                    char.IsAsciiLetterOrDigit(character) ||
                    character is '.' or '_' or '-'),
                "stream.invalid");
        }
    }

    private static void ValidateVlm(
        JsonElement element,
        int maximumOutputBytes)
    {
        var fields = Object(
            element,
            ["answer", "uncertainty", "confidence"],
            []);
        var answer = Text(fields, "answer", 4096, allowNewLines: true);
        var uncertainty = Text(
            fields,
            "uncertainty",
            512,
            allowNewLines: true);
        var bytes = checked(
            StrictUtf8.GetByteCount(answer) +
            StrictUtf8.GetByteCount(uncertainty));
        GatewayRules.Require(
            StrictUtf8.GetByteCount(answer) <= 8192 &&
            StrictUtf8.GetByteCount(uncertainty) <= 1024 &&
            bytes <= maximumOutputBytes,
            "stream.limit");
        ValidateConfidence(fields["confidence"]);
    }

    private static void ValidateBounds(JsonElement element)
    {
        var fields = Object(
            element,
            ["left", "top", "right", "bottom"],
            []);
        var left = Integer(fields, "left", 0, 10_000);
        var top = Integer(fields, "top", 0, 10_000);
        var right = Integer(fields, "right", 0, 10_000);
        var bottom = Integer(fields, "bottom", 0, 10_000);
        GatewayRules.Require(left < right && top < bottom,
            "stream.invalid");
    }

    private static void ValidateConfidence(JsonElement element)
    {
        var fields = Object(element, ["kind"], ["value"]);
        var kind = Text(fields, "kind", 16);
        if (kind == "unavailable")
        {
            GatewayRules.Require(!fields.ContainsKey("value"),
                "stream.invalid");
            return;
        }
        GatewayRules.Require(
            (kind is "uncalibrated" or "calibrated") &&
            fields.TryGetValue("value", out var valueElement) &&
            valueElement.ValueKind == JsonValueKind.Number &&
            valueElement.TryGetDouble(out var value) &&
            double.IsFinite(value) &&
            value is >= 0 and <= 1,
            "stream.invalid");
    }

    private static Dictionary<string, JsonElement> Object(
        JsonElement element,
        IReadOnlyCollection<string> required,
        IReadOnlyCollection<string> optional)
    {
        GatewayRules.Require(element.ValueKind == JsonValueKind.Object,
            "stream.invalid");
        var allowed = required.Concat(optional).ToHashSet(StringComparer.Ordinal);
        var fields = new Dictionary<string, JsonElement>(StringComparer.Ordinal);
        foreach (var property in element.EnumerateObject())
        {
            GatewayRules.Require(
                allowed.Contains(property.Name) &&
                fields.TryAdd(property.Name, property.Value),
                "stream.invalid");
            if (property.Value.ValueKind == JsonValueKind.String)
                _ = property.Value.GetString();
        }
        GatewayRules.Require(required.All(fields.ContainsKey),
            "stream.invalid");
        return fields;
    }

    private static string Text(
        IReadOnlyDictionary<string, JsonElement> fields,
        string name,
        int maximum,
        bool allowNewLines = false) =>
        Text(fields[name], maximum, allowNewLines);

    private static string Text(
        JsonElement element,
        int maximum,
        bool allowNewLines = false)
    {
        GatewayRules.Require(element.ValueKind == JsonValueKind.String,
            "stream.invalid");
        var value = element.GetString();
        GatewayRules.Require(value is { Length: > 0 } &&
            value.Length <= maximum &&
            !value.Any(character => char.IsControl(character) &&
                !(allowNewLines && character is '\r' or '\n' or '\t')),
            "stream.invalid");
        _ = StrictUtf8.GetByteCount(value!);
        return value!;
    }

    private static long Integer(
        IReadOnlyDictionary<string, JsonElement> fields,
        string name,
        long minimum,
        long maximum)
    {
        var element = fields[name];
        var raw = element.GetRawText();
        long value = 0;
        GatewayRules.Require(
            element.ValueKind == JsonValueKind.Number &&
            raw.Length > 0 &&
            raw.All(char.IsAsciiDigit) &&
            element.TryGetInt64(out value) &&
            value >= minimum &&
            value <= maximum,
            "stream.invalid");
        return value;
    }

    private static Guid Guid(
        IReadOnlyDictionary<string, JsonElement> fields,
        string name)
    {
        var text = Text(fields, name, 36);
        GatewayRules.Require(System.Guid.TryParseExact(text, "D", out var value) &&
            text == value.ToString("D"), "stream.invalid");
        return value;
    }

    private static DateTimeOffset Utc(
        IReadOnlyDictionary<string, JsonElement> fields,
        string name)
    {
        var text = Text(fields, name, 40);
        GatewayRules.Require(DateTimeOffset.TryParseExact(
                text,
                "O",
                CultureInfo.InvariantCulture,
                DateTimeStyles.None,
                out var value) &&
            value.Offset == TimeSpan.Zero &&
            text == value.ToString("O", CultureInfo.InvariantCulture),
            "stream.invalid");
        return value;
    }

    private static void Equal<T>(T actual, T expected)
        where T : IEquatable<T> =>
        GatewayRules.Require(actual.Equals(expected), "stream.invalid");
}
