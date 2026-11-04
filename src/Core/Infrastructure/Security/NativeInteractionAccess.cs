using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace ResourceManager.App.Infrastructure.Security;

// Admission/read consumers wait for the integrated privilege baseline. The
// operation-only GPU helper also serves the existing invocation host. This
// bridge has no registry, defaults or identity reader; actual caller facts come
// from the existing platform attester, never a deserialized request.
internal static unsafe partial class NativeInteractionAccess
{
    internal const uint AbiVersion = 0x0001_0002;
    internal enum Requirement : uint
    {
        Anonymous = 0, NormalRead = 1, NormalAction = 2,
        Administrator = 3, PerSelector = 4, MissingPolicy = 5
    }

    internal enum CallerLevel : uint { Unidentified = 0, Normal = 1, Administrator = 2 }
    internal enum Disposition : uint { Rejected = 0, Allowed = 1, CheckSelectors = 2, CheckProcessPermission = 3 }
    internal enum Reason : uint
    {
        None = 0, LoopbackOriginRequired = 1, LocalWindowsCallerRequired = 2,
        AdministratorRequired = 3, AccessPolicyMissing = 4, SourceUnavailable = 5
    }
    internal enum GpuOperation : uint { Invalid = 0, Resolve = 1, Report = 2 }
    // Supplied by the source owner. OrdinaryData excludes process details;
    // ProcessEnumeration is name/PID/session and is not desktop-session-scoped.
    internal enum ReadKind : uint { OrdinaryData = 0, DesktopFact = 1, ProcessEnumeration = 2, ProcessDetail = 3 }
    internal enum ProcessReadUse : uint { SingleOutput = 0, LiveMapping = 1 }
    internal enum EvidenceExtent : uint { SingleQuery = 0, HeldObject = 1 }
    internal enum NativeErrorKind : uint { None = 0, Win32 = 1, NtStatus = 2 }
    internal enum ProcessPermissionStatus : uint
    {
        NotProven = 0, Granted = 1, CallerUnverified = 2, CallerChanged = 3, ScopeEnded = 4,
        ImpersonationUnavailable = 5, AccessDenied = 6, TargetChanged = 7, ApiUnsupported = 8,
        QueryFailed = 9, PartialCopy = 10, InvalidResult = 11, DependencyUnavailable = 12,
        SourceMismatch = 13, LifetimeUnproven = 14
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct ProcessInstance
    {
        internal int ProcessId;
        internal long CreationFileTime100Ns; // ProcessInstanceKey.StartKey, not UTC ticks.
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct ProcessFact
    {
        internal ProcessInstance Target;
        // Borrowed original versioned source binding, exact API/reading-policy
        // declaration, and optional token/file object+version dependency identity.
        internal nint SourceIdentity, FieldIdentity, DependencyIdentity;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct ProcessEvidence
    {
        internal ProcessPermissionStatus Status;
        internal EvidenceExtent Extent;
        internal NativeErrorKind ErrorKind;
        internal uint NativeError;
        internal int CallerProcessId;
        internal long CallerProcessCreatedUtcTicks;
        internal nint CallerScope, Capability;
        internal ProcessFact Fact;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct ProcessReadDecision
    {
        internal Decision Access;
        internal ProcessPermissionStatus Status;
        internal NativeErrorKind ErrorKind;
        internal uint NativeError;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct Decision
    {
        internal Disposition Disposition;
        internal Reason Reason;
        internal CallerLevel Level;
        internal readonly bool Allowed => Disposition == NativeInteractionAccess.Disposition.Allowed;
        internal readonly bool RequiresSelectorCheck => Disposition == NativeInteractionAccess.Disposition.CheckSelectors;
        internal readonly bool RequiresProcessPermission => Disposition == NativeInteractionAccess.Disposition.CheckProcessPermission;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct Request
    {
        internal uint AbiVersion, StructSize, Requirement;
        internal uint LocalConnection, OriginAllowed, NormalActionsAllowed;
        internal uint CallerVerified, CallerIsAdministrator;
        internal int ProcessId, WindowsSessionId;
        internal long ProcessCreatedUtcTicks;
        internal char* WindowsSid;
        internal nuint WindowsSidLength;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct ReadRequest
    {
        internal uint StructSize, ReadKind, SourceSessionVerified;
        internal int SourceWindowsSessionId;
        internal Request Access;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct ProcessReadRequest
    {
        internal uint StructSize;
        internal ProcessReadUse ReadUse;
        internal nint CallerScope;
        internal ProcessFact Source;
        internal ProcessEvidence* Evidence;
        internal Request Access;
    }

    private static Request CreateRequest(Requirement requirement, bool localConnection, bool originAllowed,
        LoopbackApiCaller? actualCaller, bool normalActionsAllowed, char* sid, nuint sidLength)
        => new()
        {
            AbiVersion = NativeInteractionAccess.AbiVersion, StructSize = (uint)sizeof(Request), Requirement = (uint)requirement,
            LocalConnection = localConnection ? 1U : 0U, OriginAllowed = originAllowed ? 1U : 0U,
            NormalActionsAllowed = normalActionsAllowed ? 1U : 0U,
            CallerVerified = actualCaller is not null ? 1U : 0U,
            CallerIsAdministrator = actualCaller?.IsAdministrator == true ? 1U : 0U,
            ProcessId = actualCaller?.ProcessId ?? 0, WindowsSessionId = actualCaller?.WindowsSessionId ?? 0,
            ProcessCreatedUtcTicks = actualCaller?.ProcessCreatedUtcTicks ?? 0,
            WindowsSid = sid, WindowsSidLength = sidLength
        };

    // One synchronous call, no callback, allocation, GCHandle, retained pointer
    // or await. The original SID is fixed only for this invocation. Platform
    // facts and the explicit setting are supplied in full by the owning policy.
    internal static Decision Evaluate(Requirement requirement, bool localConnection, bool originAllowed,
        LoopbackApiCaller? actualCaller, bool normalActionsAllowed)
    {
        var sid = actualCaller?.WindowsSid;
        fixed (char* pointer = sid)
        {
            var request = CreateRequest(requirement, localConnection, originAllowed, actualCaller,
                normalActionsAllowed, pointer, checked((nuint)(sid?.Length ?? 0)));
            var status = EvaluateNative(in request, out var decision);
            if (status != 0) throw new InvalidOperationException($"Native interaction access ABI rejected its input ({status}).");
            return decision;
        }
    }

    // The source session is the service-verified desktop publisher's login
    // session and is used only for DesktopFact. ProcessDetail never becomes an
    // allowed read from a role/session comparison: RequiresProcessPermission
    // remains non-admitting until the original process-rights contract closes.
    // Domain-editing sessions and request/active-session IDs do not enter this
    // contract. D-grant and output projections use this same result.
    internal static Decision EvaluateRead(Requirement requirement, bool localConnection, bool originAllowed,
        LoopbackApiCaller? actualCaller, bool normalActionsAllowed, ReadKind readKind,
        bool sourceSessionVerified, int sourceWindowsSessionId)
    {
        var sid = actualCaller?.WindowsSid;
        fixed (char* pointer = sid)
        {
            var request = new ReadRequest
            {
                StructSize = (uint)sizeof(ReadRequest), ReadKind = (uint)readKind,
                SourceSessionVerified = sourceSessionVerified ? 1U : 0U,
                SourceWindowsSessionId = sourceWindowsSessionId,
                Access = CreateRequest(requirement, localConnection, originAllowed, actualCaller,
                    normalActionsAllowed, pointer, checked((nuint)(sid?.Length ?? 0)))
            };
            var status = ReadNative(in request, out var decision);
            if (status != 0) throw new InvalidOperationException($"Native interaction read ABI rejected its input ({status}).");
            return decision;
        }
    }

    internal static GpuOperation GetGpuOperation(string operation, int payloadLength, int maximumPayload)
    {
        fixed (char* pointer = operation)
            return GpuOperationNative(pointer, checked((nuint)operation.Length), checked((nuint)payloadLength), checked((nuint)maximumPayload));
    }

    // Invoke only WITHIN the original platform capability borrow and source
    // stable-read borrow. Neither an identity-only caller nor stored past proof
    // metadata can create those borrows. No raw client handle enters this call.
    // Proof queries never sample/publish source Current. Consumers own their
    // scope locally through send/cancel/finally; no lease is retained here.
    internal static ProcessReadDecision EvaluateProcessRead(Requirement requirement, bool localConnection,
        bool originAllowed, LoopbackApiCaller? actualCaller, bool normalActionsAllowed, nint callerScope,
        in ProcessFact source, ProcessReadUse readUse, ProcessEvidence? evidence)
    {
        var sid = actualCaller?.WindowsSid;
        var proof = evidence.GetValueOrDefault();
        fixed (char* pointer = sid)
        {
            var request = new ProcessReadRequest
            {
                StructSize = (uint)sizeof(ProcessReadRequest), ReadUse = readUse,
                CallerScope = callerScope, Source = source, Evidence = evidence.HasValue ? &proof : null,
                Access = CreateRequest(requirement, localConnection, originAllowed, actualCaller,
                    normalActionsAllowed, pointer, checked((nuint)(sid?.Length ?? 0)))
            };
            var status = ProcessReadNative(in request, out var decision);
            if (status != 0) throw new InvalidOperationException($"Native process permission ABI rejected its input ({status}).");
            return decision;
        }
    }

    [LibraryImport("ResourceManager.NativeCore", EntryPoint = "rm_interaction_access_evaluate")]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    private static partial uint EvaluateNative(in Request request, out Decision decision);

    [LibraryImport("ResourceManager.NativeCore", EntryPoint = "rm_interaction_access_read")]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    private static partial uint ReadNative(in ReadRequest request, out Decision decision);

    [LibraryImport("ResourceManager.NativeCore", EntryPoint = "rm_interaction_access_process_read")]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    private static partial uint ProcessReadNative(in ProcessReadRequest request, out ProcessReadDecision decision);

    [LibraryImport("ResourceManager.NativeCore", EntryPoint = "rm_interaction_access_gpu_operation")]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    private static partial GpuOperation GpuOperationNative(char* pointer, nuint length, nuint payloadLength, nuint maximumPayload);
}
