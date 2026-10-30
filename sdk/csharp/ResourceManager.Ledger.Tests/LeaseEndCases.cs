using System.Buffers;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Reflection;
using ResourceManager.Ledger;
using ResourceManager.Ledger.Identity;

internal static class LeaseEndCases
{
    internal static bool NoFaultHooks(string path)
    {
        nint library = NativeLibrary.Load(path);
        try { return !NativeLibrary.TryGetExport(library, "rm_ledger_test_fail_release", out _); }
        finally { NativeLibrary.Free(library); }
    }
    internal static async Task<int> RunAsync(string path, string manifestPath, bool negative)
    {
        using var manifest = JsonDocument.Parse(File.ReadAllBytes(manifestPath));
        JsonElement fields = manifest.RootElement;
        var identity = new LedgerBuildIdentity(fields.GetProperty("tableCount").GetUInt32(), fields.GetProperty("fixedCount").GetUInt32(),
            fields.GetProperty("resourceCapacity").GetUInt32(), fields.GetProperty("partitionCapacity").GetUInt32(),
            fields.GetProperty("indexNodeCount").GetUInt32(), fields.GetProperty("indexHomeCount").GetUInt32(),
            Convert.FromHexString(fields.GetProperty("structureSha256").GetString()!), Convert.FromHexString(fields.GetProperty("policySha256").GetString()!));
        return fields.GetProperty("uidWidthBits").GetInt32() == 256
            ? await Run<LedgerUid256>(path, identity, negative) : await Run<LedgerUid128>(path, identity, negative);
    }

    private static async Task<int> Run<TUid>(string path, LedgerBuildIdentity identity, bool negative)
        where TUid : unmanaged, ILedgerUid<TUid>
    {
        int cases = 0, active = 0, settlements = 0;
        void Check(bool value, string name)
        {
            if (!value) throw new InvalidOperationException("EndContractFailed: " + name);
            cases++;
            Console.WriteLine("PASS end-contract " + name);
        }
        void Settle(LedgerLease<TUid> lease)
        {
            if (lease.TryTakeEndResult(out LedgerEndResult end))
            {
                if (!end.HasEnded) throw new InvalidOperationException("Claimed nonterminal result");
                active--; settlements++;
            }
        }
        using var controls = new Controls(path);
        using var owner = LedgerOwner<TUid>.Open(path, identity);
        TUid Key(byte key) { byte[] bytes = new byte[TUid.ByteLength]; bytes[^1] = key; return TUid.FromBytes(bytes); }
        LedgerLease<TUid> Load(byte key)
        {
            LedgerPushResult<TUid> pushed = owner.Push(LedgerTarget<TUid>.ForTable(0), Key(key), 64);
            Check(pushed.Status == LedgerStatus.NewlyAllocated, "new real region " + key);
            active++;
            return pushed.Lease!;
        }

        // Fail before the actual OS free. The original loading use must still
        // end exactly once; failed content must never become available.
        LedgerLease<TUid> failed = Load(11);
        failed.Span[0] = 0x75;
        controls.Fail(2);
        LedgerEndResult failedResult;
        try { failedResult = await failed.EndUseAsync(); }
        finally { Settle(failed); }
        Check(failedResult.Status == LedgerStatus.EndedCleanupFailed && failed.HasEnded && failed.EndResult == failedResult, "release failure is known ended");
        Check(active == 0 && settlements == 1, "Active settled once despite cleanup failure");
        ulong attempts = controls.Attempts();
        Check(await failed.EndUseAsync() == failedResult, "repeat end returns terminal outcome");
        bool disposeFailure = false;
        try { failed.Dispose(); }
        catch (LedgerEndException e) { disposeFailure = e.HasEnded && e.Result == failedResult; }
        Settle(failed);
        Check(disposeFailure && active == 0 && settlements == 1 && controls.Attempts() == attempts, "terminal cleanup exception never repeats native completion");
        Check(owner.Use(Key(11)).Status == LedgerStatus.NotInMemory, "failed load stays unpublished");
        LedgerTableView pending = owner.GetTableView(0);
        Check(pending.RegisteredBytes == 64 && pending.SystemPageBytes == (ulong)Environment.SystemPageSize, "failed cleanup remains accurately accounted");
        Check(owner.Push(LedgerTarget<TUid>.ForTable(0), Key(11), 64).Status == LedgerStatus.CleanupPending, "push cannot publish pending discard");
        Check(owner.Close(Key(11)) == LedgerStatus.CleanupPending && owner.GetTableView(0) == pending, "cleanup retry failure preserves pages and index");
        Check(owner.Close(Key(11)) == LedgerStatus.Ok && owner.GetTableView(0).SystemPageBytes == 0, "explicit retry frees original pages");
        Check(owner.Use(Key(11)).Status == LedgerStatus.NotInMemory && !failed.TryTakeEndResult(out _), "retired credential has no second settlement");

        // Terminal abandon does not call even a repeatedly failing OS backend.
        LedgerLease<TUid> abandoned = Load(12);
        abandoned.PublishOnDispose();
        controls.Fail(1);
        attempts = controls.Attempts();
        try { Check((await abandoned.AbandonAsync()).Status == LedgerStatus.Abandoned, "explicit terminal abandon"); }
        finally { Settle(abandoned); }
        Check(active == 0 && controls.Attempts() == attempts, "abandon has no fallible page operation");
        Check(owner.Use(Key(12)).Status == LedgerStatus.NotInMemory, "abandon overrides a prior publish request");
        _ = owner.Tick();
        Check(owner.GetTableView(0).SystemPageBytes != 0, "failed Tick attempt retains pages");
        _ = owner.Tick();
        Check(owner.GetTableView(0).SystemPageBytes == 0, "later Tick reclaims abandoned allocation");
        abandoned.Dispose(); Settle(abandoned);
        Check(active == 0 && settlements == 2, "abandon and later dispose settle once");

        // Even if adapter cleanup throws after the SDK boundary, the finally
        // can take the confirmed outcome once and must never complete again.
        LedgerLease<TUid> published = Load(13);
        published.PublishOnDispose();
        bool afterEndFailure = false;
        try
        {
            await published.EndUseAsync();
            throw new IOException("adapter cleanup failed after end");
        }
        catch (IOException) { afterEndFailure = true; }
        finally { Settle(published); }
        await published.DisposeAsync(); Settle(published);
        Check(afterEndFailure && active == 0 && settlements == 3 && published.HasEnded, "after-end adapter cleanup failure preserves one settlement");
        LedgerUseResult<TUid> acquired = owner.Use(Key(13));
        Check(acquired.Status == LedgerStatus.InMemory, "published data remains available");
        LedgerLease<TUid> pinned = acquired.Lease!; active++;
        ValueTask<LedgerEndResult> completion;
        using (MemoryHandle pin = pinned.Memory.Pin())
        {
            completion = pinned.AbandonAsync();
            Check(!completion.IsCompleted && !pinned.HasEnded && !pinned.TryTakeEndResult(out _), "terminal abandon waits for real pin drain");
        }
        try { Check((await completion).Status == LedgerStatus.Abandoned, "pin drain completes abandon"); }
        finally { Settle(pinned); }
        await pinned.DisposeAsync(); Settle(pinned);
        Check(active == 0 && settlements == 4, "pinned end settles once");
        Check(owner.Close(Key(13)) == LedgerStatus.Ok, "normal release after published abandon");

        // White-box invalid-input injection, not a production test hook. Native
        // rejects the forged credential before any resource state is changed.
        // Restoring the original credential exercises supported retry/abandon.
        foreach (bool abandon in new[] { false, true })
        {
            LedgerLease<TUid> retry = Load(abandon ? (byte)15 : (byte)14);
            FieldInfo credentialField = typeof(LedgerLease<TUid>).GetField("_credential", BindingFlags.Instance | BindingFlags.NonPublic)!;
            object original = credentialField.GetValue(retry)!;
            object invalid = Activator.CreateInstance(original.GetType())!;
            const BindingFlags memberFields = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
            foreach (FieldInfo field in original.GetType().GetFields(memberFields)) field.SetValue(invalid, field.GetValue(original));
            original.GetType().GetField("UseGeneration", memberFields)!.SetValue(invalid, 0UL);
            credentialField.SetValue(retry, invalid);
            bool beforeCommit = false;
            try { await retry.EndUseAsync(); }
            catch (LedgerEndException e) { beforeCommit = !e.HasEnded && e.Result.Status == LedgerStatus.InputError; }
            finally { credentialField.SetValue(retry, original); }
            Check(beforeCommit && !retry.HasEnded && !retry.TryTakeEndResult(out _) && active == 1, "precommit rejection retains exact acquisition");
            Check(owner.Close(Key(abandon ? (byte)15 : (byte)14)) == LedgerStatus.InUse, "precommit rejection cannot pretend Active ended");
            try
            {
                LedgerEndResult result = abandon ? await retry.AbandonAsync() : await retry.EndUseAsync();
                Check(result.Status == (abandon ? LedgerStatus.Abandoned : LedgerStatus.Completed), "same-scope corrected credential " + (abandon ? "abandon" : "retry"));
            }
            finally { Settle(retry); }
            await retry.DisposeAsync(); Settle(retry);
            Check(active == 0, "retry or abandon settles exactly once");
            if (abandon) _ = owner.Tick();
        }
        controls.Fail(0);

        // Every terminal status is final in managed code. Native would accept a
        // duplicate end of A while B and C still hold the same allocation, so a
        // repeated end reaching Native would make B's end or the InUse close fail.
        LedgerLease<TUid> shared = Load(16);
        shared.PublishOnDispose();
        try { await shared.EndUseAsync(); }
        finally { Settle(shared); }
        foreach (bool abandonFirst in new[] { false, true })
        {
            LedgerLease<TUid> first = owner.Use(Key(16)).Lease!; active++;
            LedgerLease<TUid> second = owner.Use(Key(16)).Lease!; active++;
            LedgerLease<TUid> third = owner.Use(Key(16)).Lease!; active++;
            LedgerStatus expected = abandonFirst ? LedgerStatus.Abandoned : LedgerStatus.Completed;
            LedgerEndResult ended;
            try { ended = abandonFirst ? await first.AbandonAsync() : await first.EndUseAsync(); }
            finally { Settle(first); }
            first.Dispose();
            await first.DisposeAsync();
            Check(ended.Status == expected && await first.EndUseAsync() == ended && await first.AbandonAsync() == ended &&
                !first.TryTakeEndResult(out _), "repeated end of terminal " + expected + " returns the stored result");
            try { Check((await second.EndUseAsync()).Status == LedgerStatus.Completed, "other holder ends after repeated " + expected); }
            finally { Settle(second); }
            Check(owner.Close(Key(16)) == LedgerStatus.InUse, "last holder still holds after repeated " + expected);
            try { Check((await third.EndUseAsync()).Status == LedgerStatus.Completed, "last holder ends after repeated " + expected); }
            finally { Settle(third); }
            Check(active == 0, "repeated " + expected + " settles each holder once");
        }
        Check(owner.Close(Key(16)) == LedgerStatus.Ok, "shared allocation closes after all holders ended");
        Check(active == (negative ? 1 : 0), "real final Active assertion");
        return cases;
    }

    private sealed unsafe class Controls : IDisposable
    {
        private readonly nint _library;
        private readonly delegate* unmanaged[Cdecl]<uint, void> _fail;
        private readonly delegate* unmanaged[Cdecl]<ulong> _attempts;
        internal Controls(string path)
        {
            _library = NativeLibrary.Load(path);
            _fail = (delegate* unmanaged[Cdecl]<uint, void>)NativeLibrary.GetExport(_library, "rm_ledger_test_fail_release");
            _attempts = (delegate* unmanaged[Cdecl]<ulong>)NativeLibrary.GetExport(_library, "rm_ledger_test_release_attempts");
        }
        internal void Fail(uint count) => _fail(count);
        internal ulong Attempts() => _attempts();
        public void Dispose() => NativeLibrary.Free(_library);
    }
}
