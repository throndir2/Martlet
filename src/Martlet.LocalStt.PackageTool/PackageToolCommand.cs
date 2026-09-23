using System.Text.Json;
using System.Text.Json.Serialization;
using Martlet.LocalStt;

namespace Martlet.LocalStt.PackageTool;

internal static class PackageToolCommand
{
    private const string Usage =
        """
        Usage:
          martlet-local-stt-package import --archive <absolute.zip> --model <absolute.bin> --notices <absolute-dir> --evidence <absolute.json> --destination <absolute-dir> [--approve-rights --approve-plan <preview-sha256>]
          martlet-local-stt-package inspect --destination <absolute-dir>
          martlet-local-stt-package cleanup --parent <absolute-dir>
          martlet-local-stt-package build-plan --facts <absolute.json>

        This tool never downloads, follows a URL, executes a compiler, launches whisper.cpp,
        changes firewall policy, or supplies the denied-egress evidence required for launch.
        """;
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        WriteIndented = false,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.SnakeCaseLower) }
    };

    internal static int Run(
        string[] args,
        TextWriter output,
        TextWriter error,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(args);
        ArgumentNullException.ThrowIfNull(output);
        ArgumentNullException.ThrowIfNull(error);
        try
        {
            if (args.Length == 0 ||
                args[0] is "--help" or "-h" or "help")
            {
                output.WriteLine(Usage);
                return 0;
            }
            var command = args[0];
            var options = Parse(args[1..]);
            return command switch
            {
                "import" => Import(options, output, cancellationToken),
                "inspect" => Inspect(options, output, cancellationToken),
                "cleanup" => Cleanup(options, output, cancellationToken),
                "build-plan" => BuildPlan(options, output),
                _ => UsageError(error)
            };
        }
        catch (OperationCanceledException)
        {
            Write(error, new { status = "canceled" });
            return 130;
        }
        catch (LocalSttProvisioningException provisioning)
            when (provisioning.Failure ==
                LocalSttProvisioningFailure.Canceled)
        {
            Write(error, new { status = "canceled" });
            return 130;
        }
        catch (LocalSttProvisioningException provisioning)
        {
            Write(error, new
            {
                status = "failed",
                failure = provisioning.Failure.ToString(),
                message = provisioning.Message,
                original_failure = provisioning.OriginalFailure?.ToString(),
                retained_directory = provisioning.RetainedDirectory
            });
            return 4;
        }
        catch (Exception exception) when (
            exception is ArgumentException or
                IOException or
                UnauthorizedAccessException or
                JsonException)
        {
            Write(error, new
            {
                status = "invalid_command",
                error = exception.GetType().Name,
                message = exception is ArgumentException ? exception.Message : "The local input could not be read."
            });
            return 2;
        }
    }

    private static int Import(
        IReadOnlyDictionary<string, string?> options,
        TextWriter output,
        CancellationToken cancellationToken)
    {
        RequireOnly(
            options,
            "archive",
            "model",
            "notices",
            "evidence",
            "destination",
            "approve-rights",
            "approve-plan");
        var evidence = LocalSttImportEvidence.Read(
            LocalSttOfflinePackageInput.ReadBoundedLocalFile(
                Required(options, "evidence"),
                LocalSttImportEvidence.MaximumDocumentBytes));
        var importer = new PhysicalLocalSttPackageImporter();
        var plan = importer.Preview(
            new LocalSttImportRequest(
                Required(options, "archive"),
                Required(options, "model"),
                Required(options, "notices"),
                Required(options, "destination"),
                evidence),
            cancellationToken);
        if (!plan.CanImport || !options.ContainsKey("approve-rights"))
        {
            Write(output, new
            {
                status = plan.CanImport ? "approval_required" : "blocked",
                plan.PackageId,
                plan.ManifestSha256,
                plan.Fingerprint,
                plan.Disposition,
                plan.Blocker,
                plan.RequiredFreeBytes,
                plan.ArchivePath,
                plan.ModelPath,
                plan.NoticeDirectory,
                plan.DestinationPath,
                plan.ImportEvidenceSha256,
                plan.NetworkAcquisitionBlocked,
                plan.AutomaticAcquisitionAllowed,
                plan.DeniedEgressEvidenceIncluded
            });
            return plan.CanImport ? 3 : 4;
        }
        if (!string.Equals(Required(options, "approve-plan"), plan.Fingerprint, StringComparison.Ordinal))
            throw new ArgumentException("The approved fingerprint does not match this preview.");
        var authorization = plan.Authorize(
            LocalSttRightsDecision
                .ApproveExactRuntimeModelAndNoticeRights);
        var receipt = importer.Import(
            plan,
            authorization,
            cancellationToken);
        Write(output, new
        {
            status = "imported",
            payload_path = Path.Combine(plan.DestinationPath, "payload"),
            candidate_status = "disabled_pending_qualification",
            can_launch = false,
            rights_qualified = false,
            dependency_inspection = "declared_import_tables_only",
            receipt.PackageId,
            receipt.ManifestSha256,
            receipt.ImportEvidenceSha256,
            receipt.IntegritySha256,
            receipt.AutomaticAcquisitionPerformed,
            receipt.DeniedEgressEvidenceIncluded,
            files = receipt.Files.Length
        });
        return 0;
    }

    private static int Inspect(
        IReadOnlyDictionary<string, string?> options,
        TextWriter output,
        CancellationToken cancellationToken)
    {
        RequireOnly(options, "destination");
        var inspection = new PhysicalLocalSttPackageImporter()
            .InspectInstalled(
                Required(options, "destination"),
                cancellationToken);
        Write(output, new
        {
            status = "bytes_verified_unqualified",
            inspection.DestinationPath,
            inspection.PayloadPath,
            inspection.CanLaunch,
            inspection.RuntimeQualified,
            inspection.RightsQualified,
            candidate_status = inspection.Status,
            dependency_inspection = "declared_import_tables_only",
            inspection.PackageVerifierStatus,
            inspection.Receipt.PackageId,
            inspection.Receipt.ManifestSha256,
            inspection.Receipt.IntegritySha256,
            inspection.Receipt.DeniedEgressEvidenceIncluded
        });
        return 0;
    }

    private static int Cleanup(
        IReadOnlyDictionary<string, string?> options,
        TextWriter output,
        CancellationToken cancellationToken)
    {
        RequireOnly(options, "parent");
        var receipt = new PhysicalLocalSttPackageImporter()
            .CleanupOwnedStaging(
                Required(options, "parent"),
                cancellationToken);
        Write(output, new
        {
            status = "cleanup_complete",
            receipt.RemovedOwnedDirectories,
            receipt.PreservedUnownedDirectories
        });
        return 0;
    }

    private static int BuildPlan(
        IReadOnlyDictionary<string, string?> options,
        TextWriter output)
    {
        RequireOnly(options, "facts");
        var facts = LocalSttSourceBuildFacts.Read(
            LocalSttOfflinePackageInput.ReadBoundedLocalFile(
                Required(options, "facts"),
                LocalSttSourceBuildFacts.MaximumDocumentBytes));
        var plan = LocalSttBuildPlan.Create(facts);
        output.WriteLine(System.Text.Encoding.UTF8.GetString(
            plan.ToCanonicalJson()));
        return 0;
    }

    private static Dictionary<string, string?> Parse(string[] args)
    {
        var result = new Dictionary<string, string?>(StringComparer.Ordinal);
        for (var index = 0; index < args.Length; index++)
        {
            var current = args[index];
            if (!current.StartsWith("--", StringComparison.Ordinal) ||
                current.Length <= 2)
                throw new ArgumentException("Invalid option.");
            var name = current[2..];
            string? value = null;
            if (name != "approve-rights")
            {
                if (++index >= args.Length ||
                    args[index].StartsWith("--", StringComparison.Ordinal))
                    throw new ArgumentException("Missing option value.");
                value = args[index];
            }
            if (!result.TryAdd(name, value))
                throw new ArgumentException("Duplicate option.");
        }
        return result;
    }

    private static void RequireOnly(
        IReadOnlyDictionary<string, string?> options,
        params string[] allowed)
    {
        var names = allowed.ToHashSet(StringComparer.Ordinal);
        if (options.Keys.Any(name => !names.Contains(name)))
            throw new ArgumentException("Unknown option.");
    }

    private static string Required(
        IReadOnlyDictionary<string, string?> options,
        string name) =>
        options.TryGetValue(name, out var value) &&
        !string.IsNullOrWhiteSpace(value)
            ? value
            : throw new ArgumentException("Missing required option.");

    private static int UsageError(TextWriter error)
    {
        error.WriteLine(Usage);
        return 2;
    }

    private static void Write<T>(TextWriter writer, T value) =>
        writer.WriteLine(JsonSerializer.Serialize(value, JsonOptions));
}
