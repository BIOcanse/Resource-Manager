using Microsoft.Extensions.Logging;
using System.Collections.Concurrent;

namespace ResourceManager.Shared.BusinessBlocks;

/// <summary>Generic hands: invoke an entry and deliver stop notification.</summary>
public sealed class BusinessBlockController(ILogger<BusinessBlockController> logger, int historyLimit, Func<int, string?> nameLookup)
{
    private readonly ConcurrentDictionary<int, Cell> cells = new();
    private readonly int retainedLimit = historyLimit >= 0 ? historyLimit : throw new ArgumentOutOfRangeException(nameof(historyLimit));
    private readonly BusinessBlockControlTransition?[] history = new BusinessBlockControlTransition?[historyLimit >= 0
        ? historyLimit : throw new ArgumentOutOfRangeException(nameof(historyLimit))];
    private long sequence;
    public event Action<int, BusinessBlockState>? StateChanged;

    private Cell GetCell(int id) => cells.GetOrAdd(id, static _ => new());

    public void Start(int id, Func<BusinessBlockRunContext, Task> entry)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(id, 1);
        ArgumentNullException.ThrowIfNull(entry);
        var cell = GetCell(id);
        Run run;
        BusinessBlockControlTransition transition;
        lock (cell.Sync)
        {
            if (cell.Current is { } previous)
            {
                if (previous.State != BusinessBlockState.Stopped) return;
                previous.Stop.Dispose();
            }
            run = new();
            cell.Current = run;
            transition = Record(id, run, BusinessBlockState.Starting);
        }
        Publish(transition);
        _ = Task.Run(() => ExecuteAsync(id, cell, run, entry));
    }

    private async Task ExecuteAsync(int id, Cell cell, Run run, Func<BusinessBlockRunContext, Task> entry)
    {
        try { await entry(new(run.Stop.Token, () => ReportRunning(id, cell, run))).ConfigureAwait(false); }
        catch (Exception) { /* An exceptional return is still a return; the block owns its diagnostics. */ }
        BusinessBlockControlTransition transition;
        lock (cell.Sync)
        {
            transition = Record(id, run, BusinessBlockState.Stopped);
        }
        Publish(transition);
    }

    private void ReportRunning(int id, Cell cell, Run run)
    {
        BusinessBlockControlTransition? transition = null;
        lock (cell.Sync)
            // A late startup report cannot undo an already dispatched stop.
            if (run.State == BusinessBlockState.Starting)
            {
                transition = Record(id, run, BusinessBlockState.Running);
            }
        if (transition is not null) Publish(transition);
    }

    public Task Stop(int id)
    {
        Task notification;
        if (!cells.TryGetValue(id, out var cell)) return Task.CompletedTask;
        BusinessBlockControlTransition transition;
        lock (cell.Sync)
        {
            if (cell.Current is not { } run || run.State == BusinessBlockState.Stopped)
                return Task.CompletedTask;
            if (run.State == BusinessBlockState.Stopping) return run.Notification;
            transition = Record(id, run, BusinessBlockState.Stopping);
            run.Notification = run.Stop.CancelAsync();
            notification = run.Notification;
        }
        Publish(transition);
        return notification;
    }

    public BusinessBlockControlSnapshot Read()
    {
        var states = cells.ToDictionary(pair => pair.Key, pair => pair.Value.Current?.State ?? BusinessBlockState.Stopped);
        var retained = new List<BusinessBlockControlTransition>(retainedLimit);
        for (var index = 0; index < history.Length; index++)
            if (Volatile.Read(ref history[index]) is { } transition) retained.Add(transition);
        return new(states, retained.OrderBy(item => item.Sequence).ToArray());
    }
    public BusinessBlockState ReadState(int id)
    {
        return cells.TryGetValue(id, out var cell) ? cell.Current?.State ?? BusinessBlockState.Stopped : BusinessBlockState.Stopped;
    }

    private BusinessBlockControlTransition Record(int id, Run run, BusinessBlockState to)
    {
        var from = run.State;
        run.State = to;
        var transition = new BusinessBlockControlTransition(Interlocked.Increment(ref sequence), DateTimeOffset.UtcNow, id, from, to);
        if (retainedLimit > 0)
        {
            var index = (int)((transition.Sequence - 1) % retainedLimit);
            // A delayed writer cannot replace a newer turn of this bounded slot.
            while (true)
            {
                var previous = Volatile.Read(ref history[index]);
                if (previous is not null && previous.Sequence > transition.Sequence) break;
                if (ReferenceEquals(Interlocked.CompareExchange(ref history[index], transition, previous), previous)) break;
            }
        }
        return transition;
    }
    private void Publish(BusinessBlockControlTransition transition)
    {
        // Logging and subscribers never hold a controller state or history lock.
        if (nameLookup(transition.BlockId) is { } name)
            logger.LogInformation("Business block {BlockId} ({BlockName}): {From} -> {To}", transition.BlockId, name, transition.From, transition.To);
        else
            logger.LogInformation("Business block {BlockId}: {From} -> {To}", transition.BlockId, transition.From, transition.To);
        StateChanged?.Invoke(transition.BlockId, transition.To);
    }
    private sealed class Cell
    {
        public object Sync { get; } = new();
        public volatile Run? Current;
    }
    private sealed class Run
    {
        public CancellationTokenSource Stop { get; } = new();
        public Task Notification { get; set; } = Task.CompletedTask;
        public volatile BusinessBlockState State = BusinessBlockState.Stopped;
    }
}
