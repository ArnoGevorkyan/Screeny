using ScreenTimeTracker.Models;

namespace ScreenTimeTracker.Services;

internal sealed class UsageSliceWriter(DatabaseService database, Action<PersistenceHealthStatus, bool> reportHealth)
{
    private const int MaxPendingSlices = 1024;
    private readonly object _gate = new();
    private readonly Queue<UsageSlice> _pending = new();
    private Task? _drain;
    private bool _paused;
    private PersistenceHealthStatus _health;

    public void Enqueue(UsageSlice slice)
    {
        lock (_gate)
        {
            if (_pending.Count == MaxPendingSlices)
            {
                SetHealth(PersistenceHealthStatus.HistoryIncomplete);
                return;
            }

            _pending.Enqueue(slice);
            StartDrain();
        }
    }

    public bool Reset()
    {
        PauseDrain();
        bool deleted = false;
        try
        {
            deleted = database.WipeDatabase();
            return deleted;
        }
        finally
        {
            lock (_gate)
            {
                if (deleted)
                {
                    _pending.Clear();
                    SetHealth(PersistenceHealthStatus.Healthy);
                }

                _paused = false;
                StartDrain();
            }
        }
    }

    public async Task<bool> WaitForDrainAsync()
    {
        Task? drain;
        lock (_gate) drain = _drain;
        try
        {
            if (drain != null)
                await drain.WaitAsync(TimeSpan.FromSeconds(3)).ConfigureAwait(false);
        }
        catch (TimeoutException)
        {
        }

        lock (_gate) return _pending.Count == 0;
    }

    public (int Pending, bool Lost) Stop()
    {
        PauseDrain();
        lock (_gate) return (_pending.Count, _health == PersistenceHealthStatus.HistoryIncomplete);
    }

    private void PauseDrain()
    {
        Task? drain;
        lock (_gate)
        {
            _paused = true;
            drain = _drain;
        }

        drain?.GetAwaiter().GetResult();
    }

    private void StartDrain()
    {
        if (!_paused && _drain == null && _pending.Count > 0)
            _drain = Task.Run(DrainAsync);
    }

    private async Task DrainAsync()
    {
        while (true)
        {
            UsageSlice slice;
            lock (_gate)
            {
                if (_paused || _pending.Count == 0)
                {
                    _drain = null;
                    return;
                }

                slice = _pending.Peek();
            }

            var result = database.SaveSliceWithResult(slice);
            lock (_gate)
            {
                if (result != PersistenceResult.RetryableFailure)
                    _pending.Dequeue();

                bool drained = _pending.Count == 0;
                var health = (_health, result) switch
                {
                    (PersistenceHealthStatus.HistoryIncomplete, _) or (_, PersistenceResult.FatalFailure)
                        => PersistenceHealthStatus.HistoryIncomplete,
                    (_, PersistenceResult.RetryableFailure) => PersistenceHealthStatus.RetryableIssue,
                    _ => drained ? PersistenceHealthStatus.Healthy : _health
                };
                SetHealth(health, drained);
            }

            if (result == PersistenceResult.RetryableFailure)
                await Task.Delay(500).ConfigureAwait(false);
        }
    }

    private void SetHealth(PersistenceHealthStatus health, bool drained = false)
    {
        if (health == _health && !drained) return;
        _health = health;
        reportHealth(health, drained);
    }
}
