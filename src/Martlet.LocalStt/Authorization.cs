namespace Martlet.LocalStt;

public sealed record LocalSttRequest(Guid OperationId, DateTimeOffset Deadline)
{
    internal void Validate(DateTimeOffset now)
    {
        if (OperationId == Guid.Empty || Deadline <= now ||
            Deadline - now > LocalSttPackageManifest.MaximumActionLifetime)
            throw new LocalSttContractException(LocalSttFailureCode.DeadlineExceeded);
    }
}

public sealed class LocalAudioAuthorization
{
    public Guid OperationId { get; }
    public string PackageId { get; }
    public string ManifestSha256 { get; }
    public string ModelId { get; }
    public string ModelSha256 { get; }
    public string Language { get; }
    public string AudioSha256 { get; }
    public int AudioBytes { get; }
    public DateTimeOffset ExpiresAt { get; }
    public DateTimeOffset RequestDeadline { get; }
    public bool AllowProcessLaunch { get; }
    public bool AllowLocalAudioProcessing { get; }
    public bool AllowEphemeralAudioFile { get; }
    public bool RequireDeniedEgress { get; }
    public bool RightsReviewedForCandidate { get; }
    private int consumed;

    public LocalAudioAuthorization(
        Guid operationId,
        string packageId,
        string manifestSha256,
        string modelId,
        string modelSha256,
        string language,
        string audioSha256,
        int audioBytes,
        DateTimeOffset expiresAt,
        bool allowLocalAudioProcessing,
        bool allowEphemeralAudioFile,
        bool requireDeniedEgress,
        bool rightsReviewedForCandidate,
        DateTimeOffset requestDeadline,
        bool allowProcessLaunch)
    {
        OperationId = operationId;
        PackageId = packageId;
        ManifestSha256 = manifestSha256;
        ModelId = modelId;
        ModelSha256 = modelSha256;
        Language = language;
        AudioSha256 = audioSha256;
        AudioBytes = audioBytes;
        ExpiresAt = expiresAt;
        RequestDeadline = requestDeadline;
        AllowProcessLaunch = allowProcessLaunch;
        AllowLocalAudioProcessing = allowLocalAudioProcessing;
        AllowEphemeralAudioFile = allowEphemeralAudioFile;
        RequireDeniedEgress = requireDeniedEgress;
        RightsReviewedForCandidate = rightsReviewedForCandidate;
    }

    internal bool TryConsume() => Interlocked.CompareExchange(ref consumed, 1, 0) == 0;

    public override string ToString() => nameof(LocalAudioAuthorization);
}
