namespace Martlet.Desktop;

/// <summary>Changes the owner makes from the main window (a job's route, a voice, a character, who does lip-sync, ...) take
/// turns: each reads, changes and saves the settings while it holds its turn, so two never overwrite each other's save. A
/// change that finds another one saving waits for it, in order, instead of being refused. Only that read-change-save holds
/// a turn: installs, downloads, host runs and anything else that takes long happen outside it, so they never hold up other
/// changes. Background work (sharing settings, following another computer's choice) uses <see cref="TryTake"/> and simply
/// tries again later.</summary>
internal sealed class ChangeTurns
{
    private readonly SemaphoreSlim gate = new(1, 1);

    /// <summary>A change holds its turn now.</summary>
    internal bool Busy => gate.CurrentCount == 0;

    /// <summary>The turn when it is free now, otherwise null (background work tries again later).</summary>
    internal Turn? TryTake() => gate.Wait(0) ? new(gate) : null;

    /// <summary>Waits for the turn (after every change that asked before) and takes it.</summary>
    internal async Task<Turn> TakeAsync(CancellationToken token)
    {
        await gate.WaitAsync(token);
        return new(gate);
    }

    /// <summary>A held turn; disposing it (once, or again harmlessly) lets the next change go.</summary>
    internal sealed class Turn : IDisposable
    {
        private SemaphoreSlim? gate;

        internal Turn(SemaphoreSlim gate) => this.gate = gate;

        public void Dispose() => Interlocked.Exchange(ref gate, null)?.Release();
    }
}
