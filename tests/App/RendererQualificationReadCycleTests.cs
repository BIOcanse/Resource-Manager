using ResourceManager.App.Application.GpuPlacement;
using ResourceManager.App.Domain.GpuPlacement;
using ResourceManager.App.Infrastructure.GpuPlacement.External;
using ResourceManager.ReadWrite.ProcessFields;
using ResourceManager.ReadWrite.Windows.Processes;

namespace Resource_Manager_APP.Tests;

public sealed class RendererQualificationReadCycleTests
{
    private const ulong Start = 133_900_000_000_000_000;
    private static readonly WindowsGpuRendererConfirmation.AdapterClient[] D3D11 = [new(1, 0, 11)];

    [Fact]
    public void CandidatesShareOneHandleAndOneNamesOnlyModuleReadAcrossBothChecksInOneCycle()
    {
        var api = new Api();
        var candidates = Enumerable.Range(100, 5).Select(pid => api.Add(pid)).ToArray();
        using (var reads = new RendererQualificationReadCycle(api))
        {
            foreach (var candidate in candidates)
            {
                Assert.Null(WindowsExternalGpuPlacementRuntime.ReadRendererPlan(candidate, D3D11, true, true, reads));
                Assert.Null(ChromiumGpuProcessIdentity.Read(candidate, GpuGraphicsApi.D3D11, reads));
                Assert.Null(QtQuickGpuProcessIdentity.Read(candidate, reads));
                Assert.True(WindowsExternalGpuPlacementRuntime.HasNoRendererMarkers(candidate, reads));
            }
            Assert.Equal(5, api.Opens);
            Assert.Equal(5, api.IdentitiesRead);
            Assert.Equal(5, api.ModuleReads);
            Assert.Equal(5, reads.ModuleReads);
            Assert.Equal(5, api.ArgumentReads);
            Assert.Empty(api.PathsRead);
            Assert.Empty(api.VersionsRead);
            Assert.Empty(api.HashesRead);
            Assert.Empty(api.SizesRead);
            Assert.Equal(0, api.MemoryReads);
        }
        Assert.Equal(5, api.Closes);
        Assert.Empty(api.Handles);
    }

    [Fact]
    public void ChromiumChildrenShareAnIdentityBoundParentWithoutRequiringAngleLibraries()
    {
        var api = new Api();
        var parent = api.Add(90, Start - 10);
        var children = new[] { api.Add(100), api.Add(101) };
        foreach (var child in children) { api.Arguments[child.ProcessId] = [child.ExecutablePath, "--type=gpu-process"]; api.Parents[child.ProcessId] = parent.ProcessId; }
        using (var reads = new RendererQualificationReadCycle(api))
        {
            foreach (var child in children)
            {
                var plan = WindowsExternalGpuPlacementRuntime.ReadRendererPlan(child, D3D11, true, true, reads);
                Assert.NotNull(plan);
                Assert.Equal(parent.ProcessId, plan.Root.ProcessId);
                Assert.Equal(parent.ProcessStartKey, plan.Root.ProcessStartKey);
                Assert.Equal(parent.ExecutablePath, plan.Root.ExecutablePath);
                Assert.Equal(child, plan.GpuProcess);
                Assert.Equal(ExternalGpuRenderer.ChromiumAngle, plan.Renderer);
            }
            Assert.Equal(3, api.Opens);
            Assert.Equal(0, api.ModuleReads); // Chromium uses command-line roles and kernel API, not DLL guesses.
        }
        Assert.Equal(3, api.Closes);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void QualifiedQtReadsOnlyDecidingModuleDetailsAndReverifiesEveryNewCycle(bool vulkan)
    {
        var api = new Api();
        var candidate = api.Add(100);
        api.ConfigureQt(vulkan);
        for (var cycle = 0; cycle < 2; cycle++)
        {
            using var reads = new RendererQualificationReadCycle(api);
            var clients = new[] { new WindowsGpuRendererConfirmation.AdapterClient(1, 0, vulkan ? 4U : 12U) };
            var plan = WindowsExternalGpuPlacementRuntime.ReadRendererPlan(candidate, clients, true, true, reads);
            Assert.NotNull(plan);
            Assert.Equal(vulkan ? ExternalGpuRenderer.QtQuickVulkan : ExternalGpuRenderer.QtQuickD3D12, plan.Renderer);
            Assert.Equal(candidate, plan.Root);
            Assert.False(WindowsExternalGpuPlacementRuntime.HasNoRendererMarkers(candidate, reads));
        }
        Assert.Equal(2, api.Opens);
        Assert.Equal(2, api.Closes);
        Assert.Equal(2, api.ModuleReads);
        Assert.Equal(new[] { "Qt6Gui.dll", "Qt6Quick.dll", "Qt6Gui.dll", "Qt6Quick.dll" }.Order(), api.VersionsRead.Order());
        Assert.Equal(2, api.SizesRead.Count);
        Assert.All(api.SizesRead, name => Assert.Equal("Qt6Quick.dll", name));
        Assert.Equal(vulkan ? 10 : 8, api.HashesRead.Count);
        Assert.Equal(vulkan ? 10 : 8, api.PathsRead.Count);
        Assert.DoesNotContain("unused.dll", api.PathsRead);
        Assert.DoesNotContain("D3D12Core.dll", api.PathsRead);
        Assert.Equal(2, api.MemoryReads);
    }

    [Theory]
    [InlineData("start")]
    [InlineData("image")]
    [InlineData("session")]
    [InlineData("debugger")]
    [InlineData("debugger-unknown")]
    [InlineData("machine")]
    [InlineData("version")]
    [InlineData("hash")]
    [InlineData("backend")]
    [InlineData("size")]
    [InlineData("directories")]
    [InlineData("duplicate")]
    [InlineData("no-backend")]
    public void QtQualificationStillRejectsEveryIdentityAndBackendGate(string change)
    {
        var api = new Api();
        var candidate = api.Add(100);
        api.ConfigureQt(false);
        switch (change)
        {
            case "start": api.Identities[100] = api.Identities[100] with { StartKey = Start + 1 }; break;
            case "image": api.Identities[100] = api.Identities[100] with { ImagePath = @"C:\other.exe" }; break;
            case "session": api.Identities[100] = api.Identities[100] with { SessionId = 0 }; break;
            case "debugger": api.Debugged = true; break;
            case "debugger-unknown": api.Debugged = null; break;
            case "machine": api.Amd64 = false; break;
            case "version": api.VersionValue = new(6, 8, 2); break;
            case "hash": api.BadHash = true; break;
            case "backend": api.Backend = 0; break;
            case "size": api.Size = 1; break;
            case "directories": api.SeparateGuiDirectory = true; break;
            case "duplicate": api.Modules.Add(new("Qt6Quick.dll", new(200))); break;
            case "no-backend": api.Modules.RemoveAll(module => module.Name == "D3D12Core.dll"); break;
        }
        using (var reads = new RendererQualificationReadCycle(api)) Assert.Null(QtQuickGpuProcessIdentity.Read(candidate, reads));
        Assert.Equal(api.Opens, api.Closes);
    }

    [Theory]
    [InlineData("parent-newer")]
    [InlineData("session-zero")]
    [InlineData("session-other")]
    [InlineData("parent-role")]
    [InlineData("debugger")]
    public void ChromiumParentSafetyGatesArePreserved(string change)
    {
        var api = new Api();
        api.Add(90, Start - 10);
        var candidate = api.Add(100);
        api.Arguments[100] = [candidate.ExecutablePath, "--type=gpu-process"];
        api.Parents[100] = 90;
        switch (change)
        {
            case "parent-newer": api.Identities[90] = api.Identities[90] with { StartKey = Start + 1 }; break;
            case "session-zero": api.Identities[90] = api.Identities[90] with { SessionId = 0 }; break;
            case "session-other": api.Identities[90] = api.Identities[90] with { SessionId = 2 }; break;
            case "parent-role": api.Arguments[90] = ["browser", "--type=renderer"]; break;
            case "debugger": api.Debugged = true; break;
        }
        using (var reads = new RendererQualificationReadCycle(api)) Assert.Null(ChromiumGpuProcessIdentity.Read(candidate, GpuGraphicsApi.D3D11, reads));
        Assert.Equal(api.Opens, api.Closes);
    }

    [Fact]
    public void FailedNamesOrArgumentsAreNotStructuralAbsenceAndOtherProcessesContinue()
    {
        var api = new Api();
        var first = api.Add(100);
        var second = api.Add(101);
        api.UnreadableModules.Add(100);
        using var reads = new RendererQualificationReadCycle(api);
        Assert.Null(QtQuickGpuProcessIdentity.Read(first, reads));
        Assert.False(WindowsExternalGpuPlacementRuntime.HasNoRendererMarkers(first, reads));
        Assert.True(WindowsExternalGpuPlacementRuntime.HasNoRendererMarkers(second, reads));
        api.ArgumentsFail = true;
        var third = api.Add(102);
        Assert.False(WindowsExternalGpuPlacementRuntime.HasNoRendererMarkers(third, reads));
        Assert.Equal(3, api.Opens);
        Assert.Equal(3, api.ModuleReads);
    }

    [Fact]
    public void DisposalReleasesAllHandlesEvenIfOneReleaseThrowsAndDoesNotReleaseTwice()
    {
        var api = new Api();
        var reads = new RendererQualificationReadCycle(api);
        reads.Get(api.Add(100)); reads.Get(api.Add(101));
        api.ThrowOnClose = true;
        Assert.Throws<IOException>(() => reads.Dispose());
        Assert.Equal(2, api.Closes);
        Assert.Empty(api.Handles);
        reads.Dispose();
        Assert.Equal(2, api.Closes);
        Assert.Throws<ObjectDisposedException>(() => reads.Get(100));
    }

    [Fact]
    public void NativeReaderBindsThisTestProcessAndReadsNamesWithoutProcessWrappers()
    {
        var basic = new WindowsProcessBasicMetricReader();
        var start = basic.ReadCurrentCreationFileTime(Environment.ProcessId)!.Value;
        var path = basic.Read(Environment.ProcessId, start, ProcessMetricReadMask.ImagePath).ImagePath!;
        // The test host loads and unloads modules while other tests run, and the reader reports a table that
        // changed during enumeration as unavailable. A fresh read cycle reads the table again.
        IReadOnlyList<RendererModuleName>? modules = null;
        var attempts = 0;
        while (modules is null && attempts++ < 5)
        {
            using var attempt = new RendererQualificationReadCycle(WindowsRendererQualificationNativeApi.Instance);
            var attemptProcess = attempt.Get(new GpuPlacementProcessInstance(Environment.ProcessId, (ulong)start, "test", path));
            Assert.NotNull(attemptProcess);
            modules = attemptProcess.Modules();
        }
        Assert.NotEmpty(Assert.IsAssignableFrom<IReadOnlyList<RendererModuleName>>(modules));
        using var reads = new RendererQualificationReadCycle(WindowsRendererQualificationNativeApi.Instance);
        Assert.NotNull(reads.Get(new GpuPlacementProcessInstance(Environment.ProcessId, (ulong)start, "test", path)));
        Assert.Null(reads.Get(new GpuPlacementProcessInstance(Environment.ProcessId, (ulong)start + 1, "test", path)));
        Assert.Null(reads.Get(new GpuPlacementProcessInstance(Environment.ProcessId, (ulong)start, "test", path + ".wrong")));
        Assert.Null(QtQuickGpuProcessIdentity.Read(new(Environment.ProcessId, (ulong)start, "test", path), reads));
    }

    internal sealed class Api : IRendererQualificationNativeApi
    {
        internal readonly Dictionary<IntPtr, int> Handles = [];
        internal readonly Dictionary<int, RendererProcessIdentity> Identities = [];
        internal readonly Dictionary<int, string[]> Arguments = [];
        internal readonly Dictionary<int, int> Parents = [];
        internal readonly List<RendererModuleName> Modules = [new("kernel32.dll", new(1)), new("unused.dll", new(2))];
        internal readonly HashSet<int> UnreadableModules = [];
        internal readonly List<string> PathsRead = [], VersionsRead = [], HashesRead = [], SizesRead = [];
        internal int Opens, Closes, IdentitiesRead, ModuleReads, ArgumentReads, MemoryReads;
        internal bool Amd64 = true, BadHash, SeparateGuiDirectory, ThrowOnClose, ArgumentsFail;
        internal bool? Debugged = false;
        internal int Backend = 5, Size = 0x600000;
        internal Version VersionValue = new(6, 8, 3);
        private readonly Dictionary<string, string> hashes = new(StringComparer.OrdinalIgnoreCase)
        {
            ["Qt6Quick.dll"] = "2454BAE3EE29FA76B3ABE0F9D6CE6FC55630A01B400892F79511ADD3EBE78601",
            ["Qt6Gui.dll"] = "428E11074A3764184F06BD437779F483F5C754C8C8DE5A6CDA94D9ED108A4789",
            ["Qt6Core.dll"] = "6965720757C87788C1701B236B8157270F3EA9B7D5EAF2D1297B21803FC3B000",
            ["ucrtbase.dll"] = "5C52E3A303BAAAC0E0AF8BD9B96134993DA34BC9D834A31EF37E1D2CDC7FE192",
            ["vulkan-1.dll"] = "CD63989744D15FE13972511D3AFB7BBC171C7C72FE9C479B4909595A2F9D6EFB"
        };
        internal GpuPlacementProcessInstance Add(int pid, ulong start = Start)
        {
            var path = $@"C:\fixture\app{pid}.exe";
            Identities.Add(pid, new(pid, start, path, 1)); Arguments.Add(pid, [path]);
            return new(pid, start, $"app{pid}", path);
        }
        internal void ConfigureQt(bool vulkan)
        {
            foreach (var name in new[] { "Qt6Gui.dll", "Qt6Quick.dll", "Qt6Core.dll", "ucrtbase.dll", vulkan ? "vulkan-1.dll" : "D3D12Core.dll" })
                Modules.Add(new(name, new(Modules.Count + 1)));
            Backend = vulkan ? 1 : 5;
        }
        public IntPtr Open(int processId) { var handle = new IntPtr(++Opens); Handles.Add(handle, processId); return handle; }
        public void Close(IntPtr process) { Assert.True(Handles.Remove(process)); Closes++; if (ThrowOnClose) throw new IOException("Close failed."); }
        public RendererProcessIdentity? ReadIdentity(IntPtr process, int processId) { IdentitiesRead++; Assert.Equal(processId, Handles[process]); return Identities.GetValueOrDefault(processId); }
        public string[] ReadArguments(IntPtr process) { ArgumentReads++; if (ArgumentsFail) throw new InvalidOperationException("No command line."); return Arguments[Handles[process]]; }
        public int? ReadParentId(IntPtr process) => Parents.GetValueOrDefault(Handles[process]);
        public bool? IsDebugged(IntPtr process) { Assert.True(Handles.ContainsKey(process)); return Debugged; }
        public bool IsAmd64(string path) => Amd64;
        public IReadOnlyList<RendererModuleName>? ReadModuleNames(IntPtr process) { ModuleReads++; return UnreadableModules.Contains(Handles[process]) ? null : Modules.ToArray(); }
        public string? ReadModulePath(IntPtr process, RendererModuleName module) { PathsRead.Add(module.Name); return (SeparateGuiDirectory && module.Name == "Qt6Gui.dll" ? @"C:\other\" : @"C:\qt\") + module.Name; }
        public int? ReadModuleSize(IntPtr process, RendererModuleName module) { SizesRead.Add(module.Name); return Size; }
        public Version ReadVersion(string path) { VersionsRead.Add(Path.GetFileName(path)); return VersionValue; }
        public string HashFile(string path) { HashesRead.Add(Path.GetFileName(path)); return BadHash ? "wrong" : hashes[Path.GetFileName(path)]; }
        public byte[]? ReadMemory(IntPtr process, IntPtr address, int length) { MemoryReads++; Assert.Equal(8, length); return [1, 0, 0, 0, (byte)Backend, 0, 0, 0]; }
    }
}
