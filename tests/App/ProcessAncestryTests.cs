using System.Diagnostics;
using ResourceManager.App.Infrastructure.Adaptation;
using ResourceManager.App.Infrastructure.ProcessIdentity;

namespace Resource_Manager_APP.Tests;

public sealed class ProcessAncestryTests
{
    [Fact]
    public void FamilyIncludesOnlyVerifiedChainsAndRejectsReusedParentPid()
    {
        var processes = new[] {
            new ProcessAncestryIdentity(1, 0, 100), new(2, 1, 200), new(3, 2, 300),
            new(4, 1, 50), new(5, 4, 400), new(6, 99, 600), new(7, 1, 0),
            new(8, 9, 800), new(9, 8, 800), new(10, 10, 1000)
        }.ToDictionary(item => item.ProcessId);
        var family = ProcessAncestry.FindFamily(processes, new HashSet<int> { 1 });
        Assert.Equal([1, 2, 3], family.Order().ToArray());
    }

    [Fact]
    public void MissingRootCannotClaimDescendantsAndLongChainsDoNotRecurse()
    {
        var processes = Enumerable.Range(1, 5000)
            .Select(id => new ProcessAncestryIdentity(id, id - 1, id))
            .ToDictionary(item => item.ProcessId);
        Assert.Empty(ProcessAncestry.FindFamily(processes, new HashSet<int> { 6000 }));
        Assert.Equal(5000, ProcessAncestry.FindFamily(processes, new HashSet<int> { 1 }).Count);
    }

    [Fact]
    public void NativeUiRootRequiresTheFullInstalledImagePath()
    {
        var expected = Path.Combine(AppContext.BaseDirectory, "ResourceManager.NativeUi.exe");
        Assert.True(ResourceManagerSelfProcessFamilySampler.IsNativeUiImage(expected.ToUpperInvariant(), expected));
        Assert.False(ResourceManagerSelfProcessFamilySampler.IsNativeUiImage(
            Path.Combine(Path.GetTempPath(), "ResourceManager.NativeUi.exe"), expected));
    }

    [Fact]
    public void ActualSameNamedChildIsNotAnInterfaceRootAndRetainsItsCreationIdentity()
    {
        var root = Path.Combine(Path.GetTempPath(), "rm-self-family-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var image = Path.Combine(root, "ResourceManager.NativeUi.exe");
        Process? child = null;
        try
        {
            File.Copy(Path.Combine(Environment.SystemDirectory, "ping.exe"), image);
            var start = new ProcessStartInfo(image) { UseShellExecute = false, CreateNoWindow = true };
            start.ArgumentList.Add("127.0.0.1"); start.ArgumentList.Add("-n"); start.ArgumentList.Add("30");
            child = Process.Start(start)!;
            var rows = ResourceManagerSelfProcessFamilySampler.Capture(Environment.ProcessId);
            var row = Assert.Single(rows, item => item.ProcessId == child.Id);
            Assert.False(row.IsNativeUi); Assert.False(row.IsNativeUiDescendant);
            Assert.Equal(new DateTimeOffset(child.StartTime.ToUniversalTime()), row.StartedAt);
            Assert.True(Assert.Single(rows, item => item.ProcessId == Environment.ProcessId).IsBackend);
        }
        finally
        {
            if (child is not null)
            {
                if (!child.HasExited) { child.Kill(); child.WaitForExit(); }
                child.Dispose();
            }
            File.Delete(image); Directory.Delete(root);
        }
    }
}
