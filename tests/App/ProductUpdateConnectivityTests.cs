using ResourceManager.App.Infrastructure.Updates;

namespace Resource_Manager_APP.Tests;

public sealed class ProductUpdateConnectivityTests
{
    [Fact]
    public void WindowsNlmQueryAndChangeSubscriptionCanBeOpenedAndReleased()
    {
        // Read Windows' existing machine-wide fact only. No HTTP request or
        // connectivity change is made, and either online/offline is valid.
        using var source = new WindowsInternetConnectivity();
        _ = source.IsOnline;
    }
}
