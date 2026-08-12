using System.Threading.Tasks;

namespace GatherBuddy.Crafting;

/// <summary>
/// Serializes acquisition generations across cancellation/restart. A new run
/// cannot begin until the previous run has released its captured resources and
/// completed its drain gate.
/// </summary>
internal sealed class AcquisitionRunGenerationGate
{
    private readonly object _sync = new();
    private long _nextGeneration;
    private long _activeGeneration;
    private long _drainingGeneration;
    private Task _drainGate = Task.CompletedTask;
    private TaskCompletionSource<bool>? _drainCompletion;

    public bool TryBeginRun(out long generation)
    {
        lock (_sync)
        {
            if (_activeGeneration != 0 || !_drainGate.IsCompleted)
            {
                generation = 0;
                return false;
            }

            generation = checked(++_nextGeneration);
            _activeGeneration = generation;
            return true;
        }
    }

    public bool IsReadyToBegin()
    {
        lock (_sync)
            return _activeGeneration == 0 && _drainGate.IsCompleted;
    }

    public Task DrainTask
    {
        get
        {
            lock (_sync)
                return _drainGate;
        }
    }

    public bool TryBeginDrain(
        long generation,
        out TaskCompletionSource<bool>? completion)
    {
        lock (_sync)
        {
            if (_activeGeneration != generation || _drainCompletion != null)
            {
                completion = null;
                return false;
            }

            completion = new TaskCompletionSource<bool>(
                TaskCreationOptions.RunContinuationsAsynchronously);
            _drainingGeneration = generation;
            _drainCompletion = completion;
            _drainGate = completion.Task;
            return true;
        }
    }

    public bool IsCurrent(long generation)
    {
        lock (_sync)
            return _activeGeneration == generation;
    }

    public bool TryReleaseActive(long generation)
    {
        lock (_sync)
        {
            if (_activeGeneration != generation)
                return false;

            _activeGeneration = 0;
            return true;
        }
    }

    public bool IsDrainPending()
    {
        lock (_sync)
            return _drainCompletion != null;
    }

    public bool TryCompleteDrain(
        long generation,
        TaskCompletionSource<bool> completion)
    {
        lock (_sync)
        {
            if (_drainingGeneration != generation
                || !ReferenceEquals(_drainCompletion, completion))
            {
                return false;
            }

            if (_activeGeneration == generation)
                _activeGeneration = 0;
            _drainingGeneration = 0;
            _drainCompletion = null;
            // RunContinuationsAsynchronously keeps callbacks out of this lock;
            // the gate becomes visible only after captured cleanup is done.
            completion.TrySetResult(true);
            return true;
        }
    }
}
