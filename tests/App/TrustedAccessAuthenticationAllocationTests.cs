using System.Collections.Frozen;
using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using Microsoft.AspNetCore.Http;
using ResourceManager.App.Application.TrustedAccess;
using ResourceManager.App.Domain.TrustedAccess;
using ResourceManager.App.Infrastructure.Security;
using ResourceManager.App.Infrastructure.Security.TrustedAccess;
using ResourceManager.Shared.Runtime.TrustedAccess;
using Xunit.Abstractions;

namespace Resource_Manager_APP.Tests;

public sealed class TrustedAccessAuthenticationAllocationTests(ITestOutputHelper output)
{
    [Fact]
    public async Task WarmMappedStoreAuthenticationAndFrameValidationHaveBoundedAllocation()
    {
        using var temporary = new TrustedAccessTemporaryDirectory();
        using var store = new SqliteTrustedAccessKeyStore(temporary.Root,
            new TrustedAccessKeyStoreTests.TestTrustedAccessStorageFileBoundary());
        var keyFile = new TrustedAccessTestKeyFileAccess();
        using var owner = new TrustedAccessKeyService(store, keyFile);
        await owner.InitializeAsync(false, CancellationToken.None);
        var apiKey = keyFile.Value!;
        var summary = (await owner.AuthenticateAsync(AccessKeyPurposes.Control, apiKey, CancellationToken.None))!;
        const int count = 10000;
        for (var index = 0; index < 2000; index++)
        {
            _ = owner.AuthenticateAsync(AccessKeyPurposes.Control, apiKey, CancellationToken.None).GetAwaiter().GetResult();
            _ = owner.ValidateCredentialAsync(summary.Id, CancellationToken.None).GetAwaiter().GetResult();
        }

        var before = GC.GetAllocatedBytesForCurrentThread();
        var started = Stopwatch.GetTimestamp();
        for (var index = 0; index < count; index++)
            _ = owner.AuthenticateAsync(AccessKeyPurposes.Control, apiKey, CancellationToken.None).GetAwaiter().GetResult();
        var authElapsed = Stopwatch.GetElapsedTime(started);
        var authBytes = GC.GetAllocatedBytesForCurrentThread() - before;
        before = GC.GetAllocatedBytesForCurrentThread();
        started = Stopwatch.GetTimestamp();
        for (var index = 0; index < count; index++)
            _ = owner.ValidateCredentialAsync(summary.Id, CancellationToken.None).GetAwaiter().GetResult();
        var frameElapsed = Stopwatch.GetElapsedTime(started);
        var frameBytes = GC.GetAllocatedBytesForCurrentThread() - before;

        output.WriteLine($"mapped store; calls={count}; authentication={authBytes / (double)count:F1} B/op, {authElapsed.TotalNanoseconds / count:F1} ns/op; frame validation={frameBytes / (double)count:F1} B/op, {frameElapsed.TotalNanoseconds / count:F1} ns/op");
        Assert.True(authBytes <= count * 16L, $"Mapped warm authentication allocated {authBytes} bytes.");
        Assert.True(frameBytes <= count * 16L, $"Mapped warm frame validation allocated {frameBytes} bytes.");
        Assert.True(await owner.ValidateCredentialAsync(summary.Id, CancellationToken.None));
    }

    [Theory]
    [InlineData(1)]
    [InlineData(128)]
    [InlineData(1024)]
    public async Task WarmAuthenticationAndHttpPolicyHaveBoundedAllocationAtRealisticKeyCounts(int keyCount)
    {
        var keys = Enumerable.Range(1, keyCount).Select(index =>
        {
            var secret = "rm_sk_" + index.ToString("x64");
            return new StoredTrustedAccessKey(index.ToString(), "allocation fixture", AccessKeyPurposes.Control,
                AccessKeyScopes.Default, SHA256.HashData(Encoding.UTF8.GetBytes(secret)), DateTimeOffset.UnixEpoch);
        }).ToArray();
        var state = new TrustedAccessKeyStoreState(keys, false, null, true);
        var store = new TrustedAccessTestStore("allocation-fixture");
        await store.InitializeAsync(state, CancellationToken.None);
        using var owner = new TrustedAccessKeyService(store, new TrustedAccessTestKeyFileAccess());
        var apiKey = "rm_sk_" + keyCount.ToString("x64");
        var summary = await owner.AuthenticateAsync(AccessKeyPurposes.Control, apiKey, CancellationToken.None);
        Assert.NotNull(summary);

        // This reproduces the previous owner operations from the review: allocating
        // UTF-8/hash/hex/lookup values, an EnsureLoaded task, and a summary/scope copy.
        // It is a source-based comparator, not an historical binary benchmark.
        var oldLookup = keys.ToFrozenDictionary(key => $"{key.Purpose}:{Convert.ToHexString(key.SecretHash)}");
        Task<AccessKeySummary?> OldAuthentication()
        {
            _ = Task.FromResult(state);
            var hash = SHA256.HashData(Encoding.UTF8.GetBytes(apiKey));
            var credential = oldLookup[$"{AccessKeyPurposes.Control}:{Convert.ToHexString(hash)}"];
            return Task.FromResult<AccessKeySummary?>(new AccessKeySummary(credential.Id, credential.Name,
                credential.Purpose, credential.Scopes.ToArray(), credential.CreatedAt, false));
        }

        var context = new DefaultHttpContext();
        context.Request.Method = "GET";
        context.Request.Path = "/api/metrics/snapshot";
        context.Request.Headers[TrustedAccessHeaders.KeyHeader] = apiKey;
        var administrator = new UnexpectedAdministratorReader();
        const int count = 10000;
        for (var i = 0; i < 2000; i++)
        {
            _ = owner.AuthenticateAsync(AccessKeyPurposes.Control, apiKey, CancellationToken.None).GetAwaiter().GetResult();
            _ = OldAuthentication().GetAwaiter().GetResult();
            _ = Authorize(context, owner, administrator).GetAwaiter().GetResult();
        }

        var before = GC.GetAllocatedBytesForCurrentThread();
        var started = Stopwatch.GetTimestamp();
        for (var i = 0; i < count; i++)
            _ = OldAuthentication().GetAwaiter().GetResult();
        var oldElapsed = Stopwatch.GetElapsedTime(started);
        var oldBytes = GC.GetAllocatedBytesForCurrentThread() - before;

        before = GC.GetAllocatedBytesForCurrentThread();
        started = Stopwatch.GetTimestamp();
        for (var i = 0; i < count; i++)
            _ = owner.AuthenticateAsync(AccessKeyPurposes.Control, apiKey, CancellationToken.None).GetAwaiter().GetResult();
        var elapsed = Stopwatch.GetElapsedTime(started);
        var bytes = GC.GetAllocatedBytesForCurrentThread() - before;

        before = GC.GetAllocatedBytesForCurrentThread();
        started = Stopwatch.GetTimestamp();
        for (var i = 0; i < count; i++)
            _ = Authorize(context, owner, administrator).GetAwaiter().GetResult();
        var policyElapsed = Stopwatch.GetElapsedTime(started);
        var policyBytes = GC.GetAllocatedBytesForCurrentThread() - before;

        output.WriteLine($"keys={keyCount}; calls={count}; source comparator={oldBytes / (double)count:F1} B/op, {oldElapsed.TotalNanoseconds / count:F1} ns/op; owner={bytes / (double)count:F1} B/op, {elapsed.TotalNanoseconds / count:F1} ns/op; HTTP requirement+authorization={policyBytes / (double)count:F1} B/op, {policyElapsed.TotalNanoseconds / count:F1} ns/op");
        Assert.Same(summary, await owner.AuthenticateAsync(AccessKeyPurposes.Control, apiKey, CancellationToken.None));
        Assert.True(bytes <= count * 16L, $"Warm owner authentication allocated {bytes} bytes for {count} calls.");
        Assert.True(policyBytes <= count * 256L, $"Warm policy authentication allocated {policyBytes} bytes for {count} calls.");
        Assert.True((await Authorize(context, owner, administrator)).Allowed);
    }

    private static ValueTask<TrustedAccessAuthorizationDecision> Authorize(
        HttpContext context, ITrustedAccessKeyService owner, ILoopbackApiAdministratorReader administrator)
    {
        var requirement = TrustedAccessHttpPolicy.GetRequirement(context.Request);
        return TrustedAccessHttpPolicy.AuthorizeAsync(context, owner, administrator, requirement.RequiredScopes,
            requirement.AllowKeylessLocalWindowsCaller, requirement.RequireKeyless, CancellationToken.None);
    }

    private sealed class UnexpectedAdministratorReader : ILoopbackApiAdministratorReader
    {
        public bool IsAdministrator(HttpContext context) => throw new InvalidOperationException("Read key must not require elevation.");
    }
}
