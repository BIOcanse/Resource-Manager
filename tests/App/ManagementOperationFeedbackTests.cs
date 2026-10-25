using ResourceManager.App.Application.Operations;
using ResourceManager.App.Infrastructure.Software;
using ResourceManager.App.Application.Components;
using ResourceManager.App.Application.Dependencies;
using ResourceManager.App.Domain.Components;
using ResourceManager.App.Domain.Dependencies;
using ResourceManager.App.Domain.Messages;
using ResourceManager.App.Domain.Operations;
using ResourceManager.App.Domain.Software;
using ResourceManager.App.Endpoints.Transport;
using ResourceManager.App.Infrastructure.Dependencies;
using ResourceManager.App.Infrastructure.Operations;
using ResourceManager.App.Infrastructure.Operations.Effects;

namespace Resource_Manager_APP.Tests;

public sealed class ManagementOperationFeedbackTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ComponentActionsForwardProgressThroughTheManagerAndExecutor(bool install)
    {
        var dependency = new ReportingDependency();
        var manager = new ComponentManager(dependency, null!, null!, null!);
        var executor = install ? ComponentOperationEffectExecutor.ForInstall(manager)
            : new ComponentOperationEffectExecutor(manager);
        var sink = new Sink();
        var ticket = Ticket(install);

        var completion = await executor.ExecuteStartAsync(ticket, sink, default);

        var update = Assert.Single(sink.Updates);
        Assert.Equal(25000u, update.PercentMilli);
        Assert.Equal(100ul, update.BytesDone);
        Assert.Equal(400ul, update.BytesTotal);
        Assert.Equal(20ul, update.SpeedBytesPerSecond);
        Assert.Equal(HostManagerOperationStage.Download, update.Stage);
        Assert.Equal(ticket, sink.Ticket);
        Assert.Equal(HostManagerOperationEffectOutcome.RetryableFailure, completion.Outcome);
        var feedback = Assert.IsType<HostManagerOperationFeedback>(HostManagerOperationFeedback.Read(completion.Message));
        Assert.Equal(BackendMessageDomains.Operation, feedback.Message.Domain);
        Assert.Equal(BackendMessageCodes.Operation.IoFailure, feedback.Message.Code);
        Assert.Equal("fixture write failure", feedback.Diagnostic);
    }

    [Theory]
    [InlineData("InstallerLaunched", BackendMessageCodes.Dependency.InstallerLaunched)]
    [InlineData("Installed", BackendMessageCodes.Dependency.SharedRuntimeInstalled)]
    public async Task TypedActionResultSurvivesExecutorAndWire(string state, byte code)
    {
        var executor = ComponentOperationEffectExecutor.ForInstall(new ResultComponent(state, code));
        var completion = await executor.ExecuteStartAsync(Ticket(true), new Sink(), default);
        Assert.Equal(HostManagerOperationEffectOutcome.Succeeded, completion.Outcome);
        var wire = HostManagerOperationWireProjection.Project(Operation(completion.Message, null));
        Assert.Equal(state, wire.ResultCode);
        Assert.Equal(BackendMessageDomains.Dependency, wire.ResultMessage!.Domain);
        Assert.Equal(code, wire.ResultMessage.Code);
        Assert.Equal("component:shared-webview2-runtime", wire.DomainKey);
        Assert.Null(typeof(HostManagerOperationWireSnapshot).GetProperty("Diagnostic"));
    }

    [Fact]
    public async Task ComponentInstallAcquisitionFailureSettlesWithTypedDependencyFeedback()
    {
        var executor = ComponentOperationEffectExecutor.ForInstall(new ResultComponent(
            "runtimeFilesUnavailable",
            BackendMessageCodes.Dependency.RuntimeFilesUnavailable));

        var completion = await executor.ExecuteStartAsync(Ticket(true), new Sink(), default);

        Assert.Equal(HostManagerOperationEffectOutcome.TerminalFailure, completion.Outcome);
        var feedback = Assert.IsType<HostManagerOperationFeedback>(HostManagerOperationFeedback.Read(completion.Message));
        Assert.Null(feedback.ResultCode);
        Assert.Equal(BackendMessageDomains.Dependency, feedback.Message.Domain);
        Assert.Equal(BackendMessageCodes.Dependency.RuntimeFilesUnavailable, feedback.Message.Code);
        Assert.Null(feedback.Diagnostic);

        var wire = HostManagerOperationWireProjection.Project(Operation(null, completion.Message));
        Assert.Equal("failed", wire.State);
        Assert.Null(wire.ResultCode);
        Assert.Null(wire.ResultMessage);
        Assert.Equal(BackendMessageDomains.Dependency, wire.ErrorMessage!.Domain);
        Assert.Equal(BackendMessageCodes.Dependency.RuntimeFilesUnavailable, wire.ErrorMessage.Code);
    }

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    [InlineData(4)]
    [InlineData(5)]
    [InlineData(6)]
    public void FailureCodesSurviveTheWire(int reason)
    {
        Exception error = reason switch
        {
            1 => new HttpRequestException("network"),
            2 => new FileNotFoundException("file"),
            3 => new UnauthorizedAccessException("access"),
            4 => new InvalidOperationException("terms"),
            5 => new IOException("disk"),
            _ => new Exception("unexpected")
        };
        var wire = HostManagerOperationWireProjection.Project(Operation(null, HostManagerOperationFeedback.Failure(error)));
        Assert.Equal(BackendMessageDomains.Operation, wire.ErrorMessage!.Domain);
        Assert.Equal(reason, wire.ErrorMessage.Code);
        Assert.Equal(error.Message, HostManagerOperationFeedback.Read(HostManagerOperationFeedback.Failure(error))!.Diagnostic);
        Assert.Null(typeof(HostManagerOperationWireSnapshot).GetProperty("Diagnostic"));
    }

    [Fact]
    public void CleanupResultIsACodeAndUnstructuredTextIsNotExposed()
    {
        var result = HostManagerOperationFeedback.Result("RemovedManagedRoot",
            BackendMessage.Create(BackendMessageDomains.Software, BackendMessageCodes.Software.ManagedRootRemoved));
        var wire = HostManagerOperationWireProjection.Project(Operation(result, null));
        Assert.Equal(BackendMessageCodes.Software.ManagedRootRemoved, wire.ResultMessage!.Code);
        Assert.Equal("RemovedManagedRoot", wire.ResultCode);
        Assert.Null(HostManagerOperationWireProjection.Project(Operation("old journal entry", null)).ResultMessage);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task DependencyExecutorRetainsTheOwnerResult(bool launch)
    {
        var owner = new ReportingDependency(failDownload: false);
        var command = launch ? HostManagerOperationRequestCodec.DependencyInstaller("shared-webview2-runtime", new(true))
            : HostManagerOperationRequestCodec.DependencyDownload("shared-webview2-runtime", new(true));
        var completion = await new DependencyOperationEffectExecutor(owner, launch).ExecuteStartAsync(CreateTicket(command), new Sink(), default);
        var wire = HostManagerOperationWireProjection.Project(Operation(completion.Message, null));
        Assert.Equal(launch ? "InstallerLaunched" : "Downloaded", wire.ResultCode);
        Assert.Equal(launch ? BackendMessageCodes.Dependency.InstallerLaunched : BackendMessageCodes.Dependency.InstallerDownloaded,
            wire.ResultMessage!.Code);
    }

    [Fact]
    public async Task ManagedCleanupPublishesALocalizedCodeThroughTheSoftwareExecutor()
    {
        var owner = new SoftwareOperationManager(new ReportingDependency(), null!, new EmptyTracker(), null!);
        var command = HostManagerOperationRequestCodec.SoftwareUninstall(new("dependency:shared-webview2-runtime", true), new("fixture-sid", 1));
        var completion = await new SoftwareOperationEffectExecutor(owner).ExecuteStartAsync(CreateTicket(command), new Sink(), default);
        Assert.Equal(HostManagerOperationEffectOutcome.Succeeded, completion.Outcome);
        var wire = HostManagerOperationWireProjection.Project(Operation(completion.Message, null));
        Assert.Equal("RemovedManagedRoot", wire.ResultCode);
        Assert.Equal(BackendMessageDomains.Software, wire.ResultMessage!.Domain);
        Assert.Equal(BackendMessageCodes.Software.ManagedRootRemoved, wire.ResultMessage.Code);
        Assert.Empty(wire.ResultMessage.Args);
    }

    private sealed class EmptyTracker : IFileChangeTracker
    {
        public Task<FileInventorySnapshot> CaptureAsync(FileChangeTrackingScope scope, CancellationToken cancellationToken)
            => Task.FromResult(new FileInventorySnapshot(scope.OwnerId, DateTimeOffset.UtcNow, scope.RootPaths, 2000,
                new Dictionary<string, FileInventoryEntry>(), []));
        public FileChangeReport Compare(FileInventorySnapshot before, FileInventorySnapshot after)
            => new(before.OwnerId, before.CapturedAt, after.CapturedAt, before.RootPaths, 0, 0, 0, 0, 0, [], [], [], false);
    }

    private static HostManagerOperationSnapshot Operation(string? result, string? error)
    {
        var now = DateTimeOffset.UtcNow;
        return new("00000000000000000000000000000001", "component.install", "component:shared-webview2-runtime",
            new HostManagerOperationSubject(TargetId: "shared-webview2-runtime"), error is null ? "succeeded" : "failed", 1, 1, 1, 3, now, now, now, false, null, result, error);
    }

    private static HostManagerOperationActionTicket Ticket(bool install)
    {
        var command = install
            ? HostManagerOperationRequestCodec.ComponentInstall("shared-webview2-runtime", new(true))
            : HostManagerOperationRequestCodec.ComponentDownload("shared-webview2-runtime", new(true));
        return CreateTicket(command);
    }

    private static HostManagerOperationActionTicket CreateTicket(HostManagerOperationSubmitCommand command)
    {
        var entry = new HostManagerOperationPayloadEntry(command.RequestSchemaId, command.RequestSchemaVersion,
            HostManagerOperationPayloadRole.Request, default, default, default, default, command.CanonicalRequest, []);
        return new(default, command.Kind, entry, default);
    }

    private sealed class Sink : IHostManagerOperationProgressSink
    {
        internal List<HostManagerOperationProgressUpdate> Updates { get; } = [];
        internal HostManagerOperationActionTicket? Ticket { get; private set; }
        public ValueTask ReportAsync(HostManagerOperationActionTicket ticket, HostManagerOperationProgressUpdate progress, CancellationToken cancellationToken)
        {
            Ticket = ticket;
            Updates.Add(progress);
            return ValueTask.CompletedTask;
        }
    }

    private sealed class ResultComponent(string state, byte code) : IComponentManager
    {
        public Task<IReadOnlyList<ComponentStatus>> GetStatusesAsync(CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<ComponentStatus?> GetStatusAsync(string id, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<ComponentActionResult> DownloadAsync(string id, bool acknowledgeExternalTerms, string? versionChoice,
            CancellationToken cancellationToken, IProgress<DependencyDownloadProgress>? progress = null) => throw new NotSupportedException();
        public Task<ComponentActionResult> InstallAsync(string id, bool acknowledgeExternalTerms, string? versionChoice,
            CancellationToken cancellationToken, SoftwareOperationCaller? caller = null, IProgress<DependencyDownloadProgress>? progress = null)
            => Task.FromResult(new ComponentActionResult(id, state, BackendMessage.Create(BackendMessageDomains.Dependency, code)));
        public Task<ComponentActionResult> VerifyAsync(string id, CancellationToken cancellationToken) => throw new NotSupportedException();
    }

    private sealed class ReportingDependency(bool failDownload = true) : IOptionalDependencyManager
    {
        private readonly string root = Path.Combine(Path.GetTempPath(), "management-feedback-nonexistent-" + Guid.NewGuid().ToString("N"));
        public Task<IReadOnlyList<OptionalDependencyStatus>> GetStatusesAsync(CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<OptionalDependencyStatus?> GetStatusAsync(string id, CancellationToken cancellationToken)
            => Task.FromResult<OptionalDependencyStatus?>(new(OptionalDependencyCatalog.Find(id)!, "Available", root, Path.Combine(root, "cache"), Path.Combine(root, "install"), null,
                false, false, true, false, "install", null, "None", BackendMessage.Create(1, 8)));
        public Task<DependencyVersionOptions> GetVersionOptionsAsync(string id, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<OptionalDependencyDownloadResult> DownloadAsync(string id, bool acknowledgeExternalTerms, string? versionChoice,
            CancellationToken cancellationToken, IProgress<DependencyDownloadProgress>? progress = null)
        {
            progress?.Report(new(id, 100, 400, 25, 20));
            if (failDownload) throw new IOException("fixture write failure");
            return Task.FromResult(new OptionalDependencyDownloadResult(id, "Downloaded", "installer.exe", 400,
                BackendMessage.Create(BackendMessageDomains.Dependency, BackendMessageCodes.Dependency.InstallerDownloaded)));
        }
        public Task<OptionalDependencyLaunchResult> LaunchInstallerAsync(string id, bool acknowledgeExternalTerms, string? versionChoice,
            CancellationToken cancellationToken, SoftwareOperationCaller? caller = null) => Task.FromResult(new OptionalDependencyLaunchResult(id, "InstallerLaunched", "installer.exe",
                BackendMessage.Create(BackendMessageDomains.Dependency, BackendMessageCodes.Dependency.InstallerLaunched)));
    }
}
