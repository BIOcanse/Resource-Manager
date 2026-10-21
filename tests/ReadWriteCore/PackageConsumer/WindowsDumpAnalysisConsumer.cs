using ResourceManager.ReadWrite;
using ResourceManager.ReadWrite.Windows.Diagnostics;

internal static class WindowsDumpAnalysisConsumer
{
    // Central package consumer runner can invoke RunPassive without installed dependencies.
    internal static async Task RunPassive()
    {
        const string firstIdentity = "package-consumer|dump-first";
        const string secondIdentity = "package-consumer|dump-second";
        using var owner = new WindowsDumpAnalysisOwner(firstIdentity);
        using var second = new WindowsDumpAnalysisOwner(secondIdentity);
        var backend = owner.CreateBackend();
        var catalog = await backend.DiscoverAsync(default);
        if (catalog.Fields.Length != 5 || owner.ReadCurrent().Status != WindowsDumpAnalysisStatus.NotAnalyzed)
            throw new InvalidOperationException("Passive dump analysis catalog must expose five null fields.");
        foreach (var field in catalog.Fields)
            if (field.Field.DeviceIdentity != firstIdentity
                || !field.Read.Dependencies.SequenceEqual(new[] { "resource-manager.native.dump-analysis-helper", "windows.dbgeng" })
                || backend.ReadCached(field.Field).Status != FieldObservationStatus.Unavailable)
                throw new InvalidOperationException("Passive dump analysis cannot fabricate data.");
        var composite = new CompositeReadWriteBackend([backend, second.CreateBackend()]);
        var combined = await composite.DiscoverAsync(default);
        if (combined.Fields.Length != 10 || combined.GetFields(firstIdentity).Length != 5 || combined.GetFields(secondIdentity).Length != 5
            || second.ReadInformation().Devices.Single().DeviceIdentity != secondIdentity)
            throw new InvalidOperationException("Distinct caller-assigned dump resource identities must compose without collisions.");
        using var borrow = owner.BorrowSubscriptionCapture();
        _ = borrow.Bind([catalog.Fields[0].Field]);
    }
    // Actual independent package API entry point: explicit EXISTING dump and installed helper/engine.
    // The central runner supplies authorized paths; this function never creates or attaches a dump.
    internal static async Task<WindowsDumpAnalysisResult> AnalyzeExisting(string resourceIdentity, WindowsDumpAnalysisRequest request)
    {
        using var owner = new WindowsDumpAnalysisOwner(resourceIdentity);
        using var borrow = owner.BorrowSubscriptionCapture();
        using var publication = borrow.Publications.Subscribe([WindowsDumpAnalysisOwner.CollectionItemId]);
        var result = await owner.AnalyzeAsync(request);
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(2));
        await publication.WaitAsync(deadline.Token);
        if (!ReferenceEquals(result, owner.ReadCurrent()) || !ReferenceEquals(result.Information, owner.ReadInformation())
            || result.Information.Devices.Single().DeviceIdentity != resourceIdentity)
            throw new InvalidOperationException("Analysis and field backend must share the original result owner.");
        return result;
    }
}
