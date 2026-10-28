using ResourceManager.App.Infrastructure.Control.Writers;
using ResourceManager.App.StateItems;
using ResourceManager.Shared.BusinessBlocks.State;

namespace Resource_Manager_APP.Tests;

public sealed class HardwareBridgeInitialAvailabilityTests
{
    [Fact]
    public void RecoveryDuringInitialFactPublicationCannotBeOverwrittenByTheCapturedUnavailableValue()
    {
        var authority = new StateAuthority(StateRegistry.Items, StateRegistry.Blocks);
        var publications = new List<bool>();
        using var observer = authority.Subscribe(StateItemUids.HardwareBridgeAvailable,
            "initial-availability-test", _ =>
            {
                publications.Add(authority.Read<bool>(StateItemUids.HardwareBridgeAvailable).Value);
                return Task.CompletedTask;
            });
        string? installed = null;
        var recovered = false;
        using var client = new HardwareBridgeClient(() => installed, HardwareBridgeSessionTests.Options,
            authority, beforeAvailabilityPublication: (owner, value) =>
            {
                if (value || recovered) return;
                recovered = true;
                // The first false has been captured, but has not reached the real state cell.
                // Installation release synchronously queues the owner's recovery notification.
                var installation = owner.SuspendForInstallationAsync(default).GetAwaiter().GetResult();
                installed = "fixture-installed-without-launching";
                installation.DisposeAsync().GetAwaiter().GetResult();
            });

        Assert.True(recovered);
        Assert.True(client.Owner.IsAvailable);
        Assert.True(authority.Read<bool>(StateItemUids.HardwareBridgeAvailable).Value);
        Assert.Equal(new[] { false, true }, publications);
    }
}
