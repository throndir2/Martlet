using System.Text.Json;

namespace Martlet.Host.Setup;

public sealed class HostComposeGenerator
{
    public HostDeploymentBundle Generate(HostDeploymentDefinition definition, ArtifactPublishedImageObservation observation)
    {
        ArgumentNullException.ThrowIfNull(definition);
        ArgumentNullException.ThrowIfNull(observation);
        DeploymentRules.Require(observation.SelectionFingerprint == definition.Selection.Fingerprint,
            HostDeploymentFailure.ContentChanged);
        var findings = new List<HostDeploymentFinding>
        {
            new("host", "host-observation-and-runtime-qualification-required"),
            new("gateway", "dedicated-linux-service-recipe-and-protected-state-required"),
            new("engine", "explicit-engine-identity-and-image-import-adapter-required"),
            new("rights", "recorded-acquisition-rights-are-not-execution-approval")
        };
        var files = new List<HostDeploymentFile>();
        foreach (var role in definition.Selection.RoleIds)
        {
            findings.Add(new(role, "source-build-provenance-and-nonroot-service-recipe-required"));
            findings.Add(new(role, "model-content-rights-and-inference-qualification-required"));
            if (!observation.ContentVerified)
                findings.Add(new(role, "selected-image-content-not-published-and-verified"));
            // These are partial service objects, not executable Compose documents or worker configurations.
            if (observation.ContentVerified && role == "ollama-llm")
            {
                var images = definition.Selection.ImageCandidates.Where(image => image.RoleIds.Contains(role)).ToArray();
                DeploymentRules.Require(images.Length == 1, HostDeploymentFailure.InvalidDefinition);
                var image = images[0];
                files.Add(new(role + ".compose-fragment.json", JsonSerializer.SerializeToUtf8Bytes(new
                {
                    image = image.Source.Registry + "/" + image.Source.Repository + "@" + image.Digest,
                    platform = image.Platform,
                    pull_policy = "never",
                    networks = new[] { "workers" },
                    cap_drop = new[] { "ALL" },
                    security_opt = new[] { "no-new-privileges:true" },
                    labels = new SortedDictionary<string, string>(StringComparer.Ordinal)
                    {
                        ["com.martlet.host"] = definition.Identity.HostId.ToString("N"),
                        ["com.martlet.deployment"] = definition.Identity.DeploymentId.ToString("N"),
                        ["com.martlet.owner"] = definition.Identity.OwnerId.ToString("N")
                    }
                })));
            }
            if (role == "f5-tts")
                findings.Add(new(role, "upstream-image-is-not-martlet-stdio-worker-host"));
        }
        var ordered = findings.OrderBy(row => row.Subject, StringComparer.Ordinal)
            .ThenBy(row => row.Code, StringComparer.Ordinal).ToArray();
        files.Add(new("deployment.json", JsonSerializer.SerializeToUtf8Bytes(new
        {
            formatVersion = 2, purpose = "HostDeploymentConfiguration",
            projectName = definition.Identity.ProjectName, identity = definition.Identity,
            definitionFingerprint = definition.Fingerprint,
            installationRequestFingerprint = definition.Plan.InstallationRequestFingerprint,
            machine = definition.Plan.InstallationMachine,
            selectedRoles = definition.Selection.RoleIds,
            target = definition.Selection.Target, platform = definition.Selection.Platform,
            manifestSha256 = definition.Selection.ManifestSha256,
            selectionFingerprint = definition.Selection.Fingerprint,
            contentObservationFingerprint = observation.Fingerprint,
            contentVerified = observation.ContentVerified,
            acquisitionJournalVersion = observation.JournalVersion,
            recordedRightsFingerprint = observation.RecordedRightsFingerprint,
            proposedUbuntuConfigurationDirectory = definition.Configuration.ConfigurationDirectory,
            proposedUbuntuIdentityDirectory = definition.ProposedIdentityDirectory,
            preserveIdentityByDefault = true, gatewayProtocol = "2.0",
            gatewayBackend = "LinuxServicePermissions-unqualified",
            selectedArtifacts = definition.Selection.Artifacts.Select(artifact => new
            {
                artifact.ArtifactId, artifact.SourceRevision, artifact.ExpectedSha256,
                artifact.ExpectedBytes, artifact.IdentityFingerprint, artifact.Licenses
            }),
            selectedImages = definition.Selection.ImageCandidates.Select(image => new
            {
                image.ArtifactId, image.Digest, image.Platform, image.SourceRevision,
                image.IdentityFingerprint, image.Licenses
            }),
            runnableComposeAvailable = false, executionAuthorized = false, runtimeEnabled = false,
            hostReady = false, publisherAuthenticated = false, findings = ordered
        })));
        return new(definition, observation, files, ordered);
    }
}
