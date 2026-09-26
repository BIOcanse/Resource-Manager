using System.Reflection;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Text;
using ResourceManager.App.Infrastructure.NativeCore;

namespace Resource_Manager_APP.Tests;

public sealed class NativeFileQuerySessionTests
{
    [Fact]
    public void SessionDeclaresEveryRootNativeEntryPoint()
    {
        var nativeMethods = typeof(NativeFileQuerySession).GetNestedType(
            "NativeMethods",
            BindingFlags.NonPublic);
        Assert.NotNull(nativeMethods);
        var expected = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["GetAbiVersion"] = "rm_file_query_abi_version",
            ["Create"] = "rm_file_query_create",
            ["Destroy"] = "rm_file_query_destroy",
            ["Reconfigure"] = "rm_file_query_reconfigure",
            ["QueryCapacity"] = "rm_file_query_query_capacity",
            ["Begin"] = "rm_file_query_begin",
            ["SubmitCandidates"] = "rm_file_query_submit_candidates",
            ["Finalize"] = "rm_file_query_finalize",
            ["Reset"] = "rm_file_query_reset",
            ["Snapshot"] = "rm_file_query_snapshot"
        };

        foreach (var (methodName, entryPoint) in expected)
        {
            var method = nativeMethods!.GetMethod(
                methodName,
                BindingFlags.Static | BindingFlags.NonPublic);
            Assert.NotNull(method);
            var import = method!.GetCustomAttribute<LibraryImportAttribute>();
            Assert.NotNull(import);
            Assert.Equal("ResourceManager.NativeCore", import!.LibraryName);
            Assert.Equal(entryPoint, import.EntryPoint);
        }
    }

    [Fact]
    public async Task WorkspaceUsesThePublishedFixedCapacityAndReusesOnlyResetSession()
    {
        using var workspace = NativeFileQueryTestWorkspaceFactory.Create();
        NativeFileQuerySession firstSession;
        using (var first = await workspace.AcquireAsync(CancellationToken.None))
        {
            firstSession = first.Session;
            Assert.Equal(NativeFileQueryAbi.Version, NativeFileQuerySession.GetAbiVersion());
            Assert.Equal(4096U, firstSession.Capacity.QueryUtf8ByteCapacity);
            Assert.Equal(3U, firstSession.Capacity.SourcePlanCapacity);
            Assert.Equal(1000U, firstSession.Capacity.CandidateCapacityPerSource);
            Assert.InRange(firstSession.Capacity.ResidentByteCount, 1UL, 12UL * 1024 * 1024);

            var query = Encoding.UTF8.GetBytes("  telemetry  ");
            var sources = new NativeFileQuerySourcePlan[3];
            var planBytes = new byte[131072];
            var begin = new NativeFileQueryBeginInput
            {
                AbiVersion = NativeFileQueryAbi.Version,
                StructSize = checked((uint)Unsafe.SizeOf<NativeFileQueryBeginInput>()),
                ConfigurationGeneration = firstSession.ConfigurationGeneration,
                OperationEpoch = workspace.NextEpoch(),
                QueryEpoch = workspace.NextEpoch(),
                QueryByteCount = checked((uint)query.Length),
                RequestedResultCount = 20,
                ValidMask = (ulong)NativeFileQueryBeginValidity.Required
            };
            Assert.Equal(
                NativeFileQueryStatus.Ok,
                firstSession.Begin(
                    in begin,
                    query,
                    sources,
                    planBytes,
                    out var output));
            Assert.Equal((uint)NativeFileQueryPlanMode.Fts, output.Mode);
            Assert.Equal(3U, output.SourcePlanCount);
            Assert.Equal(160U, output.CandidateLimitPerSource);
            Assert.Equal(480U, output.MaximumTotalCandidateCount);
            Assert.Equal(
                "telemetry",
                Encoding.UTF8.GetString(
                    planBytes,
                    checked((int)output.NormalizedQueryOffset),
                    checked((int)output.NormalizedQueryLength)));

            var reset = new NativeFileQueryResetInput
            {
                AbiVersion = NativeFileQueryAbi.Version,
                StructSize = checked((uint)Unsafe.SizeOf<NativeFileQueryResetInput>()),
                ConfigurationGeneration = firstSession.ConfigurationGeneration,
                OperationEpoch = workspace.NextEpoch(),
                ValidMask = (ulong)NativeFileQueryResetValidity.Required
            };
            Assert.Equal(NativeFileQueryStatus.Ok, firstSession.Reset(in reset));
            first.MarkReusable();
        }

        using var second = await workspace.AcquireAsync(CancellationToken.None);
        Assert.Same(firstSession, second.Session);
    }
}
