using Microsoft.Extensions.Logging;

namespace ResourceManager.Shared.BusinessBlocks;

/// <summary>One serial decision loop for one parent's immediate children. It owns no execution policy.</summary>
internal sealed class BusinessBlockDecisionNode : IDisposable
{
    private readonly object sync = new();
    private readonly IReadOnlyList<string> children;
    private readonly Func<BusinessBlockDecisionInput, IReadOnlyCollection<string>> decide;
    private readonly BusinessBlockFactAuthority authority;
    private readonly BusinessBlockController control;
    private readonly Func<string, IBusinessBlock> create;
    private readonly Func<string, BusinessBlockDecisionNode?> childNode;
    private readonly ILogger logger;
    private readonly int historyLimit;
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
    public string ParentId { get; }
    public IReadOnlyList<string> FactKeys { get; }

    public BusinessBlockDecisionNode(string parentId, IReadOnlyList<string> children, IReadOnlyList<string> factKeys,
        Func<BusinessBlockDecisionInput, IReadOnlyCollection<string>> decide, BusinessBlockFactAuthority authority,
        BusinessBlockController control, Func<string, IBusinessBlock> create,
        Func<string, BusinessBlockDecisionNode?> childNode, ILogger logger, int historyLimit)
    {
        ParentId = parentId; this.children = children; FactKeys = factKeys; this.decide = decide;
        this.authority = authority; this.control = control; this.create = create; this.childNode = childNode;
        this.logger = logger; this.historyLimit = historyLimit;
        permitted = parentId == BusinessBlockGraph.Program;
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
    public bool Permitted { get { lock (sync) return permitted; } }
    public bool SetPermitted(bool value, BusinessBlockReason why)
    {
        lock (sync)
        {
            if (permitted == value) return false;
            permitted = value;
            // A parent relies only on a summary reported after this change.
            drained = false;
            PublishDrained(false, why);
        }
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
                // A malformed rule is reported to its own fact publisher. Block errors never arrive here.
                logger.LogError(error, "Decision node {ParentId} could not evaluate its rule", ParentId);
                completion.TrySetException(error);
            }
            // Immediate returns can trigger another decision; give fact publishers a chance to run.
            await Task.Yield();
        }
    }

    private void Align(bool permit, string cause, BusinessBlockReason why)
    {
        var facts = FactKeys.ToDictionary(key => key, authority.Read, StringComparer.Ordinal);
        var actual = children.ToDictionary(id => id, control.ReadState, StringComparer.Ordinal);
        // The owner's state is read when deciding, never kept: children run only while the owner runs.
        var active = permit && (ParentId == BusinessBlockGraph.Program || control.ReadState(ParentId) == BusinessBlockState.Running);
        var wanted = active && !(ParentId == BusinessBlockGraph.Program && facts[BusinessBlockScheduler.ShutdownFact].Value)
            ? decide(new(facts, actual)).ToHashSet(StringComparer.Ordinal) : new HashSet<string>(StringComparer.Ordinal);
        foreach (var id in wanted)
            if (!actual.ContainsKey(id)) throw new InvalidOperationException($"Decision node {ParentId} cannot control non-child {id}.");
        wanted.RemoveWhere(id => !facts[BusinessBlockScheduler.EnabledFact(id)].Value);
        var decision = new BusinessBlockDecision(++sequence, ParentId, cause, why,
            children.Where(wanted.Contains).ToArray(), facts.Values.ToArray());
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
            logger.LogInformation("Business block decision {ParentId}/{Sequence}: fact={Fact}, kind={Kind}, owner={Owner}, config={Version}; children={Children}; facts={Facts}",
                ParentId, decision.Sequence, cause, why.Kind, why.Owner, why.PlanVersion, string.Join(",", decision.ShouldRun),
                string.Join(";", decision.Facts.Select(fact => $"{fact.Key}={fact.Value}[{string.Join(",", fact.Reasons.Select(item => $"{item.Kind}/{item.Owner}/{item.ConfigurationOrDependency}/{item.PlanVersion}"))}]")));

        // Siblings have no order. A child with its own node is stopped only after the handshake:
        // its permit was already revoked before this decision read its summary, and the summary says stopped.
        foreach (var id in children)
        {
            if (wanted.Contains(id)) continue;
            var branch = childNode(id);
            var intentChanged = branch?.SetPermitted(false, why) ?? false;
            if (actual[id] == BusinessBlockState.Stopped) continue;
            if (branch is not null && (intentChanged || !facts[BusinessBlockScheduler.ChildrenStoppedFact(id)].Value)) continue;
            if (actual[id] != BusinessBlockState.Stopping) _ = control.Stop(id);
        }
        // A stopped child with its own node is restarted only after the same handshake.
        foreach (var id in children)
        {
            if (!wanted.Contains(id)) continue;
            var branch = childNode(id);
            if (actual[id] == BusinessBlockState.Stopped && branch is not null
                && (branch.SetPermitted(false, why) || !facts[BusinessBlockScheduler.ChildrenStoppedFact(id)].Value)) continue;
            branch?.SetPermitted(true, why);
            if (actual[id] == BusinessBlockState.Stopped)
                control.Start(id, context => create(id).RunAsync(context));
        }
        // This branch is stopped when every child is stopped and every child with its own node completed the handshake.
        var nowDrained = children.All(id => control.ReadState(id) == BusinessBlockState.Stopped
            && (childNode(id) is not { } branch || (!branch.Permitted && authority.Read(BusinessBlockScheduler.ChildrenStoppedFact(id)).Value)));
        lock (sync)
        {
            if (nowDrained != drained)
            {
                drained = nowDrained;
                PublishDrained(nowDrained, why);
            }
        }
    }

    private void PublishDrained(bool value, BusinessBlockReason why)
    {
        if (ParentId != BusinessBlockGraph.Program)
            _ = authority.Publish(BusinessBlockScheduler.ChildrenStoppedFact(ParentId), value, why);
    }

    public IReadOnlyList<BusinessBlockDecision> ReadDecisions() { lock (sync) return history.ToArray(); }
    public BusinessBlockDecision? ReadCurrentDecision() { lock (sync) return currentDecision; }
    public void Dispose() { lock (sync) { disposed = true; pending?.TrySetResult(); } }
}
