using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using ResourceManager.App.Application.RuntimeSpecialization;
using ResourceManager.App.Domain.RuntimeSpecialization;
using ResourceManager.App.Infrastructure.Notifications;

namespace Resource_Manager_APP.Tests;

// A production notification owner over a fixed plan. Persistent owners write under root/UserData/Notifications.
internal static class NotificationTestSupport
{
    public static NotificationOwner Create(string? root = null, int retainedCount = 64, bool persist = false,
        TimeProvider? time = null, ILogger<NotificationOwner>? logger = null)
        => new(new FixedPlan(CompiledRuntimePlan.Default with
            {
                HostManager = CompiledHostManagerPlan.Unpublished with { Notifications = new(retainedCount) }
            }),
            new Environment(root ?? Path.GetTempPath()), new RuntimePersistenceCapabilityPolicy(persist),
            time ?? TimeProvider.System, logger ?? NullLogger<NotificationOwner>.Instance);

    public static string StorePath(string root) => Path.Combine(root, "UserData", "Notifications", "notifications.json");

    private sealed class FixedPlan(CompiledRuntimePlan plan) : IRuntimePlanProvider
    {
        public CompiledRuntimePlan Current { get; private set; } = plan;
        public RuntimePlanPublicationLease AcquirePublicationLease() => RuntimePlanPublicationLease.CreateUntracked(Current, 0);
        public RuntimePlanPublicationResult Publish(CompiledRuntimePlan next)
        {
            Current = next;
            return new(next, 0, []);
        }
    }

    private sealed class Environment(string root) : IHostEnvironment
    {
        public string EnvironmentName { get; set; } = Environments.Development;
        public string ApplicationName { get; set; } = "ResourceManager.Tests";
        public string ContentRootPath { get; set; } = root;
        public IFileProvider ContentRootFileProvider { get; set; } = new NullFileProvider();
    }
}
