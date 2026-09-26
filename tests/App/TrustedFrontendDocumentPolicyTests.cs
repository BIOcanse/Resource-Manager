using ResourceManager.NativeUi.WebView;

namespace Resource_Manager_APP.Tests;

public sealed class TrustedFrontendDocumentPolicyTests
{
    private readonly TrustedFrontendDocumentPolicy policy = new(
        "http://127.0.0.1:9321/");

    [Theory]
    [InlineData("http://127.0.0.1:9321/")]
    [InlineData("http://127.0.0.1:9321/#monitor")]
    public void TrustedDocument_AcceptsOnlyTheConfiguredRoot(string address)
    {
        Assert.True(policy.IsTrustedDocument(address));
    }

    [Theory]
    [InlineData("http://localhost:9321/")]
    [InlineData("http://127.0.0.1:9322/")]
    [InlineData("http://127.0.0.1:9321/index.html")]
    [InlineData("http://127.0.0.1:9321/?redirect=https://example.com")]
    [InlineData("https://127.0.0.1:9321/")]
    [InlineData("https://example.com/")]
    [InlineData("about:blank")]
    public void TrustedDocument_RejectsEveryOtherTopLevelDocument(string address)
    {
        Assert.False(policy.IsTrustedDocument(address));
    }

    [Theory]
    [InlineData("http://127.0.0.1:9321/api/settings")]
    [InlineData("http://127.0.0.1:9321/api/metrics/subscribe?intervalMs=5000")]
    public void TrustedApiRequest_AcceptsOnlyApplicationApiRequests(string address)
    {
        Assert.True(policy.IsTrustedApiRequest(address));
    }

    [Theory]
    [InlineData("http://127.0.0.1:9321/")]
    [InlineData("http://localhost:9321/api/settings")]
    [InlineData("http://127.0.0.1:9322/api/settings")]
    [InlineData("https://127.0.0.1:9321/api/settings")]
    [InlineData("https://example.com/api/settings")]
    public void TrustedApiRequest_RejectsNonApiOrForeignRequests(string address)
    {
        Assert.False(policy.IsTrustedApiRequest(address));
    }
}
