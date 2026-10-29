using ResourceManager.Shared.Packages;

namespace Resource_Manager_APP.Tests;

public sealed class UninstallArchiveRetryTests
{
    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    public void RetryPreservesExistingArchiveOrRejectsAmbiguousArchives(int count)
    {
        var fixture = Directory.CreateDirectory(Path.Combine(Path.GetTempPath(), "rm-uninstall-retry-test-" + Guid.NewGuid().ToString("N"))).FullName;
        try
        {
            var root = Directory.CreateDirectory(Path.Combine(fixture, "ResourceManager")).FullName;
            var source = Directory.CreateDirectory(Path.Combine(fixture, "source", "Config")).Parent!.FullName;
            File.WriteAllText(Path.Combine(source, "Config", "settings"), "retained data");
            var archives = Enumerable.Range(0, count).Select(index => Path.Combine(root, $"ResourceManager-user-data-{index}.zip")).ToArray();
            foreach (var archive in archives) UserDataArchive.Create(source, archive);
            var plan = new UninstallationPlan(root, "backend", Path.Combine(fixture, "manager"));
            if (count == 1) Assert.Equal(archives[0], UninstallationWorkflow.Run(plan, true, new Runtime()));
            else Assert.Throws<InvalidDataException>(() => UninstallationWorkflow.Run(plan, true, new Runtime()));
            Assert.Equal(count, Directory.GetFiles(root).Length);
            foreach (var archive in archives) UserDataArchive.Verify(archive);
        }
        finally { Directory.Delete(fixture, true); }
    }
    private sealed class Runtime : IUninstallationRuntime
    {
        public void RequireOwned(UninstallationPlan plan) { }
        public void StopProduct(UninstallationPlan plan) { }
        public void RemoveLaunchInterception(UninstallationPlan plan) { }
        public void RemoveFrontendLogonTasks(UninstallationPlan plan) { }
        public void RemoveService(UninstallationPlan plan) { }
        public void RemoveRegistration(UninstallationPlan plan) { }
        public void ProtectArchive(string archive) { }
    }
}
