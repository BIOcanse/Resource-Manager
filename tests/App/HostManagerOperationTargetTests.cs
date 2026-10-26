using System.Diagnostics;
using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;
using ResourceManager.App.Application.Software;
using ResourceManager.App.Domain.DiskUsage;
using ResourceManager.App.Domain.Operations;
using ResourceManager.App.Domain.Software;
using ResourceManager.App.Endpoints;
using ResourceManager.App.Endpoints.Transport;
using ResourceManager.App.Infrastructure.Operations;

namespace Resource_Manager_APP.Tests;

public sealed class HostManagerOperationTargetTests
{
    internal static IReadOnlyList<HostManagerOperationSubmitCommand> Commands()
    {
        var caller = new SoftwareOperationCaller("S-1-5-21-1001", 4);
        return [
            HostManagerOperationRequestCodec.ComponentDownload("shared-webview2-runtime", new(true)),
            HostManagerOperationRequestCodec.ComponentInstall("shared-webview2-runtime", new(true)),
            HostManagerOperationRequestCodec.ComponentInstall("shared-webview2-runtime", new(true, "latest"), caller),
            HostManagerOperationRequestCodec.DependencyDownload("shared-webview2-runtime", new(true)),
            HostManagerOperationRequestCodec.DependencyInstaller("shared-webview2-runtime", new(true)),
            HostManagerOperationRequestCodec.DependencyInstaller("shared-webview2-runtime", new(true), caller),
            HostManagerOperationRequestCodec.SoftwareUninstall(new("windows-installed:exact:id", true), caller, "Recorded 产品名称"),
            HostManagerOperationRequestCodec.SoftwareUninstall(new("dependency:shared-webview2-runtime", true), caller),
            HostManagerOperationRequestCodec.DiskUsageScan(new(DiskUsageScanScopes.Folder, DiskUsageScanModes.Full, @"C:\历史目录")),
            HostManagerOperationRequestCodec.DiskUsageScan(new(DiskUsageScanScopes.Volume, DiskUsageScanModes.Fast, "D:"), new("S-1-5-21-1001", 4)),
            HostManagerOperationRequestCodec.DiskUsageScan(new(DiskUsageScanScopes.AllVolumes, DiskUsageScanModes.Fast, ""))
        ];
    }

    [Fact]
    public void EveryRegisteredKindAndProducerVariantRecordsItsTypedTargetWithoutChangingDedupeOrRequest()
    {
        var commands = Commands();
        Assert.True(HostManagerOperationKinds.All.SetEquals(commands.Select(command => command.Kind)));
        foreach (var command in commands)
        {
            var subject = command.Subject;
            var expected = command.Kind switch
            {
                "component.download" or "component.install" => HostManagerOperationTargetKind.Component,
                "dependency.download" or "dependency.launch-installer" => HostManagerOperationTargetKind.Dependency,
                "software.uninstall" => HostManagerOperationTargetKind.Software,
                _ => subject.Scope == DiskUsageScanScopes.AllVolumes ? HostManagerOperationTargetKind.AllVolumes : HostManagerOperationTargetKind.Path
            };
            Assert.Equal(expected, subject.TargetKind);
            var domain = expected switch
            {
                HostManagerOperationTargetKind.Component => $"component:{subject.TargetId}",
                HostManagerOperationTargetKind.Dependency => $"dependency:{subject.TargetId}",
                HostManagerOperationTargetKind.Software => $"software:{subject.TargetId}",
                _ => "disk-usage:scan"
            };
            Assert.Equal(domain, command.DomainKey);
            if (expected is HostManagerOperationTargetKind.Component or HostManagerOperationTargetKind.Dependency or HostManagerOperationTargetKind.Software)
                Assert.NotNull(subject.TargetId);
            if (expected is HostManagerOperationTargetKind.Component or HostManagerOperationTargetKind.Dependency or HostManagerOperationTargetKind.AllVolumes)
                Assert.Null(subject.TargetName);
            if (expected == HostManagerOperationTargetKind.Path)
            {
                Assert.Null(subject.TargetId);
                Assert.Equal(subject.TargetPath, subject.TargetName);
                Assert.False(string.IsNullOrEmpty(subject.TargetName));
            }
            if (expected == HostManagerOperationTargetKind.AllVolumes) Assert.Null(subject.TargetPath);
            Assert.Equal(subject, HostManagerOperationRequestCodec.DecodeSubject(HostManagerOperationRequestCodec.EncodeSubject(subject)));
        }
        var named = commands.Single(command => command.Subject.TargetName == "Recorded 产品名称");
        var withoutName = HostManagerOperationRequestCodec.SoftwareUninstall(new(named.Subject.TargetId!, true), new("S-1-5-21-1001", 4));
        Assert.Equal(withoutName.CanonicalRequest, named.CanonicalRequest);
        Assert.Throws<ArgumentException>(() => HostManagerOperationRequestCodec.DiskUsageScan(new("unknown", "fast", "")));
    }

    [Fact]
    public async Task SoftwareProducerCapturesCallerCatalogNameOnceAndExplicitNullWhenAbsentOrUnreadable()
    {
        var registry = new TargetSoftwareRegistry();
        var caller = new SoftwareOperationCaller("S-1-5-21-1001", 4);
        var request = new SoftwareOperationRequest("windows-installed:exact:id", true);
        var accepted = await ResourceManagerEndpointRouteBuilderExtensions.CreateSoftwareUninstallCommandAsync(
            request, caller, registry, NullLogger.Instance, CancellationToken.None);
        registry.Name = "Renamed";
        Assert.Equal("Recorded 产品名称", accepted.Subject.TargetName);
        Assert.Equal(caller.WindowsSid, registry.LastSid);
        var renamed = await ResourceManagerEndpointRouteBuilderExtensions.CreateSoftwareUninstallCommandAsync(
            request, caller, registry, NullLogger.Instance, CancellationToken.None);
        Assert.Equal("Renamed", renamed.Subject.TargetName);
        registry.Name = null;
        Assert.Null((await ResourceManagerEndpointRouteBuilderExtensions.CreateSoftwareUninstallCommandAsync(
            request, caller, registry, NullLogger.Instance, CancellationToken.None)).Subject.TargetName);
        registry.Unreadable = true;
        Assert.Null((await ResourceManagerEndpointRouteBuilderExtensions.CreateSoftwareUninstallCommandAsync(
            request, caller, registry, NullLogger.Instance, CancellationToken.None)).Subject.TargetName);
    }

    [Fact]
    public async Task ProductionWireForEveryTargetAndTerminalStateFeedsProductionTypeScriptDecoder()
    {
        var now = DateTimeOffset.Parse("2026-10-26T00:00:00Z");
        var index = 0;
        var operations = Commands().SelectMany(command => new[] { "running", "succeeded", "failed", "canceled", "stateUncertain" }
            .Select(state => new HostManagerOperationSnapshot((++index).ToString("x32"), command.Kind, command.DomainKey,
                command.Subject, state, 1, 2, 1, 3, now, now, state == "running" ? null : now, false, null, null, null))).ToArray();
        var state = new HostManagerOperationsPublishedState(now, 2, 1,
            new(true, false, null, null, 2, 1), operations);
        var options = new Microsoft.AspNetCore.Http.Json.JsonOptions().SerializerOptions;
        ResourceManagerEndpointRouteBuilderExtensions.ConfigureJsonSerializerOptions(options);
        var payload = JsonSerializer.Serialize(HostManagerOperationWireProjection.Project(state), options);
        var output = await RunNodeAsync(["--input-type=module", "-e",
            "import {readFileSync} from 'node:fs'; import {operationsStateDecoder,operationSnapshotDecoder} from './src/data/operations/operationsStateDecoder.ts'; const s=operationsStateDecoder.decode(JSON.parse(readFileSync(0,'utf8'))); for(const o of s.operations) operationSnapshotDecoder.decode(o); process.stdout.write(JSON.stringify(s.operations.map(o=>o.subject)));"], payload);
        using var decoded = JsonDocument.Parse(output);
        var subjects = decoded.RootElement.EnumerateArray().ToArray();
        Assert.Equal(operations.Length, subjects.Length);
        for (var i = 0; i < subjects.Length; i++)
        {
            Assert.Equal(operations[i].Subject!.TargetKind.ToString(), subjects[i].GetProperty("targetKind").GetString());
            Assert.Equal(operations[i].Subject!.TargetName, subjects[i].GetProperty("targetName").GetString());
            Assert.Equal(operations[i].Subject!.TargetId, subjects[i].GetProperty("targetId").GetString());
            Assert.Equal(operations[i].Subject!.TargetPath, subjects[i].GetProperty("targetPath").GetString());
        }
    }

    [Fact]
    public async Task FocusedProductionTypeScriptContractsRejectMissingAndInvalidTargetFacts()
    {
        foreach (var file in new[] { "operationsStateDecoder.test.ts", "operationsApi.test.ts", "operationRegistry.test.ts", "taskCenterProjection.test.ts" })
            _ = await RunNodeAsync([Path.Combine("tests", file)]);
        _ = await RunNodeAsync([Path.Combine("node_modules", "typescript", "bin", "tsc"), "--noEmit"]);
    }

    private static async Task<string> RunNodeAsync(string[] arguments, string? stdin = null)
    {
        var start = new ProcessStartInfo("node")
        {
            WorkingDirectory = Path.Combine(TestRepositoryPaths.FindRoot(), "src", "UI", "Frontend"),
            UseShellExecute = false, CreateNoWindow = true,
            RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true
        };
        foreach (var argument in new[] { "--experimental-transform-types", "--experimental-strip-types", "--conditions=browser" }.Concat(arguments))
            start.ArgumentList.Add(argument);
        using var process = Process.Start(start)!;
        var stdout = process.StandardOutput.ReadToEndAsync();
        var stderr = process.StandardError.ReadToEndAsync();
        if (stdin is not null) await process.StandardInput.WriteAsync(stdin);
        process.StandardInput.Close();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(60));
        await process.WaitForExitAsync(timeout.Token);
        var result = await stdout;
        Assert.True(process.ExitCode == 0, $"node {string.Join(' ', arguments)} exited {process.ExitCode}: {await stderr}\n{result}");
        return result;
    }

    private sealed class TargetSoftwareRegistry : ISoftwareRegistryView
    {
        internal string? Name = "Recorded 产品名称";
        internal string? LastSid;
        internal bool Unreadable;
        public Task<IReadOnlyList<SoftwareRecord>> GetSoftwareAsync(CancellationToken cancellationToken) => throw new InvalidOperationException("Caller SID is required.");
        public Task<IReadOnlyList<SoftwareRecord>> RefreshSoftwareAsync(CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<IReadOnlyList<SoftwareRecord>> GetSoftwareAsync(string? windowsSid, CancellationToken cancellationToken,
            IReadOnlyList<ResourceManager.App.Domain.ProcessAttribution.RuntimeSoftwareAttribution>? runtimeSoftware = null)
        {
            LastSid = windowsSid;
            if (Unreadable) throw new IOException("Catalog unavailable");
            return Task.FromResult<IReadOnlyList<SoftwareRecord>>(Name is null ? [] : [new("windows-installed:exact:id", Name,
                "other", "installed", [], [], new(true, "WindowsUninstaller", "none"), null)]);
        }
    }
}
