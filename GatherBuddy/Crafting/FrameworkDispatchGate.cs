using System;
using System.Threading;
using System.Threading.Tasks;

namespace GatherBuddy.Crafting;

/// <summary>
/// One-shot state gate for framework callbacks. The callback must claim the
/// gate before touching game state; timeout/cancellation closes the gate so a
/// callback that arrives later cannot mutate or complete an old request.
/// </summary>
internal sealed class FrameworkDispatchGate<T>
{
    private static long _nextGeneration;
    private static long _activeGeneration;
    private readonly object _sync = new();
    private readonly TaskCompletionSource<T> _completion = new(
        TaskCreationOptions.RunContinuationsAsynchronously);
    private DispatchState _state;

    public FrameworkDispatchGate()
    {
        Generation = Interlocked.Increment(ref _nextGeneration);
        Interlocked.Exchange(ref _activeGeneration, Generation);
    }

    public long Generation { get; }
    public Task<T> Completion => _completion.Task;

    public bool TryClaim(CancellationToken cancellationToken = default)
    {
        lock (_sync)
        {
            if (cancellationToken.IsCancellationRequested
                || _state != DispatchState.Pending
                || Interlocked.Read(ref _activeGeneration) != Generation)
                return false;

            _state = DispatchState.Claimed;
            return true;
        }
    }

    public bool TryComplete(T result, CancellationToken cancellationToken = default)
    {
        lock (_sync)
        {
            if (cancellationToken.IsCancellationRequested || _state != DispatchState.Claimed)
                return false;

            if (Interlocked.Read(ref _activeGeneration) != Generation)
            {
                _state = DispatchState.Completed;
                _completion.TrySetCanceled();
                return false;
            }

            _state = DispatchState.Completed;
            _completion.TrySetResult(result);
            return true;
        }
    }

    public bool TryFail(Exception exception)
    {
        ArgumentNullException.ThrowIfNull(exception);
        lock (_sync)
        {
            if (_state != DispatchState.Claimed)
                return false;

            if (Interlocked.Read(ref _activeGeneration) != Generation)
            {
                _state = DispatchState.Completed;
                _completion.TrySetCanceled();
                return false;
            }

            _state = DispatchState.Completed;
            _completion.TrySetException(exception);
            return true;
        }
    }

    public bool TryCancel(CancellationToken cancellationToken = default)
    {
        lock (_sync)
        {
            if (_state == DispatchState.Completed)
                return false;

            // A claimed callback may finish its synchronous work, but it must
            // not publish a result after its caller has been canceled/timed out.
            _state = DispatchState.Completed;
            if (cancellationToken.CanBeCanceled)
                _completion.TrySetCanceled(cancellationToken);
            else
                _completion.TrySetCanceled();
            return true;
        }
    }

    private enum DispatchState
    {
        Pending,
        Claimed,
        Completed,
    }
}
