using System.Runtime.CompilerServices;
using Martlet.Gateway;

namespace Martlet.Gateway.Tests;

// NOT AI: controlled permission, readiness and retirement boundaries; no provider/network/model.
internal sealed class ControlledInferenceWorker(GatewayInferenceRoute route) :
    IOllamaGatewayInferenceWorker, IF5GatewayInferenceWorker, IPerceptionGatewayInferenceWorker
{
    private readonly SyntheticInferenceWorker inner = new(route);
    internal readonly TaskCompletionSource Preparing = NewGate();
    internal readonly TaskCompletionSource Disposing = NewGate();
    internal readonly TaskCompletionSource DataPending = NewGate();
    internal readonly TaskCompletionSource RetirementEntered = NewGate();
    internal readonly TaskCompletionSource PermissionDisposed = NewGate();
    internal readonly CancellationTokenSource PermissionRevocation = new();
    internal TaskCompletionSource? PreparationGate { get; set; }
    internal TaskCompletionSource? DataGate { get; set; }
    internal TaskCompletionSource? DisposalGate { get; set; }
    internal TaskCompletionSource? RetirementGate { get; set; }
    internal TaskCompletionSource? PermissionDisposalGate { get; set; }
    internal bool FailPermissionDisposal { get; set; }
    internal int ValidationFailureAt { get; set; }
    internal int RevokeAtValidation { get; set; }
    private int validationChecks;
    internal bool Denied { get; set; }
    internal bool Ready { get; set; } = true;
    internal GatewayInferencePermissionLease? ReusedPermission { get; set; }
    internal GatewayInferencePermissionLease? LastPermission { get; private set; }
    internal GatewayInferenceRequest? LastRequest { get; private set; }
    internal Func<GatewayInferenceEvent, GatewayInferenceEvent>? Transform { get; set; }
    internal int Calls => inner.Calls;
    internal int CancelCalls => inner.CancelCalls;
    public GatewayInferenceRoute Route { get; set; } = route;
    internal static TaskCompletionSource NewGate() => new(TaskCreationOptions.RunContinuationsAsynchronously);

    public async ValueTask<GatewayInferencePermissionLease> AcquirePermissionAsync(
        GatewayInferenceRequest request, GatewayPrincipal principal, CancellationToken cancellationToken)
    {
        LastRequest = request;
        Preparing.TrySetResult();
        if (PreparationGate is { } gate)
            await gate.Task;
        if (Denied)
            throw new GatewayInferenceWorkerException(GatewayInferenceWorkerFailure.PermissionDenied);
        if (!Ready)
            throw new GatewayInferenceWorkerException(GatewayInferenceWorkerFailure.Unavailable);
        return LastPermission = ReusedPermission ?? new Permission(request, principal, this);
    }

    public IAsyncEnumerable<GatewayInferenceEvent> ExecuteAsync(
        GatewayInferenceRequest request, CancellationToken cancellationToken) =>
        new RetiringStream(this, ExecuteCoreAsync(request, cancellationToken));

    private async IAsyncEnumerable<GatewayInferenceEvent> ExecuteCoreAsync(
        GatewayInferenceRequest request, [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        try
        {
            await foreach (var item in inner.ExecuteAsync(request, cancellationToken))
            {
                if (item.Sequence == 1 && DataGate is { } gate)
                {
                    DataPending.TrySetResult();
                    await gate.Task;
                }
                yield return Transform?.Invoke(item) ?? item;
            }
        }
        finally
        {
            Disposing.TrySetResult();
            if (DisposalGate is { } gate)
                await gate.Task;
        }
    }

    public ValueTask<GatewayInferenceCancellationReceipt> CancelAsync(
        GatewayInferenceCancellationRequest request, CancellationToken cancellationToken) =>
        inner.CancelAsync(request, cancellationToken);

    private sealed class RetiringStream(ControlledInferenceWorker owner,
        IAsyncEnumerable<GatewayInferenceEvent> stream) : IAsyncEnumerable<GatewayInferenceEvent>
    {
        public IAsyncEnumerator<GatewayInferenceEvent> GetAsyncEnumerator(CancellationToken cancellationToken = default) =>
            new Enumerator(owner, stream.GetAsyncEnumerator(cancellationToken));

        private sealed class Enumerator(ControlledInferenceWorker owner,
            IAsyncEnumerator<GatewayInferenceEvent> inner) : IAsyncEnumerator<GatewayInferenceEvent>
        {
            public GatewayInferenceEvent Current => inner.Current;
            public ValueTask<bool> MoveNextAsync() => inner.MoveNextAsync();
            public async ValueTask DisposeAsync()
            {
                await inner.DisposeAsync();
                owner.RetirementEntered.TrySetResult();
                if (owner.RetirementGate is { } gate)
                    await gate.Task;
            }
        }
    }

    private sealed class Permission(GatewayInferenceRequest request, GatewayPrincipal principal,
        ControlledInferenceWorker owner) : GatewayInferencePermissionLease(request, principal)
    {
        private bool disposed;
        public override CancellationToken Revoked => owner.PermissionRevocation.Token;
        public override void Validate()
        {
            var validation = Interlocked.Increment(ref owner.validationChecks);
            if (validation == owner.ValidationFailureAt)
                throw new GatewayInferenceWorkerException(GatewayInferenceWorkerFailure.IdentityMismatch);
            if (validation == owner.RevokeAtValidation)
                owner.PermissionRevocation.Cancel();
            if (disposed || owner.Denied)
                throw new GatewayInferenceWorkerException(GatewayInferenceWorkerFailure.PermissionDenied);
        }
        public override async ValueTask DisposeAsync()
        {
            if (owner.PermissionDisposalGate is { } gate)
                await gate.Task;
            disposed = true;
            owner.PermissionDisposed.TrySetResult();
            if (owner.FailPermissionDisposal)
                throw new InvalidOperationException("SECRET_FROM_BAD_ADAPTER");
        }
    }
}
