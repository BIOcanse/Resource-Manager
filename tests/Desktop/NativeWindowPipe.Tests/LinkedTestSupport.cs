global using ResourceManager.Shared.BusinessBlocks;
global using ResourceManager.Shared.BusinessBlocks.State;
global using UiUids = ResourceManager.NativeUi.StateItems.BlockUids;

namespace Resource_Manager_APP.Tests;

// Linked WebView integration tests use this workset-owned fixture instead of
// the backend assembly's general temporary-directory helper.
internal sealed class TrustedAccessTemporaryDirectory : IDisposable
{
    internal TrustedAccessTemporaryDirectory()
    {
        var scratch = Environment.GetEnvironmentVariable("RM_REVIEW_SCRATCH")
            ?? throw new InvalidOperationException("A registered validation scratch root is required.");
        Root = Path.Combine(scratch, "native-bridge-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Root);
    }
    public string Root { get; }
    public void Dispose() { } // The workset's maintenance entry owns bounded cleanup.
}
