using Microsoft.Extensions.Logging.Abstractions;
using ResourceManager.App.Application.ExternalInvocation;
using ResourceManager.App.Domain.ExternalInvocation;
using ResourceManager.App.Infrastructure.ExternalInvocation;
using ResourceManager.App.Application.RuntimeSpecialization;
using ResourceManager.App.Domain.RuntimeSpecialization;

namespace Resource_Manager_APP.Tests;

public sealed class ExternalInvokerTests
{
    [Fact]
    public async Task InvokeAsync_UsesRegisteredInProcessHandler()
    {
        var handlerCalls = 0;
        var module = CreateModule(
            ExternalInvocationAccessClass.Open,
            (_, _) =>
            {
                handlerCalls++;
                return ValueTask.FromResult("direct-result");
            });
        var audit = new RecordingAuditSink();
        var invoker = CreateInvoker(module, ExternalInvocationRuntimePlan.Default, audit);

        var result = await invoker.InvokeAsync(
            CreateRequest(),
            CancellationToken.None);

        Assert.True(result.Succeeded);
        Assert.Equal("direct-result", result.Value);
        Assert.Equal(1, handlerCalls);
        var auditEvent = Assert.Single(audit.Events);
        Assert.Equal(ExternalInvocationStatus.Succeeded, auditEvent.Status);
        Assert.Equal(OperationId, auditEvent.OperationId);
    }

    [Fact]
    public async Task InvokeAsync_DisabledOperationDoesNotReachHandler()
    {
        var handlerCalls = 0;
        var module = CreateModule(
            ExternalInvocationAccessClass.Open,
            (_, _) =>
            {
                handlerCalls++;
                return ValueTask.FromResult("should-not-run");
            });
        var plan = ExternalInvocationRuntimePlan.Compile(
            enabled: true,
            disabledOperations: [OperationId]);
        var invoker = CreateInvoker(module, plan, new RecordingAuditSink());

        var result = await invoker.InvokeAsync(
            CreateRequest(),
            CancellationToken.None);

        Assert.Equal(ExternalInvocationStatus.Disabled, result.Status);
        Assert.Equal(0, handlerCalls);
    }

    [Theory]
    [InlineData(false, false, false)]
    [InlineData(true, false, true)]
    [InlineData(false, true, true)]
    [InlineData(true, true, true)]
    public async Task AuthenticatedOperation_AcceptsFrontendOrAdministrator(
        bool frontend, bool administrator, bool allowed)
    {
        var calls = 0;
        var invoker = CreateInvoker(
            CreateModule(ExternalInvocationAccessClass.Authenticated, (_, _) =>
            {
                calls++;
                return ValueTask.FromResult("called");
            }),
            ExternalInvocationRuntimePlan.Default,
            new RecordingAuditSink());

        var result = await invoker.InvokeAsync(
            CreateRequest(frontend: frontend, administrator: administrator),
            CancellationToken.None);

        Assert.Equal(allowed, result.Succeeded);
        Assert.Equal(allowed ? 1 : 0, calls);
        if (!allowed)
            Assert.Equal("frontend-or-administrator-required", result.ErrorCode);
    }

    [Fact]
    public async Task OpenOperation_DoesNotRequireCallerIdentity()
    {
        var invoker = CreateInvoker(
            CreateModule(ExternalInvocationAccessClass.Open,
                (_, _) => ValueTask.FromResult("open")),
            ExternalInvocationRuntimePlan.Default,
            new RecordingAuditSink());

        var result = await invoker.InvokeAsync(
            CreateRequest(windowsSid: null), CancellationToken.None);

        Assert.True(result.Succeeded);
        Assert.Equal("open", result.Value);
    }

    [Fact]
    public void Registry_RejectsDuplicateOperationIds()
    {
        var first = CreateModule(
            ExternalInvocationAccessClass.Open,
            (_, _) => ValueTask.FromResult("one"),
            moduleId: "module-one");
        var second = CreateModule(
            ExternalInvocationAccessClass.Open,
            (_, _) => ValueTask.FromResult("two"),
            moduleId: "module-two");

        var exception = Assert.Throws<InvalidOperationException>(
            () => new ExternalInvocationRegistry([first, second]));

        Assert.Contains(OperationId, exception.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("Test.Operation")]
    [InlineData("components.list")]
    [InlineData("test.operation_name.read")]
    [InlineData("test.operation--name.read")]
    public void Registry_RejectsNonCanonicalOperationIds(string operationId)
    {
        var operation = new ExternalInvocationOperation<ExternalInvocationNoRequest, string>(
            new ExternalInvocationOperationDescriptor(
                operationId,
                "test-module",
                "1.0.0",
                ExternalInvocationAccessClass.Open,
                "ITestQueries",
                ExternalInvocationOperationCategory.Query,
                ExternalInvocationRiskLevel.Low,
                "Invalid test operation.",
                "test.no-request.v1",
                "test.response.v1"),
            (_, _) => ValueTask.FromResult("invalid"));
        var module = new TestModule("test-module", operation);

        var exception = Assert.Throws<InvalidOperationException>(
            () => new ExternalInvocationRegistry([module]));

        Assert.Contains("lowercase kebab-case segments", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Registry_RejectsNonCanonicalModuleIds()
    {
        var module = CreateModule(
            ExternalInvocationAccessClass.Open,
            (_, _) => ValueTask.FromResult("invalid"),
            moduleId: "Test_Module");

        var exception = Assert.Throws<InvalidOperationException>(
            () => new ExternalInvocationRegistry([module]));

        Assert.Contains("lowercase kebab-case", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Registry_RejectsContractInterfaceThatDoesNotMatchCategory()
    {
        var operation = new ExternalInvocationOperation<ExternalInvocationNoRequest, string>(
            new ExternalInvocationOperationDescriptor(
                OperationId,
                "test-module",
                "1.0.0",
                ExternalInvocationAccessClass.Open,
                "ITestService",
                ExternalInvocationOperationCategory.Query,
                ExternalInvocationRiskLevel.Low,
                "Invalid test operation.",
                "test.no-request.v1",
                "test.response.v1"),
            (_, _) => ValueTask.FromResult("invalid"));

        var exception = Assert.Throws<InvalidOperationException>(
            () => new ExternalInvocationRegistry([new TestModule("test-module", operation)]));

        Assert.Contains("does not match category", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Registry_RejectsLowRiskStateChangingCategory()
    {
        var operation = new ExternalInvocationOperation<ExternalInvocationNoRequest, string>(
            new ExternalInvocationOperationDescriptor(
                OperationId,
                "test-module",
                "1.0.0",
                ExternalInvocationAccessClass.Authenticated,
                "ITestCommands",
                ExternalInvocationOperationCategory.Command,
                ExternalInvocationRiskLevel.Low,
                "Invalid test operation.",
                "test.no-request.v1",
                "test.response.v1"),
            (_, _) => ValueTask.FromResult("invalid"));

        var exception = Assert.Throws<InvalidOperationException>(
            () => new ExternalInvocationRegistry([new TestModule("test-module", operation)]));

        Assert.Contains("risk 'Low' is invalid", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Registry_RequiresHighRiskForPlatformGateway()
    {
        var operation = new ExternalInvocationOperation<ExternalInvocationNoRequest, string>(
            new ExternalInvocationOperationDescriptor(
                OperationId,
                "test-module",
                "1.0.0",
                ExternalInvocationAccessClass.Authenticated,
                "ITestGateway",
                ExternalInvocationOperationCategory.PlatformAction,
                ExternalInvocationRiskLevel.Medium,
                "Invalid test operation.",
                "test.no-request.v1",
                "test.response.v1"),
            (_, _) => ValueTask.FromResult("invalid"));

        var exception = Assert.Throws<InvalidOperationException>(
            () => new ExternalInvocationRegistry([new TestModule("test-module", operation)]));

        Assert.Contains("risk 'Medium' is invalid", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Registry_CatalogExposesCanonicalInterfaceCategoryAndRisk()
    {
        var registry = new ExternalInvocationRegistry(
        [
            CreateModule(
                ExternalInvocationAccessClass.Open,
                (_, _) => ValueTask.FromResult("result"))
        ]);

        var catalog = registry.GetCatalog(ExternalInvocationRuntimePlan.Default);
        var operation = Assert.Single(Assert.Single(catalog.Modules).Operations).Operation;

        Assert.Equal("1.1.0", catalog.SchemaVersion);
        Assert.Equal("ITestQueries", operation.ContractInterfaceName);
        Assert.Equal(ExternalInvocationOperationCategory.Query, operation.Category);
        Assert.Equal(ExternalInvocationRiskLevel.Low, operation.RiskLevel);
    }

    private const string OperationId = "test.operation.read";

    private static ExternalInvoker CreateInvoker(
        IExternalInvocationModule module,
        ExternalInvocationRuntimePlan plan,
        IExternalInvocationAuditSink auditSink,
        IRuntimePlanProvider? appRuntimePlan = null)
    {
        return new ExternalInvoker(
            new ExternalInvocationRegistry([module]),
            new StaticRuntimePlanProvider(plan),
            new ExternalInvocationPolicy(appRuntimePlan ?? CreateAppRuntimePlan(debugEnabled: false)),
            auditSink,
            NullLogger<ExternalInvoker>.Instance);
    }

    [Fact]
    public async Task DebugMode_SkipsAuthenticationAndClosingItRestoresTheRule()
    {
        var calls = 0;
        var runtime = CreateAppRuntimePlan(debugEnabled: false);
        var audit = new RecordingAuditSink();
        var invoker = CreateInvoker(
            CreateModule(ExternalInvocationAccessClass.Authenticated, (_, _) =>
            {
                calls++;
                return ValueTask.FromResult("called");
            }),
            ExternalInvocationRuntimePlan.Default, audit, runtime);
        var anonymous = CreateRequest(windowsSid: null);

        Assert.False((await invoker.InvokeAsync(anonymous, CancellationToken.None)).Succeeded);
        runtime.Publish(runtime.Current with
        {
            Diagnostics = runtime.Current.Diagnostics with { DebugModeEnabled = true }
        });
        Assert.True((await invoker.InvokeAsync(anonymous, CancellationToken.None)).Succeeded);
        runtime.Publish(runtime.Current with
        {
            Diagnostics = runtime.Current.Diagnostics with { DebugModeEnabled = false }
        });
        Assert.False((await invoker.InvokeAsync(anonymous, CancellationToken.None)).Succeeded);
        Assert.Equal(1, calls);
        Assert.Equal(3, audit.Events.Count);
    }

    private static MutableAppRuntimePlanProvider CreateAppRuntimePlan(bool debugEnabled)
        => new(CompiledRuntimePlan.Default with
        {
            Diagnostics = CompiledDiagnosticsPlan.Default with { DebugModeEnabled = debugEnabled }
        });

    private sealed class MutableAppRuntimePlanProvider(CompiledRuntimePlan plan) : IRuntimePlanProvider
    {
        public CompiledRuntimePlan Current { get; private set; } = plan;

        public RuntimePlanPublicationLease AcquirePublicationLease()
            => RuntimePlanPublicationLease.CreateUntracked(Current, 0);

        public RuntimePlanPublicationResult Publish(CompiledRuntimePlan next)
        {
            Current = next;
            return new(next, 0, []);
        }
    }

    private static IExternalInvocationModule CreateModule(
        ExternalInvocationAccessClass accessClass,
        Func<ExternalInvocationNoRequest, CancellationToken, ValueTask<string>> handler,
        string moduleId = "test-module")
    {
        var operation = new ExternalInvocationOperation<ExternalInvocationNoRequest, string>(
            new ExternalInvocationOperationDescriptor(
                OperationId,
                moduleId,
                "1.0.0",
                accessClass,
                "ITestQueries",
                ExternalInvocationOperationCategory.Query,
                ExternalInvocationRiskLevel.Low,
                "Test operation.",
                "test.no-request.v1",
                "test.response.v1"),
            handler);
        return new TestModule(moduleId, operation);
    }

    private static ExternalInvocationRequest CreateRequest(
        bool frontend = false,
        bool administrator = false,
        string? windowsSid = "S-1-5-21-test")
    {
        return new ExternalInvocationRequest(
            OperationId,
            null,
            new ExternalInvocationCaller(
                "test-subject",
                windowsSid,
                "test-app",
                ExternalInvocationTransport.Internal,
                frontend,
                administrator,
                Environment.ProcessId),
            Guid.NewGuid().ToString("N"));
    }

    private sealed class TestModule(
        string moduleId,
        IExternalInvocationOperation operation) : IExternalInvocationModule
    {
        public ExternalInvocationModuleDescriptor Descriptor { get; } = new(
            moduleId,
            "1.0.0",
            "Test module.");

        public ExternalInvocationModuleAvailability Availability
            => ExternalInvocationModuleAvailability.Ready;

        public IReadOnlyList<IExternalInvocationOperation> Operations { get; } = [operation];
    }

    private sealed class StaticRuntimePlanProvider(
        ExternalInvocationRuntimePlan plan) : IExternalInvocationRuntimePlanProvider
    {
        public ExternalInvocationRuntimePlan Current { get; } = plan;
    }

    private sealed class RecordingAuditSink : IExternalInvocationAuditSink
    {
        public List<ExternalInvocationAuditEvent> Events { get; } = [];

        public ValueTask WriteAsync(
            ExternalInvocationAuditEvent auditEvent,
            CancellationToken cancellationToken)
        {
            Events.Add(auditEvent);
            return ValueTask.CompletedTask;
        }
    }
}
