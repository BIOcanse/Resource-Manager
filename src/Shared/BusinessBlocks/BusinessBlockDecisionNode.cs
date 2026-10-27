using Microsoft.Extensions.Logging;
using ResourceManager.Shared.BusinessBlocks.State;

namespace ResourceManager.Shared.BusinessBlocks;

/// <summary>Transitional managed decision loop for the window process and isolated fixtures. Claude's control session removes it and the selection seam at the end of 5d.</summary>
internal sealed class BusinessBlockDecisionNode : IDisposable
{
    private readonly object sync = new();
    private readonly IReadOnlyList<int> children;
    private readonly Func<BusinessBlockDecisionInput, IReadOnlyCollection<int>> decide;
    private readonly StateAuthority authority;
    private readonly BusinessBlockController control;
    private readonly Func<int, IBusinessBlock> create;
    private readonly Func<int, BusinessBlockDecisionNode?> childNode;
    private readonly ILogger logger;
    private readonly int historyLimit;
    private readonly int program;
    private readonly int shutdownItemUid;
    private readonly Action<BusinessBlockReason> wakeParent;
    private readonly Func<int, string?> nameLookup;
    private readonly Queue<BusinessBlockDecision> history = new();
    private BusinessBlockDecision? currentDecision;
    private TaskCompletionSource? pending;
    private bool processing;
    private bool disposed;
    private bool permitted;
    private bool drained = true;
    private long sequence;
    private string transition = "initial";
    private BusinessBlockReason reason = new(BusinessBlockRequestKind.Configuration, "program", "initial", 0);
    public int ParentId { get; }
    public IReadOnlyList<StateItemKey> ItemKeys { get; }

    public BusinessBlockDecisionNode(int parentId, IReadOnlyList<int> children, IReadOnlyList<StateItemKey> itemKeys,
        Func<BusinessBlockDecisionInput, IReadOnlyCollection<int>> decide, StateAuthority authority,
        BusinessBlockController control, Func<int, IBusinessBlock> create,
        Func<int, BusinessBlockDecisionNode?> childNode, Action<BusinessBlockReason> wakeParent, ILogger logger, int historyLimit, int program, int shutdownItemUid, Func<int, string?> nameLookup)
    {
        ParentId = parentId; this.children = children; ItemKeys = itemKeys; this.decide = decide;
        this.authority = authority; this.control = control; this.create = create; this.childNode = childNode;
        this.logger = logger; this.historyLimit = historyLimit; this.program = program; this.nameLookup = nameLookup; this.shutdownItemUid = shutdownItemUid; this.wakeParent = wakeParent;
        permitted = parentId == program;
    }

    public Task Wake(string cause, BusinessBlockReason why)
    {
        lock (sync)
        {
            if (disposed) return Task.CompletedTask;
            transition = cause; reason = why;
            pending ??= new(TaskCreationOptions.RunContinuationsAsynchronously);
            var completion = pending.Task;
            if (!processing) { processing = true; _ = Task.Run(ProcessAsync); }
            return completion;
        }
    }

    /// <summary>Written only by the parent's own decision loop.</summary>
    public bool Permitted => Volatile.Read(ref permitted);
    public bool Drained => Volatile.Read(ref drained);
    public bool SetPermitted(bool value, BusinessBlockReason why)
    {
        lock (sync)
        {
            if (permitted == value) return false;
            permitted = value;
            // A parent relies only on a summary reported after this change.
            drained = false;

        }
        PublishDrained(why);
        _ = Wake("parent-intent", why);
        return true;
    }

    private async Task ProcessAsync()
    {
        while (true)
        {
            TaskCompletionSource completion;
            BusinessBlockReason why;
            string cause;
            bool permit;
            lock (sync)
            {
                if (pending is null || disposed) { processing = false; pending?.TrySetResult(); return; }
                completion = pending; pending = null;
                why = reason; cause = transition; permit = permitted;
            }
            try { Align(permit, cause, why); completion.TrySetResult(); }
            catch (Exception error)
            {
                // A malformed rule is reported to the publisher awaiting this round. Block errors never arrive here.
                logger.LogError(error, "Decision node {ParentId}{ParentName} could not evaluate its rule", ParentId, NameSuffix(ParentId));
                completion.TrySetException(error);
            }
            // Immediate returns can trigger another decision; give publishers a chance to run.
            await Task.Yield();
        }
    }

    private void Align(bool permit, string cause, BusinessBlockReason why)
    {
        var publications = ItemKeys.ToDictionary(key => key, authority.ReadPublication);
        var summaries = children.ToDictionary(id => id, id => childNode(id)?.Drained ?? true);
        var actual = children.ToDictionary(id => id, control.ReadState);
        var input = new BusinessBlockDecisionInput(authority, publications, actual);
        // The owner's state is read when deciding, never kept: children run only while the owner runs.
        var active = permit && (ParentId == program || control.ReadState(ParentId) == BusinessBlockState.Running);
        var wanted = active && !(ParentId == program && input.Read<bool>(shutdownItemUid).GetValueOrDefault(false))
            ? decide(input).ToHashSet() : new HashSet<int>();
        foreach (var id in wanted)
            if (!actual.ContainsKey(id)) throw new InvalidOperationException($"Decision node {ParentId} cannot control non-child {id}.");

        var decision = new BusinessBlockDecision(++sequence, ParentId, cause, why,
            children.Where(wanted.Contains).ToArray(), ItemKeys.Select(key => authority.Snapshot(key, publications[key], nameLookup)).ToArray());
        lock (sync)
        {
            currentDecision = decision;
            if (historyLimit > 0)
            {
                if (history.Count == historyLimit) history.Dequeue();
                history.Enqueue(decision);
            }
        }
        if (logger.IsEnabled(LogLevel.Information))
            logger.LogInformation("Business block decision {ParentId}{ParentName}/{Sequence}: cause={Cause}, kind={Kind}, owner={Owner}, config={Version}; children={Children}; items={Items}",
                ParentId, NameSuffix(ParentId), decision.Sequence, cause, why.Kind, why.Owner, why.PlanVersion, string.Join(",", decision.ShouldRun.Select(id => id.ToString(System.Globalization.CultureInfo.InvariantCulture) + NameSuffix(id))),
                string.Join(";", decision.Items.Select(item => $"{item.ItemUid}{NameSuffix(item.ItemUid)}"
                    + (item.BlockUid is { } block ? $"/{block}{NameSuffix(block)}" : "")
                    + $"={item.Value?.ToString() ?? item.NoValueReason?.ToString()}")));

        // Siblings have no order. A child with its own node is stopped only after the handshake:
        // its permit was already revoked before this decision read its summary, and the summary says stopped.
        foreach (var id in children)
        {
            if (wanted.Contains(id)) continue;
            var branch = childNode(id);
            var intentChanged = branch?.SetPermitted(false, why) ?? false;
            if (actual[id] == BusinessBlockState.Stopped) continue;
            if (branch is not null && (intentChanged || !summaries[id])) continue;
            if (actual[id] != BusinessBlockState.Stopping) _ = control.Stop(id);
        }
        // A stopped child with its own node is restarted only after the same handshake.
        foreach (var id in children)
        {
            if (!wanted.Contains(id)) continue;
            var branch = childNode(id);
            if (actual[id] == BusinessBlockState.Stopped && branch is not null
                && (branch.SetPermitted(false, why) || !summaries[id])) continue;
            branch?.SetPermitted(true, why);
            if (actual[id] == BusinessBlockState.Stopped)
                control.Start(id, context => create(id).RunAsync(context));
        }
        // This branch is stopped when every child is stopped and every child with its own node completed the handshake.
        var nowDrained = children.All(id => control.ReadState(id) == BusinessBlockState.Stopped
            && (childNode(id) is not { } branch || (!branch.Permitted && branch.Drained)));
        bool changed;
        lock (sync) { changed = nowDrained != drained; drained = nowDrained; }
        if (changed) PublishDrained(why);
    }

    private string NameSuffix(int id) => nameLookup(id) is { } name ? $" ({name})" : "";

    private void PublishDrained(BusinessBlockReason why)
    {
        if (ParentId != program)
            wakeParent(why);
    }

    public IReadOnlyList<BusinessBlockDecision> ReadDecisions() { lock (sync) return history.ToArray(); }
    public BusinessBlockDecision? ReadCurrentDecision() { lock (sync) return currentDecision; }
    public void Dispose() { lock (sync) { disposed = true; pending?.TrySetResult(); } }
}
