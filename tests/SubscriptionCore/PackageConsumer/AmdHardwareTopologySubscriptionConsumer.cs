using System.Threading.Channels;
using ResourceManager.ReadWrite.Windows.Cpu.Snapshots;

internal static class AmdHardwareTopologySubscriptionConsumer
{
    internal static async Task RunRecordedAsync()
    {
        var captures = 0; var overlapping = false;
        using var owner = new CpuTopologyCaptureOwner(() =>
        {
            captures++;
            return new(null, AmdHardwareTopologyConsumer.Information(overlapping), DateTimeOffset.UtcNow);
        }, static () => { });
        var backend = owner.CreateBackend(static _ => new(true,null,[]));
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        using var publication = owner.SubscribePublications();
        var pending = publication.WaitAsync(timeout.Token).AsTask();
        if(captures != 0 || pending.IsCompleted) throw new InvalidDataException("CPU publication must remain passive");
        await backend.DiscoverAsync(timeout.Token);
        await pending;
        var field = backend.Catalog.Fields.Single(static row => row.Field.FieldName == "cpu_amd_ccd_core_membership").Field;
        AmdHardwareTopologyConsumer.AssertMembership(backend.ReadCached(field));
        pending = publication.WaitAsync(timeout.Token).AsTask();
        if(pending.IsCompleted) throw new InvalidDataException("CPU publication unexpectedly replays stale notification");
        await backend.CollectAsync([field],timeout.Token);
        await pending;
        AmdHardwareTopologyConsumer.AssertMembership(backend.ReadCached(field));
        if(captures != 2) throw new InvalidDataException("CPU publication caused an extra capture");
        overlapping = true;
        pending = publication.WaitAsync(timeout.Token).AsTask();
        await backend.CollectAsync([field],timeout.Token);
        await pending;
        if(backend.ReadCached(field).Value is not null || captures != 3)
            throw new InvalidDataException("Overlapping domains were published as valid CPU membership");
        overlapping = false;
        pending = publication.WaitAsync(timeout.Token).AsTask();
        await backend.CollectAsync([field],timeout.Token);
        await pending;
        AmdHardwareTopologyConsumer.AssertMembership(backend.ReadCached(field));
        if(captures != 4) throw new InvalidDataException("CPU topology recovery caused an extra capture");
        publication.Dispose();
        try { await publication.WaitAsync(timeout.Token); throw new InvalidDataException("Disposed CPU publication delivered a value"); }
        catch(ChannelClosedException) { }
        Console.WriteLine("[AMD-HARDWARE-TOPOLOGY-SUBSCRIPTION-PACKAGE] original CPU publication and same Current through installed transitive package; recorded only. Host native subscription is qualified separately.");
    }
}
