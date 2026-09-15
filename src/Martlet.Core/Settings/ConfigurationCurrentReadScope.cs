namespace Martlet.Core.Settings;

public sealed class ConfigurationCurrentInspection
{
    public string Path { get; }
    public string CurrentJson { get; }
    public string Revision { get; }
    public Guid ProfileId { get; }
    public int SchemaVersion { get; }

    internal ConfigurationCurrentInspection(string path, string currentJson, string revision, Guid profileId, int schemaVersion)
    {
        Path = path;
        CurrentJson = currentJson;
        Revision = revision;
        ProfileId = profileId;
        SchemaVersion = schemaVersion;
    }
}

// Owns only a current observation. Neither the inspection nor this scope proves a historical restore.
public sealed class ConfigurationCurrentReadScope : IAsyncDisposable
{
    private readonly SettingsStore store;
    private readonly CancellationToken token;
    private readonly FileStream writer, current, currentName;
    private readonly object gate = new();
    private Task? active, retirement;
    private bool retiring;

    public ConfigurationCurrentInspection Inspection { get; }

    internal ConfigurationCurrentReadScope(SettingsStore store, CancellationToken token, FileStream writer,
        FileStream current, FileStream currentName, ConfigurationCurrentInspection inspection)
    {
        this.store = store;
        this.token = token;
        this.writer = writer;
        this.current = current;
        this.currentName = currentName;
        Inspection = inspection;
    }

    public Task<ConfigurationCurrentInspection> VerifyAsync()
    {
        TaskCompletionSource finished;
        lock (gate)
        {
            if (retiring || active is not null) throw new RecoveryException(RecoveryFailure.Conflict);
            finished = new(TaskCreationOptions.RunContinuationsAsynchronously);
            active = finished.Task;
        }
        return CompleteAsync();

        async Task<ConfigurationCurrentInspection> CompleteAsync()
        {
            try
            {
                await ReadPinnedAsync(current);
                await ReadPinnedAsync(currentName);
                token.ThrowIfCancellationRequested();
                return Inspection;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
            { throw new RecoveryException(RecoveryFailure.Unavailable); }
            finally
            {
                lock (gate)
                {
                    active = null;
                    finished.SetResult();
                }
            }
        }
    }

    private async Task ReadPinnedAsync(FileStream retained)
    {
        token.ThrowIfCancellationRequested();
        store.RecoveryIo?.Invoke(SettingsIoPoint.BeforeRead, token);
        token.ThrowIfCancellationRequested();
        await ConfigurationRestoreScope.CheckBytesAsync(retained, Inspection.Path, Inspection.Revision,
            AppSettings.MaxFileBytes, token, token.ThrowIfCancellationRequested);
        token.ThrowIfCancellationRequested();
        store.RecoveryIo?.Invoke(SettingsIoPoint.AfterRead, token);
        token.ThrowIfCancellationRequested();
    }

    public ValueTask DisposeAsync()
    {
        lock (gate)
        {
            retiring = true;
            retirement ??= RetireAsync(active);
            return new(retirement);
        }
    }

    private async Task RetireAsync(Task? inFlight)
    {
        if (inFlight is not null) await inFlight;
        try { currentName.Dispose(); }
        finally
        {
            try { current.Dispose(); }
            finally { writer.Dispose(); }
        }
    }
}
