using System.Buffers.Binary;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Martlet.Core.Contracts;
using Martlet.F5;
using Martlet.Perception;
using Martlet.Providers;

namespace Martlet.Gateway;

internal static class GatewayInferenceJson
{
    private static readonly UTF8Encoding StrictUtf8 = new(false, true);

    internal static GatewayInferenceRequest ParseRequest(
        ReadOnlyMemory<byte> bytes,
        GatewayInferenceRoute route,
        DateTimeOffset now,
        Func<string, byte[]?>? referenceAudio = null)
    {
        try
        {
            using var document = JsonDocument.Parse(bytes, new JsonDocumentOptions
            {
                MaxDepth = 16
            });
            var fields = Object(
                document.RootElement,
                [
                    "protocol_version",
                    "route_id",
                    "contract_id",
                    "contract_version",
                    "destination_id",
                    "worker_id",
                    "adapter_version",
                    "model_id",
                    "model_revision",
                    "model_sha256",
                    "artifact_identity_sha256",
                    "session_id",
                    "turn_id",
                    "request_id",
                    "epoch",
                    "deadline_utc",
                    "payload"
                ],
                ["parent_request_id"]);
            ParseProtocol(fields["protocol_version"]);
            Equal(Text(fields, "route_id", 96), route.RouteId);
            Equal(Text(fields, "contract_id", 96), route.ContractId);
            Equal(Text(fields, "contract_version", 32), route.ContractVersion);
            Equal(Text(fields, "destination_id", 128), route.DestinationId);
            Equal(Text(fields, "worker_id", 96), route.WorkerId);
            Equal(Text(fields, "adapter_version", 64), route.AdapterVersion);
            Equal(Text(fields, "model_id", 128), route.ModelId);
            Equal(Text(fields, "model_revision", 128), route.ModelRevision);
            Equal(Text(fields, "model_sha256", 64), route.ModelSha256);
            Equal(Text(fields, "artifact_identity_sha256", 71),
                route.ArtifactIdentitySha256);

            var sessionId = Guid(fields, "session_id");
            var turnId = Guid(fields, "turn_id");
            var requestId = Guid(fields, "request_id");
            Guid? parentRequestId = fields.TryGetValue("parent_request_id", out var parent)
                ? Guid(parent)
                : null;
            GatewayRules.Require(sessionId != System.Guid.Empty &&
                turnId != System.Guid.Empty &&
                requestId != System.Guid.Empty &&
                (parentRequestId is null ||
                    parentRequestId.Value != System.Guid.Empty) &&
                parentRequestId != requestId, "request.invalid");
            var epoch = Integer(fields, "epoch",
                route.Kind is GatewayInferenceKind.PerceptionOcr or GatewayInferenceKind.PerceptionVlm ? 1 : 0,
                int.MaxValue - 1);
            var deadline = Utc(fields, "deadline_utc");
            GatewayRules.Require(deadline > now &&
                deadline <= now + route.MaximumDuration, "job.deadline");
            GatewayInferencePayload payload = route.Kind switch
            {
                GatewayInferenceKind.OllamaChat =>
                    ParseOllama(fields["payload"], route),
                GatewayInferenceKind.F5Synthesis =>
                    ParseF5(fields["payload"], route, referenceAudio),
                GatewayInferenceKind.PerceptionOcr or
                    GatewayInferenceKind.PerceptionVlm =>
                    ParsePerception(fields["payload"], route, now),
                GatewayInferenceKind.Audio2Face =>
                    ParseAudio2Face(fields["payload"], route),
                GatewayInferenceKind.Transcription =>
                    ParseTranscription(fields["payload"], route),
                _ => throw new GatewayProtocolException("request.invalid")
            };
            return new(
                route,
                sessionId,
                turnId,
                requestId,
                parentRequestId,
                epoch,
                deadline,
                payload);
        }
        catch (JsonException)
        {
            throw new GatewayProtocolException("request.invalid");
        }
        catch (InvalidOperationException)
        {
            throw new GatewayProtocolException("request.invalid");
        }
        catch (FormatException)
        {
            throw new GatewayProtocolException("request.invalid");
        }
        catch (OverflowException)
        {
            throw new GatewayProtocolException("request.invalid");
        }
        catch (ContractException)
        {
            throw new GatewayProtocolException("request.invalid");
        }
        catch (PerceptionWorkerException)
        {
            throw new GatewayProtocolException("request.invalid");
        }
    }

    internal static GatewayInferenceCancellationRequest ParseCancellation(
        ReadOnlyMemory<byte> bytes)
    {
        try
        {
            using var document = JsonDocument.Parse(bytes, new JsonDocumentOptions
            {
                MaxDepth = 4
            });
            var fields = Object(
                document.RootElement,
                ["protocol_version", "route_id", "request_id"],
                []);
            ParseProtocol(fields["protocol_version"]);
            var routeId = Text(fields, "route_id", 96);
            GatewayRules.Identifier(routeId, 96);
            var requestId = Guid(fields, "request_id");
            GatewayRules.Require(requestId != System.Guid.Empty, "request.invalid");
            return new()
            {
                RouteId = routeId,
                RequestId = requestId
            };
        }
        catch (JsonException)
        {
            throw new GatewayProtocolException("request.invalid");
        }
        catch (InvalidOperationException)
        {
            throw new GatewayProtocolException("request.invalid");
        }
        catch (FormatException)
        {
            throw new GatewayProtocolException("request.invalid");
        }
    }

    private static GatewayOllamaChatPayload ParseOllama(
        JsonElement element,
        GatewayInferenceRoute route)
    {
        var fields = Object(
            element,
            ["input", "temperature", "maximum_output_tokens", "maximum_context_tokens"],
            ["system", "history", "images", "top_p", "top_k", "min_p", "repeat_penalty", "frequency_penalty",
                "presence_penalty", "context_tokens"]);
        var input = Text(fields, "input", BoundedTextInput.HardMaxUtf8Bytes, allowNewLines: true);
        string? system = fields.TryGetValue("system", out var systemElement)
            ? Text(systemElement, BoundedTextInput.HardMaxUtf8Bytes, allowNewLines: true)
            : null;
        var history = new List<TextHistoryMessage>();
        if (fields.TryGetValue("history", out var historyElement))
        {
            GatewayRules.Require(historyElement.ValueKind == JsonValueKind.Array &&
                historyElement.GetArrayLength() <= BoundedTextInput.HardMaxHistoryMessages, "request.invalid");
            foreach (var item in historyElement.EnumerateArray())
            {
                var message = Object(item, ["role", "text"], []);
                var role = Text(message, "role", 16) switch
                {
                    "user" => TextHistoryRole.User,
                    "assistant" => TextHistoryRole.Assistant,
                    _ => throw new GatewayProtocolException("request.invalid")
                };
                history.Add(new(role, Text(message, "text", BoundedTextInput.HardMaxUtf8Bytes, allowNewLines: true)));
            }
        }
        var bounded = new BoundedTextInput(input, system, history);
        GatewayRules.Require(bounded.Utf8Bytes <= route.MaximumInputBytes,
            "request.too_large");
        var temperature = Number(fields, "temperature");
        GatewayRules.Require(double.IsFinite(temperature) &&
            temperature is >= 0 and <= 2, "request.invalid");
        var outputTokens = checked((int)Integer(
            fields, "maximum_output_tokens", 1, 4_096));
        var contextTokens = checked((int)Integer(
            fields, "maximum_context_tokens", 1, 32_768));
        GatewayRules.Require(outputTokens <= contextTokens, "request.invalid");
        var images = new List<string>();
        if (fields.TryGetValue("images", out var imagesElement))
        {
            GatewayRules.Require(imagesElement.ValueKind == JsonValueKind.Array &&
                imagesElement.GetArrayLength() is > 0 and <= GatewayOllamaChatPayload.MaximumImages, "request.invalid");
            foreach (var item in imagesElement.EnumerateArray())
            {
                var encoded = Text(item, GatewayOllamaChatPayload.MaximumImageBase64Characters);
                var decoded = Convert.FromBase64String(encoded);
                GatewayRules.Require(Convert.ToBase64String(decoded) == encoded, "request.invalid");
                // Only a JPEG or PNG still image; BoundedImage checks the signature and byte bound.
                var jpeg = decoded.Length > 3 && decoded[0] == 0xFF && decoded[1] == 0xD8;
                _ = new BoundedImage(decoded, jpeg ? ImageMediaType.Jpeg : ImageMediaType.Png, 1, 1);
                images.Add(encoded);
            }
        }
        return new(input, temperature, outputTokens, contextTokens, system, history, images,
            ParseSampling(fields, outputTokens, contextTokens));
    }

    // Optional Ollama sampling options, each bounded like the client's settings; absent ones keep Ollama's defaults.
    private static GatewayOllamaSampling ParseSampling(
        IReadOnlyDictionary<string, JsonElement> fields, int outputTokens, int maximumContextTokens)
    {
        double? Optional(string name, double minimum, double maximum)
        {
            if (!fields.ContainsKey(name)) return null;
            var value = Number(fields, name);
            GatewayRules.Require(double.IsFinite(value) && value >= minimum && value <= maximum, "request.invalid");
            return value;
        }
        int? OptionalInteger(string name, long minimum, long maximum) =>
            fields.ContainsKey(name) ? checked((int)Integer(fields, name, minimum, maximum)) : null;
        var topP = Optional("top_p", 0, 1);
        GatewayRules.Require(topP is null or > 0, "request.invalid");
        var context = OptionalInteger("context_tokens", Martlet.Core.Settings.GenerationSettings.MinimumContextTokens,
            Martlet.Core.Settings.GenerationSettings.MaximumContextTokens);
        GatewayRules.Require(context is null || context > outputTokens && context <= maximumContextTokens, "request.invalid");
        return new(
            topP,
            OptionalInteger("top_k", 1, Martlet.Core.Settings.GenerationSettings.MaximumTopK),
            Optional("min_p", 0, 1),
            Optional("repeat_penalty", 0, 2),
            Optional("frequency_penalty", -2, 2),
            Optional("presence_penalty", -2, 2),
            context);
    }

    // The recording travels with the request, or (without reference_audio_base64) is the one this host keeps for a voice of
    // the shared speaking-voice list, found by its SHA-256 through referenceAudio; reference.missing when it has none.
    private static GatewayF5SynthesisPayload ParseF5(
        JsonElement element,
        GatewayInferenceRoute route,
        Func<string, byte[]?>? referenceAudio)
    {
        var fields = Object(
            element,
            [
                "preset_id",
                "reference_revision",
                "reference_audio_sha256",
                "transcript",
                "transcript_revision",
                "chunks"
            ],
            ["reference_language", "reference_audio_base64"]);
        var presetId = Guid(fields, "preset_id");
        GatewayRules.Require(presetId != System.Guid.Empty, "request.invalid");
        var referenceRevision = Text(fields, "reference_revision", 64);
        var audioSha256 = Text(fields, "reference_audio_sha256", 64);
        var transcript = Text(
            fields,
            "transcript",
            F5ReferenceLimits.MaximumTranscriptCharacters,
            allowNewLines: true);
        var transcriptRevision = Text(fields, "transcript_revision", 64);
        GatewayRules.Sha256(referenceRevision);
        GatewayRules.Sha256(audioSha256);
        GatewayRules.Sha256(transcriptRevision);
        GatewayRules.Require(StrictUtf8.GetByteCount(transcript) <=
            F5ReferenceLimits.MaximumTranscriptUtf8Bytes, "request.invalid");
        var actualTranscriptRevision = Convert.ToHexStringLower(
            SHA256.HashData(StrictUtf8.GetBytes(transcript)));
        GatewayRules.Require(actualTranscriptRevision == transcriptRevision,
            "request.invalid");
        Span<byte> revisionMaterial = stackalloc byte[64];
        Convert.FromHexString(audioSha256).CopyTo(revisionMaterial);
        Convert.FromHexString(transcriptRevision).CopyTo(revisionMaterial[32..]);
        GatewayRules.Require(Convert.ToHexStringLower(
            SHA256.HashData(revisionMaterial)) == referenceRevision,
            "request.invalid");

        byte[] audio;
        if (fields.ContainsKey("reference_audio_base64"))
        {
            var encodedAudio = Text(
                fields,
                "reference_audio_base64",
                ((F5ReferenceLimits.MaximumAudioFileBytes + 2) / 3) * 4);
            audio = Convert.FromBase64String(encodedAudio);
            if (Convert.ToBase64String(audio) != encodedAudio)
            {
                CryptographicOperations.ZeroMemory(audio);
                throw new GatewayProtocolException("request.invalid");
            }
        }
        else
        {
            GatewayRules.Require(referenceAudio is not null, "request.invalid");
            audio = referenceAudio!(audioSha256) ?? throw new GatewayProtocolException("reference.missing");
        }
        try
        {
            GatewayRules.Require(audio.Length <= route.MaximumInputBytes,
                "request.too_large");
            GatewayRules.Require(Convert.ToHexStringLower(SHA256.HashData(audio)) ==
                audioSha256, "request.invalid");
            ValidateF5Wave(audio, Martlet.Core.Settings.SpeechEngines.ForRoute(route.RouteId) ??
                Martlet.Core.Settings.SpeechEngines.F5);
            // The recording's language, which GPT-SoVITS needs; other engines ignore it.
            string? referenceLanguage = null;
            if (fields.ContainsKey("reference_language"))
            {
                referenceLanguage = Text(fields, "reference_language", 8);
                GatewayRules.Require(Martlet.Core.Settings.SpeechEngines.ReferenceLanguages.Contains(referenceLanguage),
                    "request.invalid");
            }
            var chunksElement = fields["chunks"];
            GatewayRules.Require(chunksElement.ValueKind == JsonValueKind.Array,
                "request.invalid");
            var chunks = new List<GatewayF5TextChunk>();
            var totalTextBytes = 0;
            foreach (var chunkElement in chunksElement.EnumerateArray())
            {
                GatewayRules.Require(chunks.Count < F5WorkerProtocol.MaximumTextChunks,
                    "request.too_large");
                var chunkFields = Object(
                    chunkElement,
                    ["index", "chunk_id", "text"],
                    []);
                var index = checked((int)Integer(
                    chunkFields, "index", 0, F5WorkerProtocol.MaximumTextChunks - 1));
                GatewayRules.Require(index == chunks.Count, "request.invalid");
                var chunkId = Text(chunkFields, "chunk_id", 64);
                GatewayRules.Identifier(chunkId);
                var text = Text(
                    chunkFields,
                    "text",
                    F5WorkerProtocol.MaximumChunkCharacters,
                    allowNewLines: true);
                totalTextBytes = checked(totalTextBytes + StrictUtf8.GetByteCount(text));
                GatewayRules.Require(StrictUtf8.GetByteCount(text) <=
                    F5WorkerProtocol.MaximumChunkUtf8Bytes, "request.too_large");
                chunks.Add(new(index, chunkId, text));
            }
            GatewayRules.Require(chunks.Count > 0 &&
                chunks.Select(chunk => chunk.ChunkId)
                    .Distinct(StringComparer.Ordinal).Count() == chunks.Count &&
                totalTextBytes <= F5WorkerProtocol.MaximumTextUtf8Bytes,
                "request.invalid");
            return new(
                presetId,
                referenceRevision,
                audioSha256,
                transcript,
                transcriptRevision,
                audio,
                chunks.ToArray(),
                referenceLanguage);
        }
        catch
        {
            CryptographicOperations.ZeroMemory(audio);
            throw;
        }
    }

    private static GatewayPerceptionPayload ParsePerception(
        JsonElement element,
        GatewayInferenceRoute route,
        DateTimeOffset now)
    {
        var required = new List<string>
        {
            "frame_id",
            "capture_epoch",
            "selection_id",
            "source_revision",
            "capture_permission_revision",
            "captured_at_utc",
            "maximum_frame_age_milliseconds",
            "frame_sha256",
            "frame_byte_count",
            "width",
            "height"
        };
        if (route.Kind == GatewayInferenceKind.PerceptionVlm)
            required.Add("question");
        var fields = Object(
            element,
            required,
            [
                "frame_base64",
                "frame_reference",
                "frame_reference_expires_at_utc"
            ]);
        var frameId = Guid(fields, "frame_id");
        var epoch = Integer(fields, "capture_epoch", 1, int.MaxValue);
        var selectionId = Guid(fields, "selection_id");
        GatewayRules.Require(frameId != System.Guid.Empty &&
            selectionId != System.Guid.Empty, "request.invalid");
        var sourceRevision = Text(fields, "source_revision", 64);
        var permissionRevision = Text(fields, "capture_permission_revision", 64);
        GatewayRules.Sha256(sourceRevision);
        GatewayRules.Sha256(permissionRevision);
        var capturedAt = Utc(fields, "captured_at_utc");
        var maximumFrameAge = TimeSpan.FromMilliseconds(Integer(
            fields,
            "maximum_frame_age_milliseconds",
            1,
            Convert.ToInt64(PerceptionProtocol.MaximumFrameAge.TotalMilliseconds,
                CultureInfo.InvariantCulture)));
        GatewayRules.Require(
            capturedAt <= now + PerceptionProtocol.MaximumClockSkew &&
            now - capturedAt < maximumFrameAge,
            "request.invalid");
        var frameSha256 = Text(fields, "frame_sha256", 64);
        GatewayRules.Sha256(frameSha256);
        var frameByteCount = checked((int)Integer(
            fields, "frame_byte_count", 1, route.MaximumInputBytes));
        var width = checked((int)Integer(
            fields, "width", 1, route.PerceptionIdentity!.Limits.MaximumWidth));
        var height = checked((int)Integer(
            fields, "height", 1, route.PerceptionIdentity!.Limits.MaximumHeight));
        GatewayRules.Require(checked((long)width * height) <=
            route.PerceptionIdentity!.Limits.MaximumPixels, "request.too_large");

        var inline = fields.TryGetValue("frame_base64", out var inlineElement);
        var reference = fields.TryGetValue("frame_reference", out var referenceElement);
        GatewayRules.Require(inline != reference, "request.invalid");
        SelectedWindowFrameContent content;
        if (inline)
        {
            GatewayRules.Require(!fields.ContainsKey("frame_reference_expires_at_utc"),
                "request.invalid");
            var encoded = Text(inlineElement, ((route.MaximumInputBytes + 2) / 3) * 4);
            var png = Convert.FromBase64String(encoded);
            if (Convert.ToBase64String(png) != encoded)
            {
                CryptographicOperations.ZeroMemory(png);
                throw new GatewayProtocolException("request.invalid");
            }
            try
            {
                GatewayRules.Require(png.Length == frameByteCount &&
                    Convert.ToHexStringLower(SHA256.HashData(png)) == frameSha256,
                    "request.invalid");
                content = SelectedWindowFrameContent.FromInlinePng(png);
                GatewayRules.Require(content.Width == width &&
                    content.Height == height, "request.invalid");
            }
            finally
            {
                CryptographicOperations.ZeroMemory(png);
            }
        }
        else
        {
            GatewayRules.Require(fields.TryGetValue(
                "frame_reference_expires_at_utc", out _), "request.invalid");
            content = SelectedWindowFrameContent.FromEphemeralGatewayReference(
                Text(referenceElement, 96),
                frameSha256,
                frameByteCount,
                width,
                height,
                Utc(fields, "frame_reference_expires_at_utc"));
            GatewayRules.Require(
                content.ReferenceExpiresAtUtc > now,
                "request.invalid");
        }

        string? question = null;
        if (route.Kind == GatewayInferenceKind.PerceptionVlm)
        {
            question = Text(
                fields,
                "question",
                PerceptionProtocol.MaximumQuestionCharacters);
            GatewayRules.Require(StrictUtf8.GetByteCount(question) <=
                PerceptionProtocol.MaximumQuestionUtf8Bytes, "request.too_large");
        }
        var frame = new SelectedWindowFrame(
            frameId,
            epoch,
            new()
            {
                SelectionId = selectionId,
                SourceRevision = sourceRevision,
                CapturePermissionRevision = permissionRevision
            },
            capturedAt,
            content);
        return new(frame, question, maximumFrameAge);
    }

    private static GatewayAudio2FacePayload ParseAudio2Face(
        JsonElement element,
        GatewayInferenceRoute route)
    {
        var fields = Object(element, ["sample_rate", "pcm_base64"], []);
        var sampleRate = checked((int)Integer(fields, "sample_rate", 16_000, 48_000));
        GatewayRules.Require(sampleRate is 16_000 or 24_000 or 44_100 or 48_000,
            "request.invalid");
        var encoded = Text(fields, "pcm_base64", ((route.MaximumInputBytes + 2) / 3) * 4);
        var pcm = Convert.FromBase64String(encoded);
        if (Convert.ToBase64String(pcm) != encoded || pcm.Length is 0 ||
            pcm.Length % 2 != 0 || pcm.Length > route.MaximumInputBytes)
        {
            CryptographicOperations.ZeroMemory(pcm);
            throw new GatewayProtocolException(pcm.Length > route.MaximumInputBytes
                ? "request.too_large" : "request.invalid");
        }
        return new(sampleRate, pcm);
    }

    private static GatewayTranscriptionPayload ParseTranscription(
        JsonElement element,
        GatewayInferenceRoute route)
    {
        var fields = Object(element, ["sample_rate", "pcm_base64"], []);
        GatewayRules.Require(Integer(fields, "sample_rate", 8_000, 48_000) ==
            GatewayInferenceRoute.TranscriptionSampleRate, "request.invalid");
        var encoded = Text(fields, "pcm_base64", ((route.MaximumInputBytes + 2) / 3) * 4);
        var pcm = Convert.FromBase64String(encoded);
        if (Convert.ToBase64String(pcm) != encoded || pcm.Length is 0 ||
            pcm.Length % 2 != 0 || pcm.Length > route.MaximumInputBytes)
        {
            CryptographicOperations.ZeroMemory(pcm);
            throw new GatewayProtocolException(pcm.Length > route.MaximumInputBytes
                ? "request.too_large" : "request.invalid");
        }
        return new(GatewayInferenceRoute.TranscriptionSampleRate, pcm);
    }

    private static void ValidateF5Wave(ReadOnlySpan<byte> bytes, Martlet.Core.Settings.SpeechEngine engine)
    {
        GatewayRules.Require(bytes.Length is >= 44 and <=
            F5ReferenceLimits.MaximumAudioFileBytes &&
            bytes[..4].SequenceEqual("RIFF"u8) &&
            bytes.Slice(8, 4).SequenceEqual("WAVE"u8) &&
            BinaryPrimitives.ReadUInt32LittleEndian(bytes.Slice(4, 4)) + 8UL ==
                (ulong)bytes.Length, "request.invalid");

        int? sampleRate = null;
        int? dataBytes = null;
        var usableSample = false;
        var offset = 12;
        while (offset < bytes.Length)
        {
            GatewayRules.Require(bytes.Length - offset >= 8, "request.invalid");
            var id = bytes.Slice(offset, 4);
            var declared = BinaryPrimitives.ReadUInt32LittleEndian(
                bytes.Slice(offset + 4, 4));
            var payload = offset + 8;
            GatewayRules.Require(declared <= int.MaxValue &&
                declared <= bytes.Length - payload, "request.invalid");
            var count = checked((int)declared);
            var end = checked(payload + count);
            if (id.SequenceEqual("fmt "u8))
            {
                GatewayRules.Require(sampleRate is null && count == 16,
                    "request.invalid");
                var format = BinaryPrimitives.ReadUInt16LittleEndian(
                    bytes.Slice(payload, 2));
                var channels = BinaryPrimitives.ReadUInt16LittleEndian(
                    bytes.Slice(payload + 2, 2));
                var rate = BinaryPrimitives.ReadInt32LittleEndian(
                    bytes.Slice(payload + 4, 4));
                var byteRate = BinaryPrimitives.ReadInt32LittleEndian(
                    bytes.Slice(payload + 8, 4));
                var alignment = BinaryPrimitives.ReadUInt16LittleEndian(
                    bytes.Slice(payload + 12, 2));
                var bits = BinaryPrimitives.ReadUInt16LittleEndian(
                    bytes.Slice(payload + 14, 2));
                GatewayRules.Require(format == 1 && channels == 1 &&
                    bits == 16 &&
                    (rate is 16_000 or 22_050 or 24_000 or 44_100 or 48_000) &&
                    alignment == 2 && byteRate == checked(rate * 2),
                    "request.invalid");
                sampleRate = rate;
            }
            else if (id.SequenceEqual("data"u8))
            {
                GatewayRules.Require(dataBytes is null && count > 0 &&
                    count % 2 == 0, "request.invalid");
                dataBytes = count;
                for (var sample = payload; sample < end; sample += 2)
                    usableSample |= BinaryPrimitives.ReadInt16LittleEndian(
                        bytes.Slice(sample, 2)) != 0;
            }
            offset = checked(end + ((count & 1) == 0 ? 0 : 1));
            GatewayRules.Require(offset <= bytes.Length, "request.invalid");
        }
        GatewayRules.Require(offset == bytes.Length &&
            sampleRate is not null &&
            dataBytes is not null &&
            usableSample, "request.invalid");
        var durationMilliseconds = Math.Ceiling(
            dataBytes!.Value / 2d * 1000d / sampleRate!.Value);
        GatewayRules.Require(durationMilliseconds >=
            Math.Max(F5ReferenceLimits.MinimumDurationMilliseconds, engine.MinimumReferenceMilliseconds) &&
            durationMilliseconds <=
            Math.Min(F5ReferenceLimits.MaximumDurationMilliseconds, engine.MaximumReferenceMilliseconds), "request.invalid");
    }

    private static Dictionary<string, JsonElement> Object(
        JsonElement element,
        IReadOnlyCollection<string> required,
        IReadOnlyCollection<string> optional)
    {
        GatewayRules.Require(element.ValueKind == JsonValueKind.Object,
            "request.invalid");
        var allowed = required.Concat(optional).ToHashSet(StringComparer.Ordinal);
        var fields = new Dictionary<string, JsonElement>(StringComparer.Ordinal);
        foreach (var property in element.EnumerateObject())
        {
            GatewayRules.Require(allowed.Contains(property.Name) &&
                fields.TryAdd(property.Name, property.Value), "request.invalid");
            InspectStrings(property.Value);
        }
        GatewayRules.Require(required.All(fields.ContainsKey), "request.invalid");
        return fields;
    }

    private static void InspectStrings(JsonElement element)
    {
        if (element.ValueKind == JsonValueKind.String)
        {
            _ = element.GetString();
            return;
        }
        if (element.ValueKind == JsonValueKind.Object)
        {
            var names = new HashSet<string>(StringComparer.Ordinal);
            foreach (var property in element.EnumerateObject())
            {
                GatewayRules.Require(names.Add(property.Name), "request.invalid");
                InspectStrings(property.Value);
            }
            return;
        }
        if (element.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in element.EnumerateArray())
                InspectStrings(item);
        }
    }

    private static void ParseProtocol(JsonElement element)
    {
        var fields = Object(element, ["major", "minor"], []);
        var major = Integer(fields, "major", 0, 9999);
        var minor = Integer(fields, "minor", 0, 9999);
        GatewayRules.Require(major == GatewayProtocolVersion.Current.Major,
            "protocol.unsupported");
        GatewayRules.Require(minor <= GatewayProtocolVersion.Current.Minor,
            "protocol.unsupported");
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
            "request.invalid");
        var value = element.GetString();
        GatewayRules.Require(value is { Length: > 0 } &&
            value.Length <= maximum &&
            !value.Any(character => char.IsControl(character) &&
                !(allowNewLines && character is '\r' or '\n' or '\t')),
            "request.invalid");
        try
        {
            GatewayRules.Require(StrictUtf8.GetByteCount(value!) <=
                checked(maximum * 4), "request.invalid");
        }
        catch (EncoderFallbackException)
        {
            throw new GatewayProtocolException("request.invalid");
        }
        return value!;
    }

    private static long Integer(
        IReadOnlyDictionary<string, JsonElement> fields,
        string name,
        long minimum,
        long maximum)
    {
        var element = fields[name];
        GatewayRules.Require(element.ValueKind == JsonValueKind.Number,
            "request.invalid");
        var raw = element.GetRawText();
        long value = 0;
        GatewayRules.Require(raw.Length > 0 &&
            (raw[0] == '-' ? raw.Length > 1 && raw[1..].All(char.IsAsciiDigit)
                : raw.All(char.IsAsciiDigit)) &&
            element.TryGetInt64(out value) &&
            value >= minimum &&
            value <= maximum, "request.invalid");
        return value;
    }

    private static double Number(
        IReadOnlyDictionary<string, JsonElement> fields,
        string name)
    {
        var element = fields[name];
        double value = 0;
        GatewayRules.Require(element.ValueKind == JsonValueKind.Number &&
            element.TryGetDouble(out value), "request.invalid");
        return value;
    }

    private static Guid Guid(
        IReadOnlyDictionary<string, JsonElement> fields,
        string name) => Guid(fields[name]);

    private static Guid Guid(JsonElement element)
    {
        var text = Text(element, 36);
        GatewayRules.Require(System.Guid.TryParseExact(text, "D", out var value) &&
            text == value.ToString("D"), "request.invalid");
        return value;
    }

    private static DateTimeOffset Utc(
        IReadOnlyDictionary<string, JsonElement> fields,
        string name) => Utc(fields[name]);

    private static DateTimeOffset Utc(JsonElement element)
    {
        var text = Text(element, 40);
        GatewayRules.Require(DateTimeOffset.TryParseExact(
                text,
                "O",
                CultureInfo.InvariantCulture,
                DateTimeStyles.None,
                out var value) &&
            value.Offset == TimeSpan.Zero &&
            text == value.ToString("O", CultureInfo.InvariantCulture),
            "request.invalid");
        return value;
    }

    private static void Equal(string actual, string expected) =>
        GatewayRules.Require(actual == expected, "worker.identity");
}
