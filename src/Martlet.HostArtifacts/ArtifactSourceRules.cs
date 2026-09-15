using System.Text.RegularExpressions;
using Martlet.Core.Contracts;

namespace Martlet.HostArtifacts;

internal static partial class ArtifactSourceRules
{
    internal static void Require(bool condition, string code)
    {
        if (!condition) throw new ArtifactManifestException(code);
    }

    internal static void Id(string value)
    {
        ContractRules.Identifier(value);
        Require(value.All(c => c is >= 'a' and <= 'z' or >= '0' and <= '9' or '.' or '_' or '-'),
            "manifest.identifier_invalid");
    }

    internal static bool Hex(string? value, int length) =>
        value is not null && value.Length == length &&
        value.All(c => c is >= '0' and <= '9' or >= 'a' and <= 'f') && value.Any(c => c != '0');

    internal static void Version(string value) => Require(value.Length is > 0 and <= 128 &&
        VersionPattern().IsMatch(value), "artifact.pin_invalid");

    internal static void Path(string path)
    {
        Require(path.Length is > 0 and <= 512, "artifact.path_invalid");
        foreach (var segment in path.Split('/'))
        {
            Require(segment.Length is > 0 and <= 128 && SegmentPattern().IsMatch(segment) &&
                segment is not ("." or "..") && !segment.EndsWith('.') &&
                !DevicePattern().IsMatch(segment), "artifact.path_invalid");
        }
    }

    internal static string Evidence(SourceDocument source, string path)
    {
        Path(path);
        return source.Kind == SourceKind.Github
            ? $"https://raw.githubusercontent.com/{source.Repository}/{source.Revision}/{path}"
            : $"https://huggingface.co/{source.Repository}/raw/{source.Revision}/{path}";
    }

    internal static void ExactUrl(string actual, string expected)
    {
        // Equality is checked on raw text, not on a URI that has already erased traversal/escape aliases.
        Require(actual.Length <= 2048 && actual == expected, "artifact.source_invalid");
    }

    internal static void Source(SourceDocument source)
    {
        Id(source.Id);
        var segments = source.Repository.Split('/');
        Require(segments.Length == 2 && segments.All(segment => segment.Length is > 0 and <= 128 &&
            RepositoryPattern().IsMatch(segment) && segment is not ("." or "..") && !segment.EndsWith('.')),
            "artifact.source_invalid");
        Require(Hex(source.Revision, 40), "artifact.pin_invalid");
        ExactUrl(source.CommitUrl, source.Kind == SourceKind.Github
            ? $"https://github.com/{source.Repository}/commit/{source.Revision}"
            : $"https://huggingface.co/{source.Repository}/tree/{source.Revision}");
    }

    internal static void Artifact(ArtifactDocument artifact, SourceDocument source)
    {
        Path(artifact.Path);
        Require(artifact.Bytes is > 0 and <= 17_592_186_044_416L, "artifact.pin_invalid");
        Require(artifact.Sha256 is null ? artifact.Sha256Evidence == HashEvidence.Unavailable :
            Hex(artifact.Sha256, 64) && artifact.Sha256Evidence != HashEvidence.Unavailable, "artifact.pin_invalid");
        if (source.Kind == SourceKind.Github)
        {
            Require(artifact.Kind == ArtifactKind.RuntimeArchive && artifact.Release is not null &&
                artifact.Sha256Evidence == HashEvidence.GithubReleaseMetadata && artifact.Sha256 is not null &&
                artifact.GitBlobSha1 is null && !artifact.Path.Contains('/'), "artifact.pin_invalid");
            var release = artifact.Release!;
            Require(release.ReleaseId > 0 && release.AssetId > 0 &&
                release.Tag.StartsWith('v'), "artifact.pin_invalid");
            Version(release.Tag[1..]);
            ExactUrl(release.MetadataUrl, $"https://api.github.com/repos/{source.Repository}/releases/{release.ReleaseId}");
            ExactUrl(artifact.EvidenceUrl, $"https://api.github.com/repos/{source.Repository}/releases/assets/{release.AssetId}");
            ExactUrl(artifact.SourceUrl, $"https://github.com/{source.Repository}/releases/download/{release.Tag}/{artifact.Path}");
        }
        else
        {
            Require(artifact.Kind != ArtifactKind.RuntimeArchive && artifact.Release is null &&
                Hex(artifact.GitBlobSha1, 40) &&
                artifact.Sha256Evidence is HashEvidence.HuggingFaceLfsMetadata or HashEvidence.Unavailable, "artifact.pin_invalid");
            ExactUrl(artifact.SourceUrl, $"https://huggingface.co/{source.Repository}/resolve/{source.Revision}/{artifact.Path}");
            var separator = artifact.Path.LastIndexOf('/');
            var directory = separator < 0 ? "" : "/" + artifact.Path[..separator];
            ExactUrl(artifact.EvidenceUrl,
                $"https://huggingface.co/api/models/{source.Repository}/tree/{source.Revision}{directory}");
        }
    }

    [GeneratedRegex(@"\A[0-9]+(?:\.[0-9]+){1,3}(?:-[a-z0-9]+(?:[.-][a-z0-9]+)*)?\z", RegexOptions.CultureInvariant)]
    private static partial Regex VersionPattern();
    [GeneratedRegex(@"\A[a-zA-Z0-9._-]+\z", RegexOptions.CultureInvariant)]
    private static partial Regex SegmentPattern();
    [GeneratedRegex(@"\A[a-zA-Z0-9][a-zA-Z0-9._-]*\z", RegexOptions.CultureInvariant)]
    private static partial Regex RepositoryPattern();
    [GeneratedRegex(@"\A(CON|PRN|AUX|NUL|CLOCK\$|CONIN\$|CONOUT\$|COM[1-9]|LPT[1-9])(?:\.|\z)", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex DevicePattern();
}
